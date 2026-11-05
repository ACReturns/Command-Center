using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace CommandCenter.Model
{
    /// <summary>
    /// Where updates come from and where the updater keeps its working files. The Confluence page
    /// is the "Maple Central" page in the QA space; the newest .7z attached to it (or linked from
    /// it) is treated as the latest release. Page id/base URL can be overridden in settings.json
    /// (AppSettings.UpdatePageId / UpdateConfluenceBaseUrl) without a rebuild.
    /// </summary>
    public static class UpdateDefaults
    {
        public const string ConfluenceBaseUrl = "https://confluence.nexon.com";
        public const string PageId = "3030680595";

        public static string PageUrlFor(string baseUrl, string pageId) =>
            $"{baseUrl.TrimEnd('/')}/pages/viewpage.action?pageId={Uri.EscapeDataString(pageId)}";

        // Name of the managed main assembly inside the install folder / the .7z. Used both to find
        // the app's root folder inside the archive and to compare "is this the same build?".
        public const string MainAssemblyFileName = "MapleCentral.dll";
        public const string MainExeFileName = "MapleCentral.exe";

        // %LocalAppData%\CommandCenter\Updates - downloaded archives, the staging folder the new
        // build is extracted into, the generated updater script and its log.
        public static string UpdatesFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommandCenter", "Updates");

        public static string DownloadsFolder => Path.Combine(UpdatesFolder, "Downloads");
        public static string StagingFolder => Path.Combine(UpdatesFolder, "Staging");
        public static string UpdaterScriptPath => Path.Combine(UpdatesFolder, "apply-update.ps1");
        public static string UpdaterLogPath => Path.Combine(UpdatesFolder, "update.log");

        // Which Confluence attachment the current install came from - see InstalledUpdateRecord.
        public static string InstalledRecordPath => Path.Combine(AppPaths.AppDataFolder, "installed_update.json");

        // Written right before the app hands off to the updater script; promoted to the installed
        // record on the next startup once the new MapleCentral.dll is confirmed in place.
        public static string PendingRecordPath => Path.Combine(AppPaths.AppDataFolder, "pending_update.json");
    }

    /// <summary>
    /// The version stamp compiled into this exe (see the AssemblyMetadata items in
    /// CommandCenter.csproj): BuildCode is "ADW-MC-##" and BuildDate is the date/time it was built.
    /// </summary>
    public sealed record AppVersionInfo(string BuildCode, DateTime? BuildDate)
    {
        public static AppVersionInfo Current { get; } = Read(Assembly.GetExecutingAssembly());

        public string Display => BuildDate is { } d ? $"{BuildCode}  ·  {d:yyyy.MM.dd}" : BuildCode;

        private static AppVersionInfo Read(Assembly assembly)
        {
            var meta = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
            string code = meta.FirstOrDefault(m => m.Key == "BuildCode")?.Value ?? "ADW-MC-??";
            return new AppVersionInfo(code, ParseBuildDate(meta.FirstOrDefault(m => m.Key == "BuildDate")?.Value));
        }

        public static DateTime? ParseBuildDate(string? value) =>
            DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var d) ? d : null;
    }

    /// <summary>The newest update package found on the Confluence page.</summary>
    public sealed record RemoteUpdatePackage(
        string Id,              // attachment id (or the link URL when the .7z is linked, not attached)
        string FileName,
        string DownloadUrl,     // absolute
        DateTimeOffset? Uploaded,
        int? AttachmentVersion,
        long? SizeBytes)
    {
        // Stable key for "is this the exact same upload?" - a re-upload of the same file name bumps
        // the attachment version, so name alone isn't enough.
        public string Identity => $"{Id}|v{AttachmentVersion?.ToString() ?? "-"}|{Uploaded?.UtcDateTime:o}";
    }

    /// <summary>
    /// Remembers which package the current install came from, plus the hash of its main assembly so
    /// a manual reinstall/rebuild in between is detected. Lets "Check for Updates" answer "you're up
    /// to date" without downloading the .7z again every time.
    /// </summary>
    public sealed class InstalledUpdateRecord
    {
        public string PackageIdentity { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string BuildCode { get; set; } = string.Empty;
        public string MainAssemblySha256 { get; set; } = string.Empty;
        public DateTime RecordedAt { get; set; } = DateTime.Now;

        public static InstalledUpdateRecord? Load(string path)
        {
            try
            {
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<InstalledUpdateRecord>(File.ReadAllText(path))
                    : null;
            }
            catch
            {
                return null; // corrupt/old file - just behaves like "no record"
            }
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
