using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Model;
using SharpCompress.Archives;

namespace CommandCenter.Services
{
    public enum UpdateCheckStatus
    {
        UpToDate,
        UpdateAvailable,
        // The published .7z is an OLDER build than the one running (e.g. a dev build) - never offered.
        LocalIsNewer
    }

    public sealed record UpdateCheckResult(
        UpdateCheckStatus Status,
        RemoteUpdatePackage Package,
        string? RemoteBuildCode,
        DateTime? RemoteBuildDate,
        string? ArchivePath,
        string? RemoteAssemblySha256,
        // "Added"/"Updates" for the published build, from the page's Build Release / Version Updates
        // section. Null when the page has none (or only N/A) or couldn't be read.
        ReleaseNotes? Notes = null);

    /// <summary>
    /// Check / download / install for Maple Central self-updates.
    ///
    /// "Is it newer?" is decided in this order:
    ///  1. installed_update.json says this exact upload (attachment id + version + upload time) is
    ///     what's installed, and MapleCentral.dll still hashes the same -> up to date, no download.
    ///  2. Otherwise the .7z is downloaded and its MapleCentral.dll is compared to the running one.
    ///     Same bytes -> up to date (and the record is written so step 1 catches it next time).
    ///  3. Different bytes -> the BuildDate stamped into the .7z's dll (falls back to the upload
    ///     time) is compared with this exe's BuildDate. Newer -> update offered; older -> LocalIsNewer.
    ///
    /// Installing extracts into a staging folder, writes a small PowerShell script that waits for
    /// this process to exit, robocopies the new files over the install folder (no /PURGE, so user-
    /// added Servers\*.json files survive), and relaunches the app.
    /// </summary>
    public sealed class AppUpdateService
    {
        private readonly ConfluenceSession _confluence;

        public AppUpdateService(ConfluenceSession confluence) => _confluence = confluence;

        public static string InstallFolder => AppContext.BaseDirectory.TrimEnd('\\', '/');

        public static string LocalAssemblyPath => Path.Combine(InstallFolder, UpdateDefaults.MainAssemblyFileName);

        /// <summary>Running out of a Visual Studio bin\Debug or bin\Release folder.</summary>
        public static bool IsDevBuildFolder =>
            InstallFolder.Contains(@"\bin\Debug", StringComparison.OrdinalIgnoreCase) ||
            InstallFolder.Contains(@"\bin\Release", StringComparison.OrdinalIgnoreCase);

        public async Task<UpdateCheckResult> CheckAsync(string baseUrl, string pageId,
            IProgress<string>? status, IProgress<double>? downloadPercent, CancellationToken ct)
        {
            status?.Report("Checking Confluence for the latest package...");
            var package = await _confluence.GetLatestPackageAsync(baseUrl, pageId, ct);

            var localHash = await Task.Run(() => HashFile(LocalAssemblyPath), ct);
            var record = InstalledUpdateRecord.Load(UpdateDefaults.InstalledRecordPath);
            if (record != null && record.PackageIdentity == package.Identity && record.MainAssemblySha256 == localHash)
            {
                var upToDateNotes = await TryGetReleaseNotesAsync(baseUrl, pageId, record.BuildCode, status, ct);
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, package, record.BuildCode, null, null, localHash, upToDateNotes);
            }

            var archivePath = Path.Combine(UpdateDefaults.DownloadsFolder, CacheFileNameFor(package));
            if (!File.Exists(archivePath) || (package.SizeBytes is long size && new FileInfo(archivePath).Length != size))
            {
                status?.Report($"Downloading {package.FileName} to compare...");
                var progress = new Progress<(long received, long? total)>(p =>
                {
                    if (p.total is > 0) downloadPercent?.Report(p.received * 100.0 / p.total.Value);
                });
                await _confluence.DownloadAsync(baseUrl, package, archivePath, progress, ct);
            }

            status?.Report("Comparing package contents with this install...");
            var (remoteHash, remoteCode, remoteDate) = await Task.Run(() => InspectArchive(archivePath), ct);

            // Notes for the PUBLISHED build are read on every check (not just when an update is
            // offered) - Settings' Updates card always shows what the latest release added/changed.
            var notes = await TryGetReleaseNotesAsync(baseUrl, pageId, remoteCode, status, ct);

            if (remoteHash == localHash)
            {
                SaveRecord(UpdateDefaults.InstalledRecordPath, package, remoteCode, localHash);
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, package, remoteCode, remoteDate, archivePath, remoteHash, notes);
            }

            var remoteWhen = remoteDate ?? package.Uploaded?.LocalDateTime;
            var localWhen = AppVersionInfo.Current.BuildDate;
            var status2 = remoteWhen != null && localWhen != null && remoteWhen < localWhen
                ? UpdateCheckStatus.LocalIsNewer
                : UpdateCheckStatus.UpdateAvailable;

