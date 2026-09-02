using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommandCenter.Model;
using CommandCenter.Services;
using Microsoft.Win32;

namespace CommandCenter.ViewModel
{
    public enum SectionMode
    {
        NewBuild,
        Patch,
        PushedToLive,
        Launch
    }

    // Drives one tab's content (GMS / CMS / Live Service, or any extra tab). The same view is
    // reused for every BuildSection-kind tab; only the wrapped TabSettings instance differs. Every
    // section launches by picking one of two fixed client executables plus a server from that
    // tab's own persisted, editable server list (see TabSettings.Servers/ServerOptions below) -
    // built-in entries seeded from LaunchServerCatalog plus whatever custom ones were added via
    // Settings.
    public class BuildSectionViewModel : ViewModelBase
    {
        private readonly SettingsService _settingsService;
        private readonly AppSettings _appSettings;
        private readonly TabSettings _settings;
        private readonly Dispatcher _uiDispatcher;

        // Every top-level tab, live - only used (subscribed to) when SupportsPushedToLive is true,
        // to build PushTargets below. Null for GMS/CMS/extra sections, which never show that panel.
        private readonly ObservableCollection<TabInfo>? _allTabs;

        // Every candidate tab (every BuildSection tab besides this one) currently being watched for
        // BuildPath changes, regardless of whether it's eligible for PushTargets right now - see
        // RebuildPushTargets/CandidateTab_PropertyChanged. Tracked separately from PushTargets
        // itself since a tab with no build path still needs watching so it can appear the moment
        // one is set.
        private readonly List<TabSettings> _trackedPushCandidates = new();

        private SectionMode _selectedMode = SectionMode.Launch;
        private string _sourceArchivePath = string.Empty;
        private string _pendingVersion = string.Empty;
        private string _pushSourceFolderPath = string.Empty;
        private PushTargetOption? _selectedPushTarget;
        private bool _isBusy;
        private double _progressPercent;
        private string _statusText = string.Empty;
        private string? _selectedExecutable;
        private TabServerEntry? _selectedServerOption;
        private CancellationTokenSource? _updateCancellation;
        private DispatcherTimer? _statusClearTimer;

        // Path of this section's Documents folder (a sibling of CurrentBuildPath - see
        // DocumentsService) and the watcher keeping its file list live. Null/None until
        // HasBuildPath is true - see SyncDocumentsFolder.
        private string? _documentsFolderPath;
        // The version family (see DocumentsService.VersionFamily) _documentsFolderPath currently
        // belongs to - null while no version is set (the folder is the SectionTitle fallback).
        // Tracked separately from _documentsFolderPath so SyncDocumentsFolder can tell "same
        // family, version just bumped" apart from "genuinely different family" even though both
        // change the computed path.
        private string? _documentsFamily;
        private FileSystemWatcher? _documentsWatcher;

        public BuildSectionViewModel(TabSettings settings, AppSettings appSettings, SettingsService settingsService, bool supportsPushedToLive, ObservableCollection<TabInfo>? allTabs = null)
        {
            _settings = settings;
            _appSettings = appSettings;
            _settingsService = settingsService;
            SupportsPushedToLive = supportsPushedToLive;
            _allTabs = allTabs;
            _uiDispatcher = Dispatcher.CurrentDispatcher;

            _settings.PropertyChanged += Settings_PropertyChanged;

            BrowseSourceCommand = new RelayCommand(_ => BrowseSource());
            RunUpdateCommand = new AsyncRelayCommand(_ => RunUpdateAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(SourceArchivePath) && HasBuildPath);
            CancelUpdateCommand = new RelayCommand(_ => _updateCancellation?.Cancel(), _ => IsBusy && _updateCancellation != null);
            LaunchCommand = new RelayCommand(_ => Launch(), _ => !IsBusy && HasBuildPath && SelectedExecutable != null && SelectedServerOption != null);

            BrowsePushSourceCommand = new RelayCommand(_ => BrowsePushSource());
            // Cancel is shared with RunUpdateCommand's token above - only one of these operations
            // can ever be in flight at a time per section, both gated by IsBusy.
            PushToLiveCommand = new AsyncRelayCommand(_ => PushToLiveAsync(), _ => CanPushToLive());

            AddDocumentFilesCommand = new RelayCommand(_ => AddDocumentFiles(), _ => HasBuildPath);
            OpenDocumentCommand = new RelayCommand(param =>
            {
                if (param is DocumentEntry entry)
                {
                    OpenDocument(entry);
                }
            });
            // Parameter comes from the Documents ListBox's SelectedItem (bound via CommandParameter
            // on the Delete button, or passed directly from the Delete-key handler in code-behind -
            // see BuildSectionView.xaml/.xaml.cs) - null (nothing selected) just disables the button.
            DeleteDocumentCommand = new RelayCommand(param =>
            {
                if (param is DocumentEntry entry)
                {
                    DeleteDocument(entry);
                }
            }, param => param is DocumentEntry);
            // No parameter - opens the section's whole Documents folder (whichever version-family
            // folder is current, per SyncDocumentsFolder), not a specific entry inside it.
            OpenDocumentsFolderCommand = new RelayCommand(_ => OpenDocumentsFolder(), _ => _documentsFolderPath != null);
            Documents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDocuments));

