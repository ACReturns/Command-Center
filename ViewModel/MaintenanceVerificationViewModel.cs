using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CommandCenter.Model;
using CommandCenter.Services;
using CommandCenter.View;

namespace CommandCenter.ViewModel
{
    /// <summary>A build tab the user has set up (BuildSection with a Build Path) - the "installed version" picker.</summary>
    public sealed record InstalledBuildOption(Guid TabId, string Title, string Version, string BuildPath)
    {
        public string Display => string.IsNullOrWhiteSpace(Version) ? $"{Title}  (no version set)" : $"{Title}  -  {Version}";
    }

    /// <summary>One sheet row plus its local download state.</summary>
    public sealed class ChecksumRowViewModel : INotifyPropertyChanged
    {
        private string _installedOn = string.Empty;

        public ChecksumRowViewModel(ChecksumSheetRow row)
        {
            Row = row;
            LocalManifestPath = MaintenanceDefaults.ManifestPathFor(row.Tag, row.Version);
        }

        public ChecksumSheetRow Row { get; }
        public string Version => Row.Version;
        public string Tag => Row.Tag;
        public string Family => Row.Family;
        public bool HasChecksumLink => Row.HasChecksumLink;
        public string LocalManifestPath { get; }
        public bool IsDownloaded => File.Exists(LocalManifestPath);

        public string ChecksumStatus =>
            !HasChecksumLink ? "No checksum posted"
            : IsDownloaded ? $"Downloaded {File.GetLastWriteTime(LocalManifestPath):g}"
            : "Not downloaded";

        /// <summary>Titles of build tabs whose version matches this row.</summary>
        public string InstalledOn
        {
            get => _installedOn;
            set
            {
                if (_installedOn == value) return;
                _installedOn = value;
                OnPropertyChanged();
            }
        }

        public void RefreshState()
        {
            OnPropertyChanged(nameof(IsDownloaded));
            OnPropertyChanged(nameof(ChecksumStatus));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Permanent singleton tab: pulls the version sheet (Ver / Tag / Checksum), downloads a row's .md5
    /// into %AppData%\CommandCenter\Checksums\&lt;Tag&gt;\, and verifies an installed build tab against it.
    /// </summary>
    public sealed class MaintenanceVerificationViewModel : INotifyPropertyChanged
    {
        private readonly AppSettings _appSettings;
        private readonly SettingsService _settingsService;
        private readonly Func<IEnumerable<TabSettings>> _tabsProvider;
        private readonly GoogleWebSession _session = GoogleWebSession.Shared;
        private readonly StringBuilder _log = new();

        private CancellationTokenSource? _cts;
        private DispatcherTimer? _statusClearTimer;
        private bool _initialized;

        private ChecksumRowViewModel? _selectedRow;
        private InstalledBuildOption? _selectedBuild;
        private bool _isBusy;
        private bool _isProgressIndeterminate;
        private double _progressPercent;
        private string _statusText = string.Empty;
        private string _sheetStatusText = "Sheet not loaded yet.";
        private bool _needsSignIn;
        private string _summaryText = string.Empty;
        private string _summaryState = "None"; // None | Pass | Fail | Cancelled
        private string _logText = string.Empty;
        private bool _newestFirst;

        public MaintenanceVerificationViewModel(AppSettings appSettings, SettingsService settingsService, Func<IEnumerable<TabSettings>> tabsProvider)
        {
            _appSettings = appSettings;
            _settingsService = settingsService;
            _tabsProvider = tabsProvider;
            _newestFirst = appSettings.MaintenanceNewestFirst;

            RowsView = CollectionViewSource.GetDefaultView(Rows);
            RowsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChecksumRowViewModel.Family)));

            RefreshSheetCommand = new MaintenanceCommand(_ => RefreshSheetAsync(), _ => !IsBusy);
            SignInCommand = new MaintenanceCommand(_ => SignInAsync(), _ => !IsBusy);
            DownloadChecksumCommand = new MaintenanceCommand(_ => DownloadSelectedAsync(),
                _ => !IsBusy && SelectedRow?.HasChecksumLink == true);
            VerifyCommand = new MaintenanceCommand(_ => VerifySelectedAsync(),
                _ => !IsBusy && SelectedRow?.IsDownloaded == true && SelectedBuild != null);
            CancelCommand = new MaintenanceCommand(_ =>
            {
                _cts?.Cancel();
                return Task.CompletedTask;
            }, _ => IsBusy);
            OpenChecksumsFolderCommand = new MaintenanceCommand(_ =>
            {
                OpenChecksumsFolder();
                return Task.CompletedTask;
            });
        }