            return new UpdateCheckResult(status2, package, remoteCode, remoteDate, archivePath, remoteHash, notes);
        }

        // Best-effort: missing/odd notes must never block offering the update itself.
        private async Task<ReleaseNotes?> TryGetReleaseNotesAsync(string baseUrl, string pageId, string? buildCode,
            IProgress<string>? status, CancellationToken ct)
        {
            try
            {
                status?.Report("Reading release notes...");
                var html = await _confluence.GetPageBodyHtmlAsync(baseUrl, pageId, ct);
                return ReleaseNotesParser.Parse(html, buildCode);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extracts the package to staging, writes the updater script, records the pending install
        /// and starts the script. The caller must shut the app down right after this returns true.
        /// </summary>
        public async Task<bool> PrepareAndLaunchInstallAsync(UpdateCheckResult result, IProgress<string>? status, CancellationToken ct)
        {
            if (result.ArchivePath == null || !File.Exists(result.ArchivePath))
                throw new InvalidOperationException("The update package isn't downloaded - run Check for Updates again.");

            status?.Report("Extracting update...");
            var staging = UpdateDefaults.StagingFolder;
            await Task.Run(() => ExtractAppRoot(result.ArchivePath, staging, ct), ct);

            if (!File.Exists(Path.Combine(staging, UpdateDefaults.MainExeFileName)))
                throw new InvalidOperationException($"The package doesn't contain {UpdateDefaults.MainExeFileName}.");

            SaveRecord(UpdateDefaults.PendingRecordPath, result.Package, result.RemoteBuildCode, result.RemoteAssemblySha256 ?? string.Empty);

            WriteUpdaterScript(staging, InstallFolder, Environment.ProcessId);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{UpdateDefaults.UpdaterScriptPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            // Installed somewhere this (unelevated) process can't write, e.g. Program Files - ask
            // for elevation just for the copy step.
            if (!CanWriteTo(InstallFolder) && !App.IsRunningElevated())
            {
                psi.Verb = "runas";
            }

            status?.Report("Restarting to finish the update...");
            try
            {
                Process.Start(psi);
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // UAC declined - nothing has been touched yet.
                TryDelete(UpdateDefaults.PendingRecordPath);
                return false;
            }
        }

        /// <summary>
        /// Called at startup: if an update was just applied and the new MapleCentral.dll is really in
        /// place, the pending record becomes the installed record. Also clears the staging folder.
        /// </summary>
        public static void FinalizePendingInstall()
        {
            try
            {
                var pending = InstalledUpdateRecord.Load(UpdateDefaults.PendingRecordPath);
                if (pending == null) return;

                if (!string.IsNullOrEmpty(pending.MainAssemblySha256) && HashFile(LocalAssemblyPath) == pending.MainAssemblySha256)
                {
                    pending.RecordedAt = DateTime.Now;
                    pending.Save(UpdateDefaults.InstalledRecordPath);
                }

                TryDelete(UpdateDefaults.PendingRecordPath);
                if (Directory.Exists(UpdateDefaults.StagingFolder)) Directory.Delete(UpdateDefaults.StagingFolder, recursive: true);
            }
            catch
            {
                // Best-effort housekeeping - never block startup over it.
            }
        }

        // ---------------------------------------------------------------- archive helpers

        // Finds MapleCentral.dll inside the archive; whatever folder it sits in is the app root
        // (handles a .7z that wraps everything in a "MapleCentral\" folder as well as a flat one).
        private static string FindAppRootPrefix(IArchive archive)
        {
            var dll = archive.Entries.FirstOrDefault(e => !e.IsDirectory && e.Key != null &&
                Path.GetFileName(e.Key.Replace('\\', '/')).Equals(UpdateDefaults.MainAssemblyFileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"The package doesn't contain {UpdateDefaults.MainAssemblyFileName}.");

            var key = dll.Key!.Replace('\\', '/');
            return key.Substring(0, key.Length - UpdateDefaults.MainAssemblyFileName.Length);
        }

        private static (string hash, string? buildCode, DateTime? buildDate) InspectArchive(string archivePath)
        {
            using var archive = ArchiveFactory.Open(archivePath);
            var prefix = FindAppRootPrefix(archive);
            var entry = archive.Entries.First(e => !e.IsDirectory && e.Key != null &&
                e.Key.Replace('\\', '/').Equals(prefix + UpdateDefaults.MainAssemblyFileName, StringComparison.OrdinalIgnoreCase));

            using var buffer = new MemoryStream();
            using (var s = entry.OpenEntryStream()) s.CopyTo(buffer);
            var bytes = buffer.ToArray();

            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var (code, date) = ReadVersionStamp(bytes);
            return (hash, code, date);
        }

        // Reads the BuildCode/BuildDate [AssemblyMetadata] values out of a dll WITHOUT loading it.
        private static (string? code, DateTime? date) ReadVersionStamp(byte[] dll)
        {
            try
            {
                using var pe = new PEReader(new MemoryStream(dll));
                var md = pe.GetMetadataReader();
                string? code = null, date = null;

                foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
                {
                    var attr = md.GetCustomAttribute(handle);
                    if (attr.Constructor.Kind != HandleKind.MemberReference) continue;

                    var ctor = md.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                    if (ctor.Parent.Kind != HandleKind.TypeReference) continue;
                    var type = md.GetTypeReference((TypeReferenceHandle)ctor.Parent);
                    if (md.GetString(type.Name) != "AssemblyMetadataAttribute") continue;

                    var blob = md.GetBlobReader(attr.Value);
                    if (blob.ReadUInt16() != 1) continue; // custom attribute prolog
                    var key = blob.ReadSerializedString();
                    var value = blob.ReadSerializedString();
                    if (key == "BuildCode") code = value;
                    else if (key == "BuildDate") date = value;
                }

                return (code, AppVersionInfo.ParseBuildDate(date));
            }
            catch
            {
                return (null, null); // older build with no stamp - fall back to upload date
            }
        }

        private static void ExtractAppRoot(string archivePath, string staging, CancellationToken ct)
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            var stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

            using var archive = ArchiveFactory.Open(archivePath);
            var prefix = FindAppRootPrefix(archive);

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsDirectory || entry.Key == null) continue;

                var key = entry.Key.Replace('\\', '/');
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = key.Substring(prefix.Length);
                var dest = Path.GetFullPath(Path.Combine(staging, relative));
                if (!dest.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase)) continue; // path-traversal guard

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var input = entry.OpenEntryStream();
                using var output = File.Create(dest);
                input.CopyTo(output);
            }
        }

        // ---------------------------------------------------------------- updater script

        private static void WriteUpdaterScript(string staging, string installFolder, int pid)
        {
            static string Q(string s) => "'" + s.Replace("'", "''") + "'";

            var script = new StringBuilder()
                .AppendLine("$ErrorActionPreference = 'Continue'")
                .AppendLine($"$log = {Q(UpdateDefaults.UpdaterLogPath)}")
                .AppendLine("function Log($m) { Add-Content -Path $log -Value (\"[{0}] {1}\" -f (Get-Date -Format s), $m) }")
                .AppendLine($"Log 'Waiting for Maple Central (PID {pid}) to close...'")
                .AppendLine($"try {{ Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue }} catch {{ }}")
                .AppendLine("Start-Sleep -Seconds 1")
                .AppendLine($"Log ('Copying ' + {Q(staging)} + ' -> ' + {Q(installFolder)})")
                .AppendLine($"robocopy {Q(staging)} {Q(installFolder)} /E /R:5 /W:2 /NP /NFL /NDL /NJH /NJS | Out-Null")
                .AppendLine("$code = $LASTEXITCODE")
                .AppendLine("if ($code -ge 8) { Log \"robocopy failed with exit code $code - install folder left as-is where possible.\" }")
                .AppendLine("else { Log \"Copy finished (robocopy $code).\" }")
                .AppendLine($"Start-Process -FilePath {Q(Path.Combine(installFolder, UpdateDefaults.MainExeFileName))} -WorkingDirectory {Q(installFolder)}")
                .AppendLine("Log 'Relaunched.'")
                .ToString();

            Directory.CreateDirectory(UpdateDefaults.UpdatesFolder);
            File.WriteAllText(UpdateDefaults.UpdaterScriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        // ---------------------------------------------------------------- misc

        private static void SaveRecord(string path, RemoteUpdatePackage package, string? buildCode, string hash)
        {
            try
            {
                new InstalledUpdateRecord
                {
                    PackageIdentity = package.Identity,
                    FileName = package.FileName,
                    BuildCode = buildCode ?? string.Empty,
                    MainAssemblySha256 = hash
                }.Save(path);
            }
            catch
            {
                // Not fatal - the next check just re-downloads to compare.
            }
        }

        private static string CacheFileNameFor(RemoteUpdatePackage p)
        {
            var safeName = string.Concat(p.FileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var stamp = p.AttachmentVersion?.ToString() ?? p.Uploaded?.UtcDateTime.ToString("yyyyMMddHHmmss") ?? "latest";
            return $"{Path.GetFileNameWithoutExtension(safeName)} (v{stamp}){Path.GetExtension(safeName)}";
        }

        private static string HashFile(string path)
        {
            if (!File.Exists(path)) return string.Empty;
            using var s = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(s));
        }

        private static bool CanWriteTo(string folder)
        {
            try
            {
                var probe = Path.Combine(folder, $".update-probe-{Guid.NewGuid():N}");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