            _selectedExecutable = ExecutableOptions.FirstOrDefault();
            _selectedServerOption = ServerOptions.FirstOrDefault();

            // PushTargets (every other build-section tab, offered as a "push this tab's build
            // straight into Live" radio option) only matters for the Live Service section - see
            // RebuildPushTargets. Kept live via _allTabs.CollectionChanged so a tab added/removed
            // later from Settings shows up (or disappears) here without restarting the app.
            if (SupportsPushedToLive && _allTabs != null)
            {
                _allTabs.CollectionChanged += (_, _) => RebuildPushTargets();
                RebuildPushTargets();
            }

            // Rehydrate the Documents folder (and start watching it) if this section already had
            // a build path configured from a previous session.
            SyncDocumentsFolder();
        }

        // Reads straight from the tab's settings, so renaming a tab in Settings (GMS/CMS/Live can
        // be renamed, same as any extra tab) is reflected here immediately once saved - see
        // Settings_PropertyChanged - including in the Documents folder's fallback name.
        public string SectionTitle => _settings.Title;

        public string CurrentBuildPath => _settings.BuildPath;
        public string VersionNumber => string.IsNullOrWhiteSpace(_settings.VersionNumber) ? "Not set" : _settings.VersionNumber;
        public bool HasBuildPath => !string.IsNullOrWhiteSpace(_settings.BuildPath) && Directory.Exists(_settings.BuildPath);

        public SectionMode SelectedMode
        {
            get => _selectedMode;
            set
            {
                if (SetProperty(ref _selectedMode, value))
                {
                    OnPropertyChanged(nameof(IsNewBuild));
                    OnPropertyChanged(nameof(IsPatch));
                    OnPropertyChanged(nameof(IsPushedToLive));
                    OnPropertyChanged(nameof(IsLaunch));
                    OnPropertyChanged(nameof(IsUpdatePanelVisible));
                    OnPropertyChanged(nameof(IsPushedToLivePanelVisible));
                    OnPropertyChanged(nameof(IsLaunchPanelVisible));
                    OnPropertyChanged(nameof(RunButtonLabel));
                }
            }
        }

        public bool IsNewBuild
        {
            get => _selectedMode == SectionMode.NewBuild;
            set { if (value) SelectedMode = SectionMode.NewBuild; }
        }

        public bool IsPatch
        {
            get => _selectedMode == SectionMode.Patch;
            set { if (value) SelectedMode = SectionMode.Patch; }
        }

        // Only offered on Live Service sections - see SupportsPushedToLive. Moves the contents of
        // a picked folder into this section's Current Build folder instead of extracting an archive.
        public bool IsPushedToLive
        {
            get => _selectedMode == SectionMode.PushedToLive;
            set { if (value) SelectedMode = SectionMode.PushedToLive; }
        }

        public bool IsLaunch
        {
            get => _selectedMode == SectionMode.Launch;
            set { if (value) SelectedMode = SectionMode.Launch; }
        }

        // True only for the Live Service section (permanent or extra) - GMS/CMS never show the
        // "Pushed to Live" mode or its radio button.
        public bool SupportsPushedToLive { get; }

        public bool IsUpdatePanelVisible => SelectedMode == SectionMode.NewBuild || SelectedMode == SectionMode.Patch;
        public bool IsPushedToLivePanelVisible => SelectedMode == SectionMode.PushedToLive;
        public bool IsLaunchPanelVisible => SelectedMode == SectionMode.Launch;
        public string RunButtonLabel => SelectedMode == SectionMode.NewBuild ? "Apply New Build" : "Apply Patch";

        public string SourceArchivePath
        {
            get => _sourceArchivePath;
            set => SetProperty(ref _sourceArchivePath, value);
        }

        public string PendingVersion
        {
            get => _pendingVersion;
            set => SetProperty(ref _pendingVersion, value);
        }

        // Folder picked for "Pushed to Live" - its entire contents get moved into CurrentBuildPath.
        // Only meaningful while IsCustomFolderSelected is true (the "Folder to Push to Live" radio).
        public string PushSourceFolderPath
        {
            get => _pushSourceFolderPath;
            set => SetProperty(ref _pushSourceFolderPath, value);
        }

        // Every other build-section tab (GMS, CMS, any extra - never Server Status/Settings/this
        // section itself), offered as a "push straight from there" radio option alongside "Folder
        // to Push to Live". Rebuilt from _allTabs by RebuildPushTargets; empty (and unused) on
        // every section except the Live Service one.
        public ObservableCollection<PushTargetOption> PushTargets { get; } = new();

        // Which PushTargets entry is currently picked, or null for "Folder to Push to Live" (see
        // IsCustomFolderSelected). Setting this reconciles every PushTargetOption's own IsSelected
        // so their RadioButtons reflect the change even when set from code (e.g. RebuildPushTargets
        // resetting back to null after a tab disappears) rather than from a click.
        public PushTargetOption? SelectedPushTarget
        {
            get => _selectedPushTarget;
            set
            {
                if (!SetProperty(ref _selectedPushTarget, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsCustomFolderSelected));

                foreach (var option in PushTargets)
                {
                    option.SetSelectedSilently(ReferenceEquals(option, value));
                }
            }
        }

        // True when "Folder to Push to Live" (the original manual-browse option) is the active
        // radio choice rather than one of PushTargets. Two-way bound to that RadioButton's
        // IsChecked - setting it true clears SelectedPushTarget; WPF setting it false (because some
        // other radio in the group was just checked) is a no-op here, since that other radio's own
        // binding already updated SelectedPushTarget.
        public bool IsCustomFolderSelected
        {
            get => _selectedPushTarget == null;
            set
            {
                if (value)
                {
                    SelectedPushTarget = null;
                }
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(IsNotBusy));
                }
            }
        }

        public bool IsNotBusy => !IsBusy;

        public double ProgressPercent
        {
            get => _progressPercent;
            set => SetProperty(ref _progressPercent, value);
        }

        public string StatusText
        {
            get => _statusText;
            set
            {
                if (SetProperty(ref _statusText, value))
                {
                    RestartStatusClearTimer();
                }
            }
        }

        // Keeps a status message on screen for 10 seconds so it's readable, then clears itself so
        // nothing lingers indefinitely. Restarts on every new message, which is harmless during a
        // burst of frequent progress updates - each one just pushes the clear back out, so it only
        // actually fires once updates stop (i.e. 10 seconds after the final message of a run).
        private void RestartStatusClearTimer()
        {
            _statusClearTimer?.Stop();

            if (string.IsNullOrEmpty(_statusText))
            {
                return;
            }

            if (_statusClearTimer == null)
            {
                _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                _statusClearTimer.Tick += (_, _) =>
                {
                    _statusClearTimer!.Stop();
                    StatusText = string.Empty;
                };
            }

            _statusClearTimer.Start();
        }

        // Same 2 client executables for every section (GMS / CMS / Live Service).
        public IReadOnlyList<string> ExecutableOptions => LaunchServerCatalog.Executables;

        // This tab's own persisted server list (see TabSettings.Servers), filtered down to
        // whatever's currently enabled - built-in and custom entries side by side, in whatever
        // order Settings has them in. Computed live off _settings.Servers rather than captured once
        // at construction, so adding/editing/toggling a server in Settings and saving shows up here
        // immediately without needing to recreate this tab - see Settings_PropertyChanged.
        public IEnumerable<TabServerEntry> ServerOptions =>
            (_settings.Servers ?? Enumerable.Empty<TabServerEntry>()).Where(s => s.IsEnabled);

        public string? SelectedExecutable
        {
            get => _selectedExecutable;
            set
            {
                if (SetProperty(ref _selectedExecutable, value))
                {
                    OnPropertyChanged(nameof(IsSelectedExecutableMissing));
                }
            }
        }

        public TabServerEntry? SelectedServerOption
        {
            get => _selectedServerOption;
            set => SetProperty(ref _selectedServerOption, value);
        }

        public bool IsSelectedExecutableMissing =>
            HasBuildPath && !string.IsNullOrEmpty(SelectedExecutable) &&
            !File.Exists(Path.Combine(CurrentBuildPath, SelectedExecutable));

        // Documents attached to whichever build this section currently points at - see
        // DocumentsService and SyncDocumentsFolder for how the backing folder is chosen,
        // created, and kept in sync with the build path/version number.
        public ObservableCollection<DocumentEntry> Documents { get; } = new();
        public bool HasDocuments => Documents.Count > 0;

        // What the Documents folder is currently named (without the containing path) - shown in
        // the UI so it's obvious which folder on disk these files live in, e.g. "271 Documents"
        // for any version in that family (271.0.2, 271.0.3, ...), or, before a version number is
        // set, "<tab name> Documents".
        public string DocumentsFolderLabel => HasBuildPath
            ? (string.IsNullOrWhiteSpace(_settings.VersionNumber)
                ? DocumentsService.FallbackFolderName(SectionTitle)
                : DocumentsService.VersionedFolderName(_settings.VersionNumber))
            : $"{SectionTitle} Documents";

        public RelayCommand BrowseSourceCommand { get; }
        public AsyncRelayCommand RunUpdateCommand { get; }
        public RelayCommand CancelUpdateCommand { get; }
        public RelayCommand LaunchCommand { get; }
        public RelayCommand BrowsePushSourceCommand { get; }
        public AsyncRelayCommand PushToLiveCommand { get; }
        public RelayCommand AddDocumentFilesCommand { get; }
        public RelayCommand OpenDocumentCommand { get; }
        public RelayCommand DeleteDocumentCommand { get; }
        public RelayCommand OpenDocumentsFolderCommand { get; }

        // Raised right after a build/patch finishes extracting, with this section's build-drive
        // free-space status (or null if it couldn't be checked). MainViewModel subscribes to this
        // on every section to drive the storage tracker at the bottom of the window.
        public event EventHandler<DiskSpaceStatus?>? DiskSpaceStatusChanged;

        private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(SectionTitle));
            OnPropertyChanged(nameof(CurrentBuildPath));
            OnPropertyChanged(nameof(VersionNumber));
            OnPropertyChanged(nameof(HasBuildPath));
            OnPropertyChanged(nameof(IsSelectedExecutableMissing));
            OnPropertyChanged(nameof(DocumentsFolderLabel));

            // Settings' Save() rebuilds this tab's whole Servers list from scratch every time (see
            // SettingsViewModel.Save/CommitServers) - even a save that never touched the Servers
            // section produces brand-new TabServerEntry instances, so the ComboBox needs an
            // explicit nudge, and re-matching the selection below has to go by Id rather than by
            // reference (the old SelectedServerOption object is never going to be one of the new
            // instances, even when nothing about it actually changed).
            OnPropertyChanged(nameof(ServerOptions));

            // Carry the same selection forward by Id if it's still there (built-in, still
            // enabled - or the very same custom entry, now a new instance with the same Id); fall
            // back to the first still-enabled option if it was disabled or removed. Without this,
            // saving Settings for any reason (even just renaming the tab) would silently reset
            // Launch's server picker back to the top of the list every time.
            if (SelectedServerOption != null)
            {
                Guid previousId = SelectedServerOption.Id;
                SelectedServerOption = ServerOptions.FirstOrDefault(s => s.Id == previousId) ?? ServerOptions.FirstOrDefault();
            }

            // Covers a build path, version number, or title being set/changed (whether typed
            // directly in Settings and saved, or a version applied from PendingVersion after a
            // build/patch/push) - any of these can change where/what this section's Documents
            // folder should be.
            SyncDocumentsFolder();
        }

        private void BrowseSource()
        {
            var dialog = new OpenFileDialog
            {
                Title = $"Select {SectionTitle} build/patch archive",
                Filter = "Build/Patch Archives (*.zip;*.7z)|*.zip;*.7z|Zip Archives (*.zip)|*.zip|7-Zip Archives (*.7z)|*.7z",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                SourceArchivePath = dialog.FileName;
            }
        }

        private async Task RunUpdateAsync()
        {
            var mode = SelectedMode == SectionMode.NewBuild ? UpdateMode.NewBuild : UpdateMode.Patch;

            if (mode == UpdateMode.NewBuild)
            {
                var confirm = MessageBox.Show(
                    $"This will remove the existing {SectionTitle} build at:\n{CurrentBuildPath}\n\nand replace it entirely. Continue?",
                    "Confirm New Build",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            IsBusy = true;
            ProgressPercent = 0;
            StatusText = "Starting...";

            // BuildUpdateService now runs the whole extraction/copy on a background thread, so the
            // window stays movable and every tab (including this section's own) stays usable while
            // this runs. The token lets the user back out of a long-running one via CancelUpdateCommand.
            _updateCancellation = new CancellationTokenSource();

            var progress = new Progress<UpdateProgress>(p =>
            {
                ProgressPercent = p.PercentComplete;
                StatusText = p.Status;
            });

            try
            {
                await BuildUpdateService.RunAsync(SourceArchivePath, CurrentBuildPath, mode, progress, _updateCancellation.Token);

                if (!string.IsNullOrWhiteSpace(PendingVersion))
                {
                    _settings.VersionNumber = PendingVersion;
                    _settingsService.Save(_appSettings);
                    PendingVersion = string.Empty;
                }

                StatusText = $"{SectionTitle} updated successfully.";

                // Refresh the storage tracker with this section's build drive now that the extraction landed.
                DiskSpaceStatusChanged?.Invoke(this, DiskSpaceService.CheckDiskSpace(CurrentBuildPath));
            }
            catch (OperationCanceledException)
            {
                StatusText = $"{SectionTitle} update cancelled.";
            }
            catch (Exception ex)
            {
                StatusText = $"Update failed: {ex.Message}";
                MessageBox.Show(ex.Message, "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                _updateCancellation?.Dispose();
                _updateCancellation = null;
            }
        }

        private void BrowsePushSource()
        {
            var dialog = new OpenFolderDialog
            {
                Title = $"Select folder to push to {SectionTitle}",
                InitialDirectory = string.IsNullOrWhiteSpace(PushSourceFolderPath) || !Directory.Exists(PushSourceFolderPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                    : PushSourceFolderPath
            };

            if (dialog.ShowDialog() == true)
            {
                // Picking a folder here only makes sense for "Folder to Push to Live" - switch to
                // it even if a tab option happened to be selected, so Browse always does what it
                // looks like it does.
                SelectedPushTarget = null;
                PushSourceFolderPath = dialog.FolderName;
            }
        }

        private bool CanPushToLive()
        {
            if (IsBusy || !SupportsPushedToLive || !HasBuildPath)
            {
                return false;
            }

            // PushTargets is already filtered to valid-build-path tabs (see RebuildPushTargets), so
            // this re-check just guards the narrow window between that filtering and an actual
            // click - e.g. the folder got deleted externally moments ago and a rebuild hasn't run.
            return IsCustomFolderSelected
                ? !string.IsNullOrWhiteSpace(PushSourceFolderPath)
                : SelectedPushTarget != null && HasValidBuildPath(SelectedPushTarget.Settings);
        }

        private async Task PushToLiveAsync()
        {
            // Snapshot which tab (if any) this push is coming from before anything below can
            // change the selection - PushTargetOption wraps the live TabSettings, so its
            // BuildPath/VersionNumber are always read fresh at the point they're actually used.
            PushTargetOption? sourceTarget = IsCustomFolderSelected ? null : SelectedPushTarget;
            string sourceFolderPath = sourceTarget != null ? sourceTarget.Settings.BuildPath : PushSourceFolderPath;
            string sourceDescription = sourceTarget != null ? $"{sourceTarget.Settings.Title} ({sourceFolderPath})" : sourceFolderPath;

            var confirm = MessageBox.Show(
                $"This will move everything from:\n{sourceDescription}\n\ninto the {SectionTitle} Current Build folder:\n{CurrentBuildPath}\n\n" +
                $"The {SectionTitle} folder will be cleared first, and then the entire contents of the source folder will be moved into it. This can't be undone. Continue?",
                "Confirm Push to Live",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            IsBusy = true;
            ProgressPercent = 0;
            StatusText = "Starting...";

            // Shares _updateCancellation with RunUpdateAsync - only one long-running operation can
            // be in flight per section at a time (both gated by IsBusy), so one token covers both.
            _updateCancellation = new CancellationTokenSource();

            var progress = new Progress<UpdateProgress>(p =>
            {
                ProgressPercent = p.PercentComplete;
                StatusText = p.Status;
            });

            try
            {
                await PushToLiveService.RunAsync(sourceFolderPath, CurrentBuildPath, progress, _updateCancellation.Token);

                if (sourceTarget != null)
                {
                    // Pushing from an existing tab: Live's Version Number always follows whatever
                    // that tab was set to (not optional/typed - "if GMS was set to 271.0.3 then
                    // that is what the Version Number should be after confirming the selection").
                    // Then clear the source tab's own Version Number - its build folder is now
                    // empty, so there's nothing left there to launch under that version.
                    _settings.VersionNumber = sourceTarget.Settings.VersionNumber;
                    sourceTarget.Settings.VersionNumber = string.Empty;
                    _settingsService.Save(_appSettings);
                }
                else if (!string.IsNullOrWhiteSpace(PendingVersion))
                {
                    // Setting VersionNumber raises Settings_PropertyChanged -> SyncDocumentsFolder,
                    // which carries this section's existing Documents folder over to the new
                    // version-named folder - "along with the build, keep the documents with the
                    // build" for Pushed to Live specifically asked for.
                    _settings.VersionNumber = PendingVersion;
                    _settingsService.Save(_appSettings);
                    PendingVersion = string.Empty;
                }

                StatusText = $"{SectionTitle} pushed to Live successfully.";
                PushSourceFolderPath = string.Empty;
                SelectedPushTarget = null;

                // Refresh the storage tracker now that the move landed.
                DiskSpaceStatusChanged?.Invoke(this, DiskSpaceService.CheckDiskSpace(CurrentBuildPath));
            }
            catch (OperationCanceledException)
            {
                StatusText = "Push to Live cancelled.";
            }
            catch (Exception ex)
            {
                StatusText = $"Push to Live failed: {ex.Message}";
                MessageBox.Show(ex.Message, "Push to Live Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                _updateCancellation?.Dispose();
                _updateCancellation = null;
            }
        }

        // (Re)builds PushTargets from _allTabs: every BuildSection-kind tab except this section
        // itself, and only those with a real Current Build path to pull from (see
        // HasValidBuildPath) - a tab with nothing configured, or whose configured folder no longer
        // exists, has nothing to transfer into Live, so it doesn't belong in the list at all.
        // Called once at construction (Live only), whenever _allTabs changes (a tab added or
        // deleted via Settings), and whenever any candidate tab's BuildPath changes (see
        // CandidateTab_PropertyChanged) - covers a tab gaining or losing eligibility without either
        // tab being added/removed. Detaches every discarded PushTargetOption from its
        // TabSettings.PropertyChanged first so it stops reacting once it's no longer shown.
        private void RebuildPushTargets()
        {
            if (_allTabs == null)
            {
                return;
            }

            // A rebuild now fires on every candidate property change (see
            // CandidateTab_PropertyChanged), not just a tab being added/removed - e.g. some other
            // tab's build finishing in the background and updating its VersionNumber while the user
            // is sitting on this panel with a target already picked. Remember which tab (if any)
            // was selected so an unrelated change doesn't silently bounce the user back to "Folder
            // to Push to Live".
            TabSettings? previouslySelected = _selectedPushTarget?.Settings;

            foreach (var tab in _trackedPushCandidates)
            {
                tab.PropertyChanged -= CandidateTab_PropertyChanged;
            }
            _trackedPushCandidates.Clear();

            foreach (var option in PushTargets)
            {
                option.Selected -= PushTargetOption_Selected;
                option.Detach();
            }
            PushTargets.Clear();

            PushTargetOption? restoredSelection = null;

            foreach (var tabInfo in _allTabs)
            {
                var candidate = tabInfo.Settings;
                if (candidate.Kind != TabKind.BuildSection || ReferenceEquals(candidate, _settings))
                {
                    continue;
                }

                // Watch every candidate regardless of current eligibility - a tab with no build
                // path yet still needs to be able to show up here the moment one is set (and one
                // that's currently eligible needs to be able to drop back out if its path is
                // cleared or the folder disappears out from under it).
                candidate.PropertyChanged += CandidateTab_PropertyChanged;
                _trackedPushCandidates.Add(candidate);

                if (!HasValidBuildPath(candidate))
                {
                    continue;
                }

                var option = new PushTargetOption(candidate);
                option.Selected += PushTargetOption_Selected;
                PushTargets.Add(option);

                if (ReferenceEquals(candidate, previouslySelected))
                {
                    restoredSelection = option;
                }
            }

            // Only actually falls back to "Folder to Push to Live" when the previously-selected
            // tab is gone entirely or no longer has a build path to pull from - otherwise the same
            // tab's freshly-rebuilt PushTargetOption is reselected here.
            SelectedPushTarget = restoredSelection;
        }

        // Same check as this section's own HasBuildPath - a configured, still-existing folder.
        private static bool HasValidBuildPath(TabSettings settings) =>
            !string.IsNullOrWhiteSpace(settings.BuildPath) && Directory.Exists(settings.BuildPath);

        // Any change on a candidate tab (title, build path, version, etc.) just rebuilds the whole
        // list - same "don't bother filtering by which property changed" approach this class
        // already takes for its own Settings_PropertyChanged. Rebuilds are cheap (a handful of
        // tabs, only triggered by infrequent Settings saves), so there's no need to special-case
        // just BuildPath.
        private void CandidateTab_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RebuildPushTargets();

        private void PushTargetOption_Selected(object? sender, EventArgs e)
        {
            if (sender is PushTargetOption option)
            {
                SelectedPushTarget = option;
            }
        }

        private void Launch()
        {
            if (string.IsNullOrEmpty(SelectedExecutable) || SelectedServerOption == null)
            {
                return;
            }

            string fullPath = Path.Combine(CurrentBuildPath, SelectedExecutable);

            if (!File.Exists(fullPath))
            {
                StatusText = $"{SelectedExecutable} was not found in the build folder.";
                MessageBox.Show(StatusText, "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                var process = new Process();
                process.StartInfo.FileName = fullPath;
                process.StartInfo.Arguments = SelectedServerOption.LaunchArgument;
                process.StartInfo.WorkingDirectory = CurrentBuildPath;
                process.StartInfo.UseShellExecute = true;
                process.Start();

                StatusText = $"Launched {SelectedExecutable} -> {SelectedServerOption.DisplayName}.";
            }
            catch (Exception ex)
            {
                StatusText = $"Launch failed: {ex.Message}";
                MessageBox.Show(ex.Message, "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Recomputes where this section's Documents folder should be (a sibling of
        // CurrentBuildPath, named from VersionNumber's family or SectionTitle - see
        // DocumentsService), and reconciles reality with that, then (re)starts the watcher
        // pointed at wherever it ends up. Called once from the constructor (to rehydrate a
        // section that already had a build path from a previous session) and on every
        // Settings_PropertyChanged after that. No-ops until HasBuildPath is true - "the folder
        // gets created once the selection and build name are made," not before.
        //
        // A version bump that stays within the same family (271.0.2 -> 271.0.3) computes the
        // same path as before, so the early-return below is all that happens - the folder just
        // keeps accumulating documents as the family's versions climb. Every OTHER kind of
        // change (no version -> first version, a tab rename while no version is set, or a version
        // being cleared back out) carries the existing folder's contents over to the new name,
        // same as before family-grouping existed. The one exception is a change to a genuinely
        // different family (e.g. 271.x -> 272.x): that does NOT carry the old folder over - "271
        // Documents" is left exactly where it is as its own archive, and the new family gets its
        // own fresh folder (adopting an old exact-version-named folder for that family if one is
        // still sitting there from before this naming scheme, so nothing already on disk goes
        // missing).
        private void SyncDocumentsFolder()
        {
            if (!HasBuildPath)
            {
                StopWatching();
                _documentsFolderPath = null;
                _documentsFamily = null;
                Documents.Clear();
                return;
            }

            string? currentFamily = string.IsNullOrWhiteSpace(_settings.VersionNumber)
                ? null
                : DocumentsService.VersionFamily(_settings.VersionNumber);

            string? newPath = DocumentsService.FolderPathFor(CurrentBuildPath, DocumentsFolderLabel);
            if (newPath == null)
            {
                return;
            }

            if (string.Equals(_documentsFolderPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                _documentsFamily = currentFamily;
                return;
            }

            bool isNewFamily = currentFamily != null &&
                !string.Equals(_documentsFamily, currentFamily, StringComparison.OrdinalIgnoreCase);

            if (isNewFamily)
            {
                if (_documentsFamily == null)
                {
                    // Coming from "no family yet" (the SectionTitle fallback, or a fresh app
                    // start with nothing rehydrated in memory) - carry any existing folder over,
                    // same as every other rename below. If there was nothing to carry (a true
                    // fresh start), this is a no-op and the legacy-adoption check right after
                    // picks up any old on-disk folder instead.
                    DocumentsService.RenameFolder(_documentsFolderPath, newPath);
                }
                // else: a genuinely different family - leave that old family's folder exactly
                // where it is rather than folding it into the new one.

                if (!Directory.Exists(newPath))
                {
                    string? legacy = DocumentsService.FindLegacyFamilyFolder(CurrentBuildPath, currentFamily!);
                    DocumentsService.RenameFolder(legacy, newPath);
                }
            }
            else
            {
                DocumentsService.RenameFolder(_documentsFolderPath, newPath);
            }

            DocumentsService.EnsureFolder(newPath);
            _documentsFolderPath = newPath;
            _documentsFamily = currentFamily;
            StartWatching(newPath);
        }

        private void StartWatching(string path)
        {
            StopWatching();

            try
            {
                _documentsWatcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
                _documentsWatcher.Created += OnDocumentsFolderChanged;
                _documentsWatcher.Deleted += OnDocumentsFolderChanged;
                _documentsWatcher.Renamed += OnDocumentsFolderChanged;
                _documentsWatcher.EnableRaisingEvents = true;
            }
            catch (IOException)
            {
                // Folder vanished out from under us between EnsureFolder and here (e.g. deleted
                // externally) - the list just won't auto-refresh until the next settings change
                // re-runs SyncDocumentsFolder.
                _documentsWatcher = null;
            }

            RefreshDocumentsList();
        }

        // Stops and disposes the watcher, if any. Public so MainViewModel can call it when a tab
        // is deleted (its BuildSectionViewModel is about to be dropped entirely).
        public void StopWatching()
        {
            if (_documentsWatcher == null)
            {
                return;
            }

            _documentsWatcher.EnableRaisingEvents = false;
            _documentsWatcher.Created -= OnDocumentsFolderChanged;
            _documentsWatcher.Deleted -= OnDocumentsFolderChanged;
            _documentsWatcher.Renamed -= OnDocumentsFolderChanged;
            _documentsWatcher.Dispose();
            _documentsWatcher = null;
        }

        // FileSystemWatcher raises events on a thread-pool thread, never the UI thread - marshal
        // back before touching the ObservableCollection bound to the UI.
        private void OnDocumentsFolderChanged(object sender, FileSystemEventArgs e)
        {
            _uiDispatcher.BeginInvoke(new Action(RefreshDocumentsList));
        }

        private void RefreshDocumentsList()
        {
            Documents.Clear();

            if (_documentsFolderPath == null)
            {
                return;
            }

            foreach (var entry in DocumentsService.ListEntries(_documentsFolderPath))
            {
                Documents.Add(entry);
            }
        }

        private void AddDocumentFiles()
        {
            var dialog = new OpenFileDialog
            {
                Title = $"Add documents to {SectionTitle}",
                Multiselect = true,
                CheckFileExists = true,
                Filter = "All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                ImportDocumentPaths(dialog.FileNames);
            }
        }

        // Copies the given files/folders (from the "Add File..." dialog, or dropped in from
        // Explorer - see BuildSectionView's drag-and-drop handlers) into this section's Documents
        // folder, creating it first if this is the very first document added.
        public void ImportDocumentPaths(IEnumerable<string> paths)
        {
            if (!HasBuildPath)
            {
                return;
            }

            if (_documentsFolderPath == null)
            {
                SyncDocumentsFolder();
                if (_documentsFolderPath == null)
                {
                    return;
                }
            }

            DocumentsService.AddPaths(_documentsFolderPath, paths);
            RefreshDocumentsList();
        }

        private void OpenDocument(DocumentEntry entry)
        {
            try
            {
                var process = new Process();
                process.StartInfo.FileName = entry.FullPath;
                process.StartInfo.UseShellExecute = true;
                process.Start();
            }
            catch (Exception ex)
            {
                StatusText = $"Couldn't open {entry.Name}: {ex.Message}";
            }
        }

        // Opens the section's Documents folder itself (whichever version-family folder
        // SyncDocumentsFolder currently has it pointed at, e.g. "271 Documents") in Explorer -
        // the "Open Folder" button next to Add File/Delete. EnsureFolder first since nothing
        // guarantees the folder still exists on disk (could've been deleted externally since the
        // watcher last ran) - same ShellExecute approach as OpenDocument above, just pointed at
        // the folder instead of one entry inside it.
        private void OpenDocumentsFolder()
        {
            if (_documentsFolderPath == null)
            {
                return;
            }

            try
            {
                DocumentsService.EnsureFolder(_documentsFolderPath);
                var process = new Process();
                process.StartInfo.FileName = _documentsFolderPath;
                process.StartInfo.UseShellExecute = true;
                process.Start();
            }
            catch (Exception ex)
            {
                StatusText = $"Couldn't open the Documents folder: {ex.Message}";
            }
        }

        // Deletes a single document (file or subfolder) off disk, after confirming with the user
        // that it can't be undone - this is a permanent delete, not a move to the Recycle Bin.
        // Backs both the Delete button next to "Add File..." and the Delete key on the Documents
        // list (see BuildSectionView.xaml/.xaml.cs). The list itself refreshes off the
        // FileSystemWatcher already pointed at the folder, but RefreshDocumentsList is also
        // called directly here so the entry disappears immediately rather than waiting on that
        // round trip.
        private void DeleteDocument(DocumentEntry entry)
        {
            string kind = entry.IsDirectory ? "folder" : "file";
            var result = MessageBox.Show(
                $"Delete the {kind} \"{entry.Name}\"?\n\nThis can't be undone.",
                "Delete Document",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                DocumentsService.DeleteEntry(entry.FullPath, entry.IsDirectory);
                RefreshDocumentsList();
            }
            catch (Exception ex)
            {
                StatusText = $"Couldn't delete {entry.Name}: {ex.Message}";
            }
        }

        // Called by MainViewModel when this tab is deleted (via Settings) - its documents
        // shouldn't outlive the tab itself. Never called for the 5 permanent tabs, which can't
        // be deleted.
        public void DeleteDocumentsFolder()
        {
            DocumentsService.DeleteFolder(_documentsFolderPath);
            _documentsFolderPath = null;
            _documentsFamily = null;
        }
    }
}