        // ---------- Bindable state ----------

        public ObservableCollection<ChecksumRowViewModel> Rows { get; } = new();
        public ICollectionView RowsView { get; }
        public ObservableCollection<InstalledBuildOption> InstalledBuilds { get; } = new();
        public ObservableCollection<ChecksumFileResult> Results { get; } = new();
        public ObservableCollection<string> Extras { get; } = new();

        public string ResultsHeader => Results.Count == 0 ? "Results" : $"Results ({Results.Count:N0})";
        public string ExtrasHeader => Extras.Count == 0 ? "Extras" : $"Extras ({Extras.Count:N0})";
        public bool HasInstalledBuilds => InstalledBuilds.Count > 0;

        public ChecksumRowViewModel? SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (!Set(ref _selectedRow, value)) return;
                OnPropertyChanged(nameof(DownloadButtonText));
                OnPropertyChanged(nameof(SelectedRowDisplay));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public InstalledBuildOption? SelectedBuild
        {
            get => _selectedBuild;
            set
            {
                if (!Set(ref _selectedBuild, value)) return;
                AutoSelectRowForBuild();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string SelectedRowDisplay =>
            SelectedRow == null ? "Pick a version in the table above." : $"{SelectedRow.Version}   {SelectedRow.Tag}";

        public string DownloadButtonText => SelectedRow?.IsDownloaded == true ? "Re-download .md5" : "Download .md5";

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsProgressIndeterminate { get => _isProgressIndeterminate; private set => Set(ref _isProgressIndeterminate, value); }
        public double ProgressPercent { get => _progressPercent; private set => Set(ref _progressPercent, value); }
        public string SheetStatusText { get => _sheetStatusText; private set => Set(ref _sheetStatusText, value); }
        public bool NeedsSignIn { get => _needsSignIn; private set => Set(ref _needsSignIn, value); }

        /// <summary>
        /// Which end of the release-version groups (v268, v269, ...) sorts to the top of the table -
        /// true puts the newest family first, false (the original behavior) leaves them in sheet
        /// order (oldest first). Bound to a checkbox on the tab itself; flipping it re-orders the
        /// already-loaded rows in place (ApplyRowOrder) and saves immediately, same "changes to this
        /// tab persist on their own" pattern Server Status uses for its expand/collapse state
        /// (ServerStatusViewModel.SaveExpandedState).
        /// </summary>
        public bool NewestFirst
        {
            get => _newestFirst;
            set
            {
                if (!Set(ref _newestFirst, value)) return;
                _appSettings.MaintenanceNewestFirst = value;
                _settingsService.Save(_appSettings);
                ApplyRowOrder();
            }
        }

        public string SummaryText { get => _summaryText; private set => Set(ref _summaryText, value); }
        public string SummaryState { get => _summaryState; private set => Set(ref _summaryState, value); }
        public string LogText { get => _logText; private set => Set(ref _logText, value); }

        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (Set(ref _statusText, value)) RestartStatusClearTimer();
            }
        }

        public ICommand RefreshSheetCommand { get; }
        public ICommand SignInCommand { get; }
        public ICommand DownloadChecksumCommand { get; }
        public ICommand VerifyCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand OpenChecksumsFolderCommand { get; }

        private string SheetUrl =>
            string.IsNullOrWhiteSpace(_appSettings.MaintenanceSheetUrl)
                ? MaintenanceDefaults.SheetUrl
                : _appSettings.MaintenanceSheetUrl.Trim();

        // ---------- Lifecycle ----------

