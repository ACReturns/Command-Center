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
using CommandCenter.View;
using Microsoft.Win32;

// ExecutableScanner (Helpers/ExecutableScanner.cs) lives in the top-level CommandCenter namespace,
// same as AppPaths/InverseBooleanToVisibilityConverter - not CommandCenter.Helpers - hence this
// using rather than "using CommandCenter.Helpers;". Same convention DraftTabViewModel.cs uses.
using CommandCenter;

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
    // section launches by picking one of this tab's own enabled client executables (discovered from
    // its build folder - see TabSettings.Executables/ExecutableOptions below) plus a server from
    // that tab's own persisted, editable server list (see TabSettings.Servers/ServerOptions below) -
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
        private bool _hasAdditionalArchives;
        private string _pendingVersion = string.Empty;
        private string _pushSourceFolderPath = string.Empty;
        private PushTargetOption? _selectedPushTarget;
        private bool _isBusy;
        private double _progressPercent;
        private string _statusText = string.Empty;
        private string? _selectedExecutable;
        // True only while Settings_PropertyChanged is re-matching SelectedExecutable after a
        // Settings save rebuilt _settings.Executables - see SelectedExecutable's setter. Without
        // this, that internal re-sync would look exactly like a user picking something from the
        // dropdown and try to persist + save again, re-entrantly, in the middle of
        // SettingsViewModel.Save() still assigning the rest of _appSettings.Tabs.
        private bool _isSyncingSelectedExecutable;
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

        // True while the "Enable Debug Command List" checkbox is checked - see
        // DebugCommandListEnabled. Read straight off _settings.DebugCommandListEnabled rather than
        // cached here (same reasoning as CurrentBuildPath/VersionNumber above); no backing field
        // needed for the property itself.

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
            // "Add Another File..." under New Build's additional-archives list - always available
            // (no CanExecute restriction), same as AddDebugCommandCommand.
            AddAdditionalArchiveCommand = new RelayCommand(_ => AddAdditionalArchive());
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

            // Debug command list: Add/Import always available; Edit/Delete need a row selected in
            // the ListBox (its SelectedItem is passed through as CommandParameter - see
            // BuildSectionView.xaml, same wiring the Documents list already uses).
            AddDebugCommandCommand = new RelayCommand(_ => AddDebugCommand());
            ImportDebugCommandsCommand = new RelayCommand(_ => ImportDebugCommands());
            EditDebugCommandCommand = new RelayCommand(param =>
            {
                if (param is string command)
                {
                    EditDebugCommand(command);
                }
            }, param => param is string);
            DeleteDebugCommandCommand = new RelayCommand(param =>
            {
                if (param is string command)
                {
                    DeleteDebugCommand(command);
                }
            }, param => param is string);
            DebugCommands.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDebugCommands));

            // Rehydrate this tab's saved debug command list (see Services/DebugCommandListService)
            // regardless of whether the toggle is currently on - the entries live on disk
            // independently of DebugCommandListEnabled, same as Documents living independently of
            // which SectionMode is selected.
            foreach (string command in DebugCommandListService.Load(_settings.Id))
            {
                DebugCommands.Add(command);
            }

            // Rehydrate whichever executable the user picked last time (see
            // TabSettings.LastSelectedExecutable), if it's still one of this tab's enabled options -
            // falls back to the first enabled option for a brand-new tab, or if the remembered one
            // got disabled/removed since. Assigned to the backing field directly (not the
            // SelectedExecutable property) so this doesn't itself count as a "user picked this" and
            // re-save LastSelectedExecutable right back to what it already was.
            _selectedExecutable = ExecutableOptions.FirstOrDefault(e =>
                string.Equals(e, _settings.LastSelectedExecutable, StringComparison.OrdinalIgnoreCase))
                ?? ExecutableOptions.FirstOrDefault();
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

        // Mirrors Settings' own "Last version pushed to Live" note (DraftTabViewModel.
        // LastVersionPushedToLiveDisplay/HasLastVersionPushedToLive) onto this tab's own header, so
        // the version this tab was last pushed to Live under is still visible here too, not only
        // after reopening Settings - see TabSettings.LastVersionPushedToLive for when this gets set
        // (PushToLiveAsync, on the source tab) and auto-cleared (a new, non-blank Version Number).
        public bool HasLastVersionPushedToLive => !string.IsNullOrEmpty(_settings.LastVersionPushedToLive);
        public string LastVersionPushedToLiveDisplay => $"Last version pushed to Live: {_settings.LastVersionPushedToLive}";

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
                    // The additional-archives list is New Build-only - switching to Patch (or
                    // anything else) needs to hide it even though HasAdditionalArchives itself
                    // doesn't change, since both panels share this same StackPanel in
                    // BuildSectionView.xaml.
                    OnPropertyChanged(nameof(IsAdditionalArchivesPanelVisible));
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

        // New Build only (see its Visibility binding in BuildSectionView.xaml) - some builds ship
        // as a base archive plus one or more patch archives that need applying right after, which
        // used to mean running New Build, then manually switching to Patch and re-running once per
        // extra file. Checking this reveals AdditionalArchives below; RunUpdateAsync applies the
        // base file with UpdateMode.NewBuild same as always, then walks AdditionalArchives in order
        // with UpdateMode.Patch - the same overlay-not-wipe flow Patch mode already uses - straight
        // onto the same CurrentBuildPath, one after another, in a single Run.
        //
        // Purely transient input state, same as SourceArchivePath itself - never persisted to
        // TabSettings. Turning this on seeds AdditionalArchives with one blank row immediately (so
        // there's always something to browse to as soon as the box is checked); turning it off just
        // hides the list rather than clearing it, so a row already browsed to survives an accidental
        // uncheck/recheck.
        public bool HasAdditionalArchives
        {
            get => _hasAdditionalArchives;
            set
            {
                if (!SetProperty(ref _hasAdditionalArchives, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsAdditionalArchivesPanelVisible));

                if (value && AdditionalArchives.Count == 0)
                {
                    AddAdditionalArchive();
                }
            }
        }

        // Every additional file, in the order they'll be applied - see HasAdditionalArchives above
        // and AddAdditionalArchive/RemoveAdditionalArchive below. A row with a blank Path (added but
        // never browsed) is skipped rather than treated as an error when Run actually executes - see
        // RunUpdateAsync.
        public ObservableCollection<AdditionalArchivePathViewModel> AdditionalArchives { get; } = new();

        // What BuildSectionView.xaml actually binds the additional-archives list's Visibility to -
        // HasAdditionalArchives alone isn't enough, since New Build and Patch share the same
        // StackPanel and this list only ever makes sense for New Build. Without this, checking the
        // box, then switching to Patch (which hides the checkbox but doesn't reset the flag behind
        // it) would leave the list showing on the Patch panel too.
        public bool IsAdditionalArchivesPanelVisible => IsNewBuild && HasAdditionalArchives;

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

        // Every other non-Live build-section tab (GMS, CMS, any extra that isn't itself Live-capable
        // - never Server Status/Settings/this section itself/another Live tab), offered as a "push
        // straight from there" radio option alongside "Folder to Push to Live". Rebuilt from
        // _allTabs by RebuildPushTargets; empty (and unused) on every section except the Live
        // Service one.
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

        // This tab's own persisted executable list (see TabSettings.Executables), filtered down to
        // whatever's currently enabled - every .exe DraftTabViewModel.RescanExecutables found
        // sitting in this tab's build folder, minus whichever ones Settings' "Available
        // Executables" checkboxes have turned off. Replaces the old fixed, 2-name
        // LaunchServerCatalog.Executables list. Computed live off _settings.Executables rather than
        // captured once at construction, same reasoning as ServerOptions - see Settings_PropertyChanged.
        public IEnumerable<string> ExecutableOptions => _settings.Executables.Where(e => e.IsEnabled).Select(e => e.FileName);

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

                    // Remember this choice for next time (see TabSettings.LastSelectedExecutable) -
                    // but only for an actual user pick from the dropdown, not
                    // Settings_PropertyChanged silently re-matching the same selection after a
                    // Settings save touched _settings.Executables. Persisted immediately, same as
                    // VersionNumber elsewhere (RunUpdateAsync/PushToLiveAsync), rather than waiting
                    // on the next Settings save - there's no "Settings" step involved in picking an
                    // executable at all.
                    if (!_isSyncingSelectedExecutable && !string.IsNullOrEmpty(value))
                    {
                        _settings.LastSelectedExecutable = value;
                        _settingsService.Save(_appSettings);
                    }
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
        // The section's Documents folder as this VM's OWN SyncDocumentsFolder actually last landed
        // it - the literal value of _documentsFolderPath, not a value recomputed from scratch. This
        // is the one place PushToLiveAsync can ask "what does the SOURCE tab's own ViewModel
        // currently think its Documents folder is" via PushTargetOption.SourceViewModel, rather than
        // reproducing the same formula and hoping it matches - see that property's own comment and
        // pushed_to_live_documents_and_executables_sync.md CORRECTIONS. Null exactly when
        // _documentsFolderPath is null (no build path configured yet - see SyncDocumentsFolder).
        public string? DocumentsFolderPath => _documentsFolderPath;

        // Live-capable tabs (SupportsPushedToLive - Live itself, and any extra tab given the same
        // toggle) always use the plain fallback name, NEVER a version-family name, regardless of
        // whether a Version Number is set. This is deliberate (user's request, 2026-09-11): a
        // Pushed to Live destination folder name that never changes means PushToLiveAsync's merge
        // target is always the exact same path, so the "does the version-named folder already
        // exist / does it match what the source thinks its folder is called" class of bug (see
        // pushed_to_live_documents_and_executables_sync.md) simply can't happen on the Live side -
        // if the folder doesn't exist yet, SyncDocumentsFolder/EnsureFolder just creates it fresh,
        // and every subsequent push merges into that same folder rather than a new version-named
        // one. Non-Live-capable tabs (GMS, CMS, plain extras) are unaffected and keep the existing
        // version-family naming for their own Documents folder.
        public string DocumentsFolderLabel => HasBuildPath
            ? (SupportsPushedToLive || string.IsNullOrWhiteSpace(_settings.VersionNumber)
                ? DocumentsService.FallbackFolderName(SectionTitle)
                : DocumentsService.VersionedFolderName(_settings.VersionNumber))
            : $"{SectionTitle} Documents";

        // The Launch panel's "Enable Debug Command List" checkbox - shows/hides the Add/Edit/
        // Delete area beneath it (bound straight to this in BuildSectionView.xaml) and gates
        // whether Launch copies cmd_uidebug.txt into the build folder (see Launch below).
        // Persisted immediately on every toggle, same direct-write pattern SelectedExecutable
        // uses for LastSelectedExecutable - there's no "Settings" step involved in this either.
        public bool DebugCommandListEnabled
        {
            get => _settings.DebugCommandListEnabled;
            set
            {
                if (_settings.DebugCommandListEnabled == value)
                {
                    return;
                }

                _settings.DebugCommandListEnabled = value;
                _settingsService.Save(_appSettings);
                OnPropertyChanged();
            }
        }

        // This tab's saved debug commands, one per line of its cmd_uidebug.txt (see
        // Services/DebugCommandListService) - loaded once at construction, then kept in sync with
        // that file by Add/Edit/Delete below (each rewrites the whole file via PersistDebugCommands
        // so the on-disk copy never drifts from what's shown here).
        public ObservableCollection<string> DebugCommands { get; } = new();
        public bool HasDebugCommands => DebugCommands.Count > 0;

        public RelayCommand BrowseSourceCommand { get; }
        public RelayCommand AddAdditionalArchiveCommand { get; }
        public AsyncRelayCommand RunUpdateCommand { get; }
        public RelayCommand CancelUpdateCommand { get; }
        public RelayCommand LaunchCommand { get; }
        public RelayCommand BrowsePushSourceCommand { get; }
        public AsyncRelayCommand PushToLiveCommand { get; }
        public RelayCommand AddDocumentFilesCommand { get; }
        public RelayCommand OpenDocumentCommand { get; }
        public RelayCommand DeleteDocumentCommand { get; }
        public RelayCommand OpenDocumentsFolderCommand { get; }
        public RelayCommand AddDebugCommandCommand { get; }
        public RelayCommand ImportDebugCommandsCommand { get; }
        public RelayCommand EditDebugCommandCommand { get; }
        public RelayCommand DeleteDebugCommandCommand { get; }

        // Raised right after a build/patch finishes extracting, with this section's build-drive
        // free-space status (or null if it couldn't be checked). MainViewModel subscribes to this
        // on every section to drive the storage tracker at the bottom of the window.
        public event EventHandler<DiskSpaceStatus?>? DiskSpaceStatusChanged;

        private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(SectionTitle));
            OnPropertyChanged(nameof(CurrentBuildPath));
            OnPropertyChanged(nameof(VersionNumber));
            OnPropertyChanged(nameof(HasLastVersionPushedToLive));
            OnPropertyChanged(nameof(LastVersionPushedToLiveDisplay));
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

            // Same "Save() rebuilds the whole list from scratch" reasoning as ServerOptions above -
            // ExecutableOptions is just a filtered projection of _settings.Executables, but the
            // ComboBox still needs an explicit nudge, and the previous selection (a plain file name,
            // not an object with an Id) needs to be re-matched by name rather than by reference.
            OnPropertyChanged(nameof(ExecutableOptions));

            if (SelectedExecutable != null)
            {
                // Guarded so this internal re-sync (a Settings save touched Executables, not the
                // user picking a new one) never re-persists LastSelectedExecutable or re-triggers a
                // settings.json write - see SelectedExecutable's setter and
                // _isSyncingSelectedExecutable's own comment.
                _isSyncingSelectedExecutable = true;
                try
                {
                    string previousExecutable = SelectedExecutable;
                    SelectedExecutable = ExecutableOptions.FirstOrDefault(e => string.Equals(e, previousExecutable, StringComparison.OrdinalIgnoreCase))
                        ?? ExecutableOptions.FirstOrDefault();
                }
                finally
                {
                    _isSyncingSelectedExecutable = false;
                }
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

        // "Add Another File..." under New Build's additional-archives list - always appends one
        // blank row (browsed to afterward via that row's own BrowseCommand), same as
        // HasAdditionalArchives seeding the very first one when the checkbox is turned on.
        private void AddAdditionalArchive()
        {
            AdditionalArchives.Add(new AdditionalArchivePathViewModel(RemoveAdditionalArchive));
        }

        // Wired into every row's RemoveCommand at construction (see AddAdditionalArchive) - just
        // drops that one row. Never touches HasAdditionalArchives itself, so the checkbox stays
        // checked (with an empty list, and "Add Another File..." still available) even if every row
        // gets removed - RunUpdateAsync treats an empty AdditionalArchives the same as the checkbox
        // being off: nothing extra to apply.
        private void RemoveAdditionalArchive(AdditionalArchivePathViewModel entry)
        {
            AdditionalArchives.Remove(entry);
        }

        private async Task RunUpdateAsync()
        {
            var mode = SelectedMode == SectionMode.NewBuild ? UpdateMode.NewBuild : UpdateMode.Patch;

            // Every additional row with something actually browsed to, in the order shown - a row
            // that was added but never pointed at a file is skipped rather than treated as an
            // error, since "Add Another File..." always adds one blank. Only meaningful for New
            // Build - Patch mode has no additional-archives UI at all (see
            // HasAdditionalArchives's Visibility binding in BuildSectionView.xaml), so this is empty
            // whenever mode == Patch regardless of what's left over in the list from a previous
            // New Build run.
            List<string> additionalPaths = mode == UpdateMode.NewBuild
                ? AdditionalArchives.Select(a => a.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList()
                : new List<string>();

            if (mode == UpdateMode.NewBuild)
            {
                string confirmMessage = additionalPaths.Count == 0
                    ? $"This will remove the existing {SectionTitle} build at:\n{CurrentBuildPath}\n\nand replace it entirely. Continue?"
                    : $"This will remove the existing {SectionTitle} build at:\n{CurrentBuildPath}\n\nreplace it with the base file, then apply " +
                      $"{additionalPaths.Count} additional file{(additionalPaths.Count == 1 ? "" : "s")} on top of it, in order. Continue?";

                var confirm = MessageBox.Show(
                    confirmMessage,
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

            int totalFiles = 1 + additionalPaths.Count;

            try
            {
                // The base file always runs with this section's own mode (New Build wipes the
                // folder first; Patch never does). Every additional file after it always runs as
                // UpdateMode.Patch, straight onto the same CurrentBuildPath BuildUpdateService just
                // laid the base file into - the same overlay-not-wipe flow a manually-run Patch
                // already uses, just chained automatically instead of requiring the user to switch
                // modes and re-run once per extra file.
                await RunOneArchiveAsync(SourceArchivePath, mode, fileNumber: 1, totalFiles, _updateCancellation.Token);

                for (int i = 0; i < additionalPaths.Count; i++)
                {
                    await RunOneArchiveAsync(additionalPaths[i], UpdateMode.Patch, fileNumber: i + 2, totalFiles, _updateCancellation.Token);
                }

                if (!string.IsNullOrWhiteSpace(PendingVersion))
                {
                    _settings.VersionNumber = PendingVersion;
                    _settingsService.Save(_appSettings);
                    PendingVersion = string.Empty;
                }

                StatusText = totalFiles == 1
                    ? $"{SectionTitle} updated successfully."
                    : $"{SectionTitle} updated successfully ({totalFiles} files applied).";

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

        // Runs one archive through BuildUpdateService and maps its own 0-100 progress into this
        // file's 1/totalFiles slice of the overall bar (fileNumber is 1-based), so a multi-file New
        // Build shows one continuous progress sweep across every file instead of the bar restarting
        // at 0 for each one. Status text gets a "File X of Y:" prefix whenever there's more than one
        // file involved; a plain single-file run (the overwhelmingly common case) is left exactly as
        // it always reads, with no prefix.
        private Task RunOneArchiveAsync(string archivePath, UpdateMode mode, int fileNumber, int totalFiles, CancellationToken cancellationToken)
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                ProgressPercent = totalFiles == 1
                    ? p.PercentComplete
                    : ((fileNumber - 1) + p.PercentComplete / 100.0) / totalFiles * 100.0;
                StatusText = totalFiles == 1 ? p.Status : $"File {fileNumber} of {totalFiles}: {p.Status}";
            });

            return BuildUpdateService.RunAsync(archivePath, CurrentBuildPath, mode, progress, cancellationToken);
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

            // Captured up front (sourceTarget != null path only) so it survives past the point
            // where this method changes VersionNumber (on both this tab and the source tab) out
            // from under itself.
            string? previousSourceVersion = sourceTarget?.Settings.VersionNumber;

            // Set inside the sourceTarget-merge block below when the Documents merge didn't fully
            // succeed - appended to the final success StatusText so a failure isn't invisible next
            // to a "pushed to Live successfully" message that would otherwise read as "everything
            // worked". See the merge block's own comments for exactly what this covers.
            string documentsMergeNote = string.Empty;

            try
            {
                if (sourceTarget != null)
                {
                    // Snapshot where the source tab's documents currently live BEFORE anything below
                    // changes - computed the exact same way that tab's own BuildSectionViewModel
                    // computes its own Documents folder (see SyncDocumentsFolder/DocumentsService),
                    // using the version it still has right now.
                    string sourceDocsLabel = string.IsNullOrWhiteSpace(previousSourceVersion)
                        ? DocumentsService.FallbackFolderName(sourceTarget.Settings.Title)
                        : DocumentsService.VersionedFolderName(previousSourceVersion!);
                    string? computedSourceDocsPath = DocumentsService.FolderPathFor(sourceTarget.Settings.BuildPath, sourceDocsLabel);

                    // Prefer asking the source tab's OWN BuildSectionViewModel what it actually
                    // thinks its current Documents folder is (its literal _documentsFolderPath,
                    // exposed as DocumentsFolderPath - see that property and
                    // PushTargetOption.SourceViewModel) rather than trusting the recomputed
                    // `computedSourceDocsPath` above blindly. Both should always agree - they read
                    // the exact same TabSettings instance (see RebuildPushTargets/
                    // CreateBuildSectionViewModel) - but this exact merge has silently failed across
                    // multiple previous "fix" attempts in ways static reading never caught (see
                    // pushed_to_live_documents_and_executables_sync.md CORRECTIONS), so this goes
                    // straight to the source of truth instead of re-deriving it and hoping. Falls
                    // back to the computed value only if no source ViewModel reference is available
                    // at all (e.g. some future caller constructs a PushTargetOption without one).
                    string? sourceDocsPath = sourceTarget.SourceViewModel?.DocumentsFolderPath ?? computedSourceDocsPath;

                    // Adopt Live's new Version Number - and with it, whichever Documents folder
                    // belongs to that version for THIS tab (see SyncDocumentsFolder) - before the
                    // (potentially long-running) build-file move even starts, so this tab's own
                    // header/Documents-folder-label already reflect the version being adopted while
                    // the bigger move is still going, rather than only catching up once it's done.
                    _settings.VersionNumber = previousSourceVersion ?? string.Empty;

                    // Recompute the destination explicitly via DocumentsFolderLabel - the exact same
                    // public property the header/Documents panel bind to - rather than trusting
                    // _documentsFolderPath was already landed correctly by the SyncDocumentsFolder
                    // call the assignment above just triggered. For a Live-capable tab (this one -
                    // PushToLiveAsync is only ever called on a section with SupportsPushedToLive),
                    // DocumentsFolderLabel as of 2026-09-11 always resolves to the plain fallback
                    // name ("{SectionTitle} Documents"), never a version-family name - see that
                    // property's own comment. That means this destination path is now the SAME
                    // every single push, regardless of which version is being adopted, which is the
                    // whole point: there's no "does the version-named folder that was just created
                    // actually match what gets merged into" class of drift left to have, because
                    // there's no version-dependent naming on this side anymore. EnsureFolder
                    // guarantees the destination actually exists on disk before the merge below.
                    string? destinationDocsPath = DocumentsService.FolderPathFor(CurrentBuildPath, DocumentsFolderLabel);

                    // Merge the source's actual files into that destination - DocumentsService.
                    // TryRenameFolder copies everything over (overwriting any same-named file/folder
                    // rather than duplicating it) and then deletes the now-emptied source folder; if
                    // anything about the copy fails partway, the source folder is left in place
                    // rather than losing anything, but - unlike the old silent RenameFolder call this
                    // replaced - the failure is now captured instead of discarded, logged via
                    // Debug.WriteLine, and folded into documentsMergeNote so it shows up in the
                    // final StatusText too. This is diagnostic instrumentation added 2026-09-11
                    // specifically because this exact merge has appeared to work (no exception, no
                    // visible error) while actually doing nothing, on every previous "fix" attempt in
                    // this area - see pushed_to_live_documents_and_executables_sync.md CORRECTIONS.
                    // Clearing the source tab's own Version Number further down runs THAT tab's own
                    // SyncDocumentsFolder, which (if this merge actually succeeded) finds nothing
                    // left to carry over and just creates a fresh, empty fallback-named folder there
                    // - "the directory is now empty for the old source version," as originally asked
                    // for. If this merge did NOT succeed, that same later call will instead find the
                    // real files still sitting there and carry THEM over to the fallback name - which
                    // is exactly the reported bug, now something documentsMergeNote will flag instead
                    // of silently reproducing.
                    if (destinationDocsPath != null)
                    {
                        DocumentsService.EnsureFolder(destinationDocsPath);

                        bool merged = DocumentsService.TryRenameFolder(sourceDocsPath, destinationDocsPath, out string? mergeError);

                        int filesAtDestination;
                        try
                        {
                            filesAtDestination = Directory.Exists(destinationDocsPath)
                                ? Directory.GetFiles(destinationDocsPath, "*", SearchOption.AllDirectories).Length
                                : 0;
                        }
                        catch
                        {
                            filesAtDestination = -1;
                        }

                        Debug.WriteLine(
                            $"[PushToLiveAsync] Documents merge for {sourceTarget.Settings.Title} -> {SectionTitle}: " +
                            $"sourceDocsPath='{sourceDocsPath}' (computed='{computedSourceDocsPath}', fromSourceViewModel={sourceTarget.SourceViewModel?.DocumentsFolderPath != null}), " +
                            $"destinationDocsPath='{destinationDocsPath}', merged={merged}, error='{mergeError}', filesAtDestinationAfter={filesAtDestination}.");

                        if (!merged)
                        {
                            documentsMergeNote = $" NOTE: Documents were not merged into Live ({mergeError}) - check {sourceTarget.Settings.Title}'s Documents folder.";
                        }
                    }

                    // Refresh directly rather than waiting on the FileSystemWatcher's async
                    // Dispatcher.BeginInvoke round trip - that's what made the Documents panel look
                    // like it only updated well after everything else had finished.
                    RefreshDocumentsList();
                }

                await PushToLiveService.RunAsync(sourceFolderPath, CurrentBuildPath, progress, _updateCancellation.Token);

                // The build files just landed in CurrentBuildPath - for every Pushed to Live, not
                // only a tab-sourced one - so rescan this tab's own Available Executables the same
                // way Settings would. Without this, Settings kept showing 0 (or whatever was there
                // before) instead of what's actually sitting in the folder now, since nothing else
                // triggers a rescan outside of Settings being opened.
                _settings.Executables = ExecutableScanner.ScanExecutables(CurrentBuildPath)
                    .Select(name => new TabExecutableEntry { FileName = name, IsEnabled = true })
                    .ToList();

                if (sourceTarget != null)
                {
                    // The source tab's build folder is now actually empty (the move above just
                    // finished), so rescan its Available Executables too - nothing left on disk
                    // means nothing should stick around in its launch dropdown just because nothing
                    // else happened to trigger a rescan.
                    sourceTarget.Settings.Executables = ExecutableScanner.ScanExecutables(sourceTarget.Settings.BuildPath)
                        .Select(name => new TabExecutableEntry { FileName = name, IsEnabled = true })
                        .ToList();

                    // Remember what this tab was pushed to Live under - shown next to its Version
                    // Number field in Settings until a new version is typed there (see
                    // TabSettings.LastVersionPushedToLive and its auto-clear) - then clear the source
                    // tab's own Version Number, since there's nothing left in its build folder to
                    // launch under that version anymore. Raises the source tab's own
                    // Settings_PropertyChanged, refreshing its bindings (HasBuildPath,
                    // ExecutableOptions off the list just reassigned, Documents list, etc.) and
                    // running its own SyncDocumentsFolder.
                    if (!string.IsNullOrWhiteSpace(previousSourceVersion))
                    {
                        sourceTarget.Settings.LastVersionPushedToLive = previousSourceVersion;
                    }

                    sourceTarget.Settings.VersionNumber = string.Empty;
                }
                else if (!string.IsNullOrWhiteSpace(PendingVersion))
                {
                    // Setting VersionNumber raises Settings_PropertyChanged -> SyncDocumentsFolder,
                    // which carries this section's existing Documents folder over to the new
                    // version-named folder - "along with the build, keep the documents with the
                    // build" for Pushed to Live specifically asked for.
                    _settings.VersionNumber = PendingVersion;
                    PendingVersion = string.Empty;
                }

                // One save covers every mutation above - this tab's own Executables/VersionNumber
                // (every push), plus the source tab's Executables/LastVersionPushedToLive/
                // VersionNumber when this was a tab-sourced push.
                _settingsService.Save(_appSettings);

                StatusText = $"{SectionTitle} pushed to Live successfully.{documentsMergeNote}";
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
        // itself and any other Live-capable tab (SupportsPushedToLive - see the loop below), and
        // only those with a real Current Build path to pull from (see HasValidBuildPath) - a tab
        // with nothing configured, or whose configured folder no longer exists, has nothing to
        // transfer into Live, so it doesn't belong in the list at all.
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
                // Never offer another Live-capable tab as a push source (2026-09-11: with extra
                // tabs able to opt into SupportsPushedToLive via the "Enable Pushed to Live"
                // toggle, there can be more than one Live tab - only non-Live build sections make
                // sense to pull FROM here).
                if (candidate.Kind != TabKind.BuildSection || ReferenceEquals(candidate, _settings)
                    || candidate.SupportsPushedToLive)
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

                var option = new PushTargetOption(candidate, tabInfo.Content as BuildSectionViewModel);
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

            // Drop this tab's saved debug command list into the build folder as cmd_uidebug.txt
            // right before the client executable starts, so it's always this tab's latest saved
            // copy - only while the toggle is on, and only if there's actually something saved
            // (see DebugCommandListService.CopyToBuildFolder).
            if (DebugCommandListEnabled)
            {
                DebugCommandListService.CopyToBuildFolder(_settings.Id, CurrentBuildPath);
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

        // "Add..." in the Launch panel's debug command list area - appends one new command, then
        // persists the whole list to this tab's cmd_uidebug.txt (see DebugCommandListService).
        private void AddDebugCommand()
        {
            if (AddEditDebugCommandDialog.PromptForCommand("Add Debug Command", string.Empty, out string command))
            {
                DebugCommands.Add(command);
                PersistDebugCommands();
            }
        }

        // "Import..." in the Launch panel's debug command list area - lets a user who already has
        // a debug command list elsewhere (an old cmd_uidebug.txt, a list handed to them by a
        // teammate, etc.) carry it straight into this tab's saved list instead of retyping every
        // line through "Add...". Reads the picked file line by line, skips blank lines and any
        // line that's an exact duplicate of one already saved (so importing the same file twice,
        // or a file that's a superset of what's already here, doesn't pile up repeats), appends
        // the rest, then persists once for the whole batch.
        private void ImportDebugCommands()
        {
            var dialog = new OpenFileDialog
            {
                Title = $"Import debug commands for {SectionTitle}",
                Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusText = $"Couldn't import {Path.GetFileName(dialog.FileName)}: {ex.Message}";
                MessageBox.Show(ex.Message, "Import Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            int imported = 0;
            foreach (string line in lines)
            {
                string command = line.Trim();
                if (command.Length == 0 || DebugCommands.Contains(command))
                {
                    continue;
                }

                DebugCommands.Add(command);
                imported++;
            }

            if (imported == 0)
            {
                StatusText = $"Nothing new to import from {Path.GetFileName(dialog.FileName)}.";
                return;
            }

            PersistDebugCommands();
            StatusText = $"Imported {imported} debug command{(imported == 1 ? "" : "s")} from {Path.GetFileName(dialog.FileName)}.";
        }

        // "Edit..." - existing is the ListBox's SelectedItem, passed through as CommandParameter
        // (see BuildSectionView.xaml). Replaces that exact list slot rather than removing/re-adding
        // so the entry's position in the file is preserved.
        private void EditDebugCommand(string existing)
        {
            int index = DebugCommands.IndexOf(existing);
            if (index < 0)
            {
                return;
            }

            if (AddEditDebugCommandDialog.PromptForCommand("Edit Debug Command", existing, out string command))
            {
                DebugCommands[index] = command;
                PersistDebugCommands();
            }
        }

        // "Delete" - same confirm-then-delete pattern as DeleteDocument below, since this is a
        // permanent removal from the saved list (and, the next time Launch runs, from
        // cmd_uidebug.txt in the build folder too).
        private void DeleteDebugCommand(string existing)
        {
            var result = MessageBox.Show(
                $"Delete the debug command \"{existing}\"?\n\nThis can't be undone.",
                "Delete Debug Command",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            DebugCommands.Remove(existing);
            PersistDebugCommands();
        }

        // Rewrites this tab's whole cmd_uidebug.txt from DebugCommands, in order - called after
        // every Add/Edit/Delete above so the saved copy under AppPaths.DebugCommandsFileFor never
        // drifts from what's shown in the Launch panel.
        private void PersistDebugCommands() => DebugCommandListService.Save(_settings.Id, DebugCommands);

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

                // One-time migration for Live-capable tabs only, now that DocumentsFolderLabel
                // always resolves to the plain fallback name for them (2026-09-11 - see that
                // property's comment): a tab that already had a version-family-named folder (e.g.
                // "271 Documents") from before this change won't be found by
                // FindLegacyFamilyFolder above, which only matches exact-version folders like
                // "271.0.3 Documents", not an already-family-named one. Without this, that folder
                // (and any real documents in it) would simply be left behind, unreferenced, the
                // first time this runs post-change. Only fires if the fallback folder still
                // doesn't exist after the check above.
                if (SupportsPushedToLive && !Directory.Exists(newPath))
                {
                    string legacyFamilyPath = DocumentsService.VersionedFolderName(_settings.VersionNumber);
                    string? legacyFamilyFolder = DocumentsService.FolderPathFor(CurrentBuildPath, legacyFamilyPath);
                    if (legacyFamilyFolder != null && Directory.Exists(legacyFamilyFolder))
                    {
                        DocumentsService.RenameFolder(legacyFamilyFolder, newPath);
                    }
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