        /// <summary>Called from the view's Loaded event, i.e. every time the tab is shown.</summary>
        public void OnActivated()
        {
            RefreshInstalledBuilds();
            foreach (var row in Rows) row.RefreshState();
            OnPropertyChanged(nameof(DownloadButtonText));

            if (_initialized) return;
            _initialized = true;

            if (File.Exists(MaintenanceDefaults.SheetCacheFile))
            {
                var count = LoadSheetFromCache();
                SheetStatusText = $"{count} versions (cached {File.GetLastWriteTime(MaintenanceDefaults.SheetCacheFile):g}). Refreshing...";
            }

            _ = RefreshSheetAsync();
        }

        /// <summary>
        /// Rebuilds the installed-build picker from the user's current tab setup. Only tabs currently
        /// checked visible in Settings are offered - a hidden build tab (unchecked "Visible" in the
        /// tab list, [[tab_management]]'s IsVisible) shouldn't show up here even if it still has a
        /// Build Path set, since the user can't actually see or work with that build right now.
        /// </summary>
        public void RefreshInstalledBuilds()
        {
            var previousTabId = _selectedBuild?.TabId;

            var builds = _tabsProvider()
                .Where(t => t.Kind == TabKind.BuildSection && t.IsVisible && !string.IsNullOrWhiteSpace(t.BuildPath))
                .OrderBy(t => t.Order)
                .Select(t => new InstalledBuildOption(t.Id, t.Title ?? string.Empty, t.VersionNumber ?? string.Empty, t.BuildPath!))
                .ToList();

            InstalledBuilds.Clear();
            foreach (var build in builds) InstalledBuilds.Add(build);
            OnPropertyChanged(nameof(HasInstalledBuilds));

            // Restore the previous pick without re-running auto-select (keeps a manual row choice intact).
            _selectedBuild = InstalledBuilds.FirstOrDefault(b => b.TabId == previousTabId);
            OnPropertyChanged(nameof(SelectedBuild));

            UpdateInstalledOn();
            CommandManager.InvalidateRequerySuggested();
        }

        // ---------- Sheet ----------

        private async Task RefreshSheetAsync()
        {
            if (!ChecksumSheetParser.TryParseSheetUrl(SheetUrl, out var sheetId, out var gid))
            {
                SheetStatusText = "The sheet URL in Settings isn't a Google Sheets link.";
                return;
            }

            var ct = Begin("Pulling the version sheet...", indeterminate: true);
            try
            {
                await _session.DownloadAsync(ChecksumSheetParser.CsvExportUrl(sheetId, gid), MaintenanceDefaults.SheetCacheFile, ct);
                var count = LoadSheetFromCache();
                NeedsSignIn = false;
                SheetStatusText = count > 0
                    ? $"{count} versions  -  pulled {DateTime.Now:g}"
                    : "Pulled the sheet but found no version rows. Check the sheet URL / tab (gid) in Settings.";
                StatusText = string.Empty;
            }
            catch (SignInRequiredException)
            {
                NeedsSignIn = true;
                SheetStatusText = "Sign in to Google to pull the sheet.";
            }
            catch (OperationCanceledException)
            {
                SheetStatusText = "Sheet refresh cancelled.";
            }
            catch (Exception ex)
            {
                SheetStatusText = $"Couldn't pull the sheet: {ex.Message}";
            }
            finally
            {
                End();
            }
        }

        private int LoadSheetFromCache()
        {
            List<ChecksumSheetRow> parsed;
            try
            {
                parsed = ChecksumSheetParser.Parse(File.ReadAllText(MaintenanceDefaults.SheetCacheFile, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                SheetStatusText = $"Couldn't read the cached sheet: {ex.Message}";
                return 0;
            }

            var previousTag = SelectedRow?.Tag;
            Rows.Clear();
            foreach (var row in parsed) Rows.Add(new ChecksumRowViewModel(row));
            ApplyRowOrder();

            UpdateInstalledOn();

            SelectedRow = Rows.FirstOrDefault(r => r.Tag == previousTag);
            if (SelectedRow == null) AutoSelectRowForBuild();
            return Rows.Count;
        }

        private async Task SignInAsync()
        {
            // Cheap check first - if the WebView2 profile's cookies are already good (a prior sign-in,
            // or a Google session that hasn't expired), skip the visible sign-in window entirely rather
            // than making the user click through it every time.
            if (await _session.IsSignedInAsync(CancellationToken.None))
            {
                StatusText = "Already signed in to Google.";
            }
            else
            {
                GoogleSignInWindow.Prompt(Application.Current?.MainWindow, SheetUrl);
            }

            await RefreshSheetAsync(); // tells us for sure whether the session works now
        }

        // ---------- Download ----------

        private async Task DownloadSelectedAsync()
        {
            var row = SelectedRow;
            if (row == null || !row.HasChecksumLink) return;

            var ct = Begin($"Downloading .md5 for {row.Version}...", indeterminate: true);
            try
            {
                var fileId = row.Row.DriveFileId;
                if (fileId == null)
                {
                    var md5 = await FindManifestEntryAsync(row.Row.DriveFolderId!, ct);
                    fileId = md5.Id;
                }

                await _session.DownloadAsync(GoogleWebSession.DriveFileDownloadUrl(fileId), row.LocalManifestPath, ct);

                var entryCount = Md5ManifestService.Parse(row.LocalManifestPath).Count;
                if (entryCount == 0)
                {
                    File.Delete(row.LocalManifestPath);
                    throw new InvalidDataException("The downloaded file isn't a checksum list (Drive may have returned a warning page).");
                }

                NeedsSignIn = false;
                StatusText = $"Downloaded .md5 for {row.Version} ({entryCount:N0} files listed).";
            }
            catch (SignInRequiredException)
            {
                NeedsSignIn = true;
                StatusText = "Sign in to Google, then try the download again.";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Download cancelled.";
            }
            catch (Exception ex)
            {
                StatusText = $"Download failed: {ex.Message}";
            }
            finally
            {
                row.RefreshState();
                OnPropertyChanged(nameof(DownloadButtonText));
                End();
            }
        }

        /// <summary>
        /// Looks in <paramref name="folderId"/> for a .md5-looking file. If every entry in that folder
        /// looks like a subfolder rather than a file (e.g. the Checksum link points at a folder holding
        /// "Part 1"/"Part 2" batch subfolders instead of checksums.md5 directly), goes one level into
        /// whichever one looks most recently modified and looks there instead - never deeper than that
        /// one extra level, and never when the folder has a mix of files and subfolders (if there are
        /// real files there and none of them match, assume the .md5 genuinely isn't there rather than
        /// guessing which subfolder to open).
        /// </summary>
        private async Task<DriveFolderEntry> FindManifestEntryAsync(string folderId, CancellationToken ct)
        {
            var entries = await _session.ListFolderAsync(folderId, ct);
            var md5 = MatchManifestEntry(entries);
            if (md5 != null) return md5;

            if (entries.Count > 0 && entries.All(e => LooksLikeFolder(e.Name)))
            {
                // "Part 2" beats "Part 1" - the trailing number in the name is a far more reliable
                // signal than anything scraped off Drive's UI (see ParseModifiedHint's doc comment),
                // so it's tried first; ParseModifiedHint is only a tiebreaker for names that don't
                // carry a number at all. Tries every subfolder in that order (highest first), not just
                // the top pick, in case the highest-numbered one turns out to be empty or mid-upload.
                var candidates = entries
                    .OrderByDescending(e => ExtractTrailingNumber(e.Name))
                    .ThenByDescending(e => ParseModifiedHint(e.ModifiedHint))
                    .ToList();

                var attempts = new List<(string Name, int FileCount)>();
                foreach (var candidate in candidates)
                {
                    var nested = await _session.ListFolderAsync(candidate.Id, ct);
                    var nestedMd5 = MatchManifestEntry(nested);
                    if (nestedMd5 != null) return nestedMd5;

                    attempts.Add((candidate.Name, nested.Count));
                }

                var summary = string.Join("; ", attempts.Select(a => $"\"{a.Name}\" ({a.FileCount} file(s))"));
                throw new FileNotFoundException(
                    $"Checked {attempts.Count} subfolder(s) under that Drive folder, highest-numbered first, " +
                    $"but none had a .md5 file. Checked: {summary}");
            }

            throw new FileNotFoundException(DescribeMissingManifest(entries, "that Drive folder"));
        }

        // Prefer an exact "checksums.md5" match (what ListFolderAsync's fast, embedded-view path
        // returns), then a trimmed exact-suffix match, then a loose "contains .md5" match - needed
        // because the authenticated Drive-UI fallback (used for folders only shared with specific
        // accounts, not "anyone with the link") can return a longer aria-label/innerText blob instead
        // of a clean file name.
        private static DriveFolderEntry? MatchManifestEntry(IReadOnlyList<DriveFolderEntry> entries) =>
            entries.FirstOrDefault(e => e.Name.Trim().Equals(Md5ManifestService.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            ?? entries.FirstOrDefault(e => e.Name.Trim().EndsWith(".md5", StringComparison.OrdinalIgnoreCase))
            ?? entries.FirstOrDefault(e => e.Name.Contains(".md5", StringComparison.OrdinalIgnoreCase));

        // No dot-extension at the end of the name => treated as a subfolder ("Part 1", "Part 2", ...).
        private static bool LooksLikeFolder(string name) => !Regex.IsMatch(name.Trim(), @"\.[A-Za-z0-9]{2,5}$");

        // The last run of digits in the name - "Part 2" -> 2, "Batch_09" -> 9. -1 (sorts last) for a
        // name with no number at all, so an unnumbered folder never outranks a numbered one. This is
        // the primary sort for picking which subfolder to check first (Don confirmed: highest number
        // wins, not most-recently-modified) - ParseModifiedHint below is only the tiebreaker for two
        // subfolders that carry the same number, or neither carrying one at all.
        private static int ExtractTrailingNumber(string name)
        {
            var matches = Regex.Matches(name, @"\d+");
            if (matches.Count == 0) return -1;
            return int.TryParse(matches[^1].Value, out var n) ? n : -1;
        }

        // Turns whatever date-like text ListFolderScriptAuthenticated happened to scrape ("Sep 20,
        // 2026", "3 days ago", "9/20/26") into a comparable DateTime - only used as a tiebreaker (see
        // ExtractTrailingNumber above), since Drive doesn't expose a clean "last modified" value to
        // scrape in the first place. Anything it can't parse sorts as DateTime.MinValue (oldest), so
        // an unparseable hint never wins over a real one but also never throws.
        private static DateTime ParseModifiedHint(string? hint)
        {
            if (string.IsNullOrWhiteSpace(hint)) return DateTime.MinValue;

            var ago = Regex.Match(hint, @"(\d+)\s+(second|minute|hour|day|week|month|year)s?\s+ago", RegexOptions.IgnoreCase);
            if (ago.Success)
            {
                var n = int.Parse(ago.Groups[1].Value);
                TimeSpan span = ago.Groups[2].Value.ToLowerInvariant() switch
                {
                    "second" => TimeSpan.FromSeconds(n),
                    "minute" => TimeSpan.FromMinutes(n),
                    "hour" => TimeSpan.FromHours(n),
                    "day" => TimeSpan.FromDays(n),
                    "week" => TimeSpan.FromDays(n * 7),
                    "month" => TimeSpan.FromDays(n * 30),
                    _ => TimeSpan.FromDays(n * 365) // "year"
                };
                return DateTime.Now - span;
            }

            return DateTime.TryParse(hint, out var parsed) ? parsed : DateTime.MinValue;
        }

        // Includes the actual names ListFolderAsync scraped - if this ever misfires again, what's in
        // the message is exactly what needs to change to fix it (either the JS name-extraction in
        // GoogleWebSession, or the matching in MatchManifestEntry above).
        private static string DescribeMissingManifest(IReadOnlyList<DriveFolderEntry> entries, string whatWasOpened)
        {
            if (entries.Count == 0)
                return $"Opened {whatWasOpened} but it looked empty - either this Google account can't see it, or it genuinely has no files.";

            var seen = string.Join(", ", entries.Select(e => $"\"{e.Name}\""));
            return $"Opened {whatWasOpened} ({entries.Count} file(s)) but none of them look like a .md5 file. Saw: {seen}";
        }

        // ---------- Verify ----------

        private async Task VerifySelectedAsync()
        {
            var row = SelectedRow;
            var build = SelectedBuild;
            if (row == null || build == null || !row.IsDownloaded) return;

            if (!Directory.Exists(build.BuildPath))
            {
                StatusText = $"Build folder not found: {build.BuildPath}";
                return;
            }

            if (ChecksumSheetParser.NormalizeVersion(build.Version) != ChecksumSheetParser.NormalizeVersion(row.Version))
            {
                var answer = MessageBox.Show(
                    $"\"{build.Title}\" is set to {(string.IsNullOrWhiteSpace(build.Version) ? "no version" : build.Version)}, " +
                    $"but the selected checksum is for {row.Version}.\n\nVerify anyway?",
                    "Version mismatch", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
            }

            Results.Clear();
            Extras.Clear();
            OnPropertyChanged(nameof(ResultsHeader));
            OnPropertyChanged(nameof(ExtrasHeader));
            SummaryState = "None";
            SummaryText = string.Empty;

            _log.Clear();
            _log.AppendLine($"Maintenance verification  {row.Version}  ({row.Tag})");
            _log.AppendLine($"Build tab : {build.Title}");
            _log.AppendLine($"Build path: {build.BuildPath}");
            _log.AppendLine($"Manifest  : {row.LocalManifestPath} (copied into the build folder for the run, removed after)");
            _log.AppendLine($"Started   : {DateTime.Now:G}");
            _log.AppendLine(new string('-', 72));
            LogText = _log.ToString();

            var ct = Begin("Copying .md5 into the build folder...", indeterminate: false);
            var linesSinceFlush = 0;
            var progress = new Progress<ChecksumVerifyProgress>(p =>
            {
                ProgressPercent = p.Percent;
                StatusText = $"Hashing {p.FilesDone:N0} of {p.FilesTotal:N0} files...";
                if (p.LogLine == null) return;
                _log.AppendLine(p.LogLine);
                if (++linesSinceFlush >= 50)
                {
                    linesSinceFlush = 0;
                    LogText = _log.ToString();
                }
            });

            try
            {
                var report = await Md5ManifestService.VerifyAsync(row.LocalManifestPath, build.BuildPath, progress, ct);

                foreach (var result in report.Results
                             .OrderBy(r => r.Outcome)
                             .ThenBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase))
                    Results.Add(result);
                foreach (var extra in report.Extras) Extras.Add(extra);
                OnPropertyChanged(nameof(ResultsHeader));
                OnPropertyChanged(nameof(ExtrasHeader));

                var verdict = report.Cancelled ? "CANCELLED" : report.Passed ? "PASS" : "FAIL";
                SummaryState = report.Cancelled ? "Cancelled" : report.Passed ? "Pass" : "Fail";
                SummaryText =
                    $"{verdict}  -  {report.MatchCount:N0} / {report.ManifestCount:N0} match  |  " +
                    $"{report.MismatchCount:N0} mismatched  |  {report.MissingCount:N0} missing  |  " +
                    $"{report.ErrorCount:N0} unreadable  |  {report.Extras.Count:N0} extra (info only)  |  " +
                    $"{report.Elapsed:mm\\:ss}";

                _log.AppendLine(new string('-', 72));
                if (report.Extras.Count > 0)
                {
                    _log.AppendLine("Extras (in the build folder, not in the manifest - informational):");
                    foreach (var extra in report.Extras) _log.AppendLine($"EXTRA     {extra}");
                    _log.AppendLine(new string('-', 72));
                }
                _log.AppendLine(SummaryText);

                StatusText = report.Cancelled ? "Verification cancelled." : $"Verification finished: {verdict}.";
            }
            catch (Exception ex)
            {
                SummaryState = "Fail";
                SummaryText = $"Verification couldn't run: {ex.Message}";
                _log.AppendLine($"ERROR     {ex.Message}");
                StatusText = "Verification couldn't run.";
            }
            finally
            {
                LogText = _log.ToString();
                End();
            }
        }

        // ---------- Helpers ----------

        /// <summary>
        /// Re-orders the already-loaded Rows so that whole release-version groups (v268, v269, ...)
        /// sort by NewestFirst, while each group's own rows keep whatever relative order they already
        /// have (the sheet's own v270.1.1 -> v270.2.0 -> ... ordering is never touched - only which
        /// family comes first). Uses ObservableCollection.Move rather than Clear+re-add so the
        /// DataGrid doesn't lose SelectedRow/scroll position, and RowsView's existing
        /// PropertyGroupDescription on Family then just displays groups in whatever order their first
        /// row now appears in Rows - no separate group-level sort needed.
        /// </summary>
        private void ApplyRowOrder()
        {
            var familyOrder = Rows
                .Select(r => r.Family)
                .Distinct()
                .OrderBy(f => ExtractFamilyNumber(f))
                .ToList();

            if (NewestFirst) familyOrder.Reverse();

            var desired = familyOrder
                .SelectMany(family => Rows.Where(r => r.Family == family))
                .ToList();

            for (var i = 0; i < desired.Count; i++)
            {
                var currentIndex = Rows.IndexOf(desired[i]);
                if (currentIndex != i) Rows.Move(currentIndex, i);
            }

            // RowsView's live grouping doesn't re-bucket groups off a plain CollectionChanged.Move
            // notification (a known WPF limitation - Move is meant for flat, ungrouped reordering), so
            // without this the DataGrid keeps showing the old group order until something else forces
            // a full re-evaluation (e.g. switching tabs away and back). Refresh() makes the reorder
            // show up immediately.
            RowsView.Refresh();
        }

        // "v271" -> 271, "271" -> 271. -1 (sorts first in ascending order) for a family name with no
        // number at all, so a malformed row never silently jumps ahead of real versions.
        private static int ExtractFamilyNumber(string family)
        {
            var digits = new string(family.Where(char.IsDigit).ToArray());
            return digits.Length > 0 && int.TryParse(digits, out var n) ? n : -1;
        }

        private void AutoSelectRowForBuild()
        {
            if (_selectedBuild == null || Rows.Count == 0) return;

            var version = ChecksumSheetParser.NormalizeVersion(_selectedBuild.Version);
            if (version.Length == 0) return;

            var candidates = Rows.Where(r => ChecksumSheetParser.NormalizeVersion(r.Version) == version).ToList();

            // Duplicate versions (e.g. v271.2.0 twice): prefer the lowest row that actually has a checksum posted.
            var pick = candidates.LastOrDefault(r => r.HasChecksumLink) ?? candidates.LastOrDefault();
            if (pick != null)
            {
                SelectedRow = pick;
            }
            else
            {
                StatusText = $"No sheet row matches {_selectedBuild.Version}. Pick one manually.";
            }
        }

        private void UpdateInstalledOn()
        {
            foreach (var row in Rows)
            {
                var version = ChecksumSheetParser.NormalizeVersion(row.Version);
                row.InstalledOn = string.Join(", ", InstalledBuilds
                    .Where(b => ChecksumSheetParser.NormalizeVersion(b.Version) == version)
                    .Select(b => b.Title));
            }
        }

        private void OpenChecksumsFolder()
        {
            try
            {
                Directory.CreateDirectory(MaintenanceDefaults.ChecksumsFolder);
                Process.Start(new ProcessStartInfo(MaintenanceDefaults.ChecksumsFolder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                StatusText = $"Couldn't open the checksums folder: {ex.Message}";
            }
        }

        private CancellationToken Begin(string status, bool indeterminate)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            ProgressPercent = 0;
            IsProgressIndeterminate = indeterminate;
            StatusText = status;
            IsBusy = true;
            return _cts.Token;
        }

        private void End()
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            _cts?.Dispose();
            _cts = null;
        }

        /// <summary>Same 10-second auto-clear the build tabs use. Held while a long operation is running.</summary>
        private void RestartStatusClearTimer()
        {
            _statusClearTimer?.Stop();
            if (string.IsNullOrEmpty(_statusText)) return;

            _statusClearTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _statusClearTimer.Tick -= StatusClearTimer_Tick;
            _statusClearTimer.Tick += StatusClearTimer_Tick;
            _statusClearTimer.Start();
        }

        private void StatusClearTimer_Tick(object? sender, EventArgs e)
        {
            if (IsBusy) return; // keep progress text up while something's running; next set restarts the timer
            _statusClearTimer?.Stop();
            StatusText = string.Empty;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    /// <summary>
    /// Self-contained async command so this tab doesn't depend on RelayCommand's exact signature.
    /// Swap to the app's RelayCommand later if you prefer one command type everywhere.
    /// </summary>
    internal sealed class MaintenanceCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public MaintenanceCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public async void Execute(object? parameter)
        {
            try
            {
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Maintenance Verification", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
