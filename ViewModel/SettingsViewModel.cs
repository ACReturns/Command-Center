using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommandCenter.Model;

namespace CommandCenter.ViewModel
{
    // Owns the DRAFT copy of every tab's settings while the Settings tab is open (AppTabs / BuildTabs).
    // Nothing here touches the live AppSettings.Tabs - or the live TabInfo/BuildSectionViewModel
    // instances MainWindow actually renders - until Save() is called. That's what makes
    // discarding unsaved changes trivial: MainViewModel's tab-switch guard just calls
    // DiscardChanges(), which throws the draft away and rebuilds it fresh from whatever the live
    // Tabs still are, since the live side was never touched to begin with.
    public class SettingsViewModel : ViewModelBase
    {
        private readonly AppSettings _appSettings;
        private readonly SettingsService _settingsService;
        private readonly Action _onTabsCommitted;
        private string _statusText = string.Empty;
        private DispatcherTimer? _statusClearTimer;
        private bool _isDirty;
        private bool _runAsAdministrator;
        private string _maintenanceSheetUrl = string.Empty;
        private string _buildRepoFolderUrl = string.Empty;

        // onTabsCommitted is called after Save() has already updated _appSettings.Tabs in place
        // and persisted it - MainViewModel uses it to reconcile its live Tabs/TabInfo collection
        // (create a BuildSectionViewModel for anything brand new, tear down anything deleted).
        public SettingsViewModel(AppSettings appSettings, SettingsService settingsService, Action onTabsCommitted)
        {
            _appSettings = appSettings;
            _settingsService = settingsService;
            _onTabsCommitted = onTabsCommitted;

            AddTabCommand = new RelayCommand(_ => AddTab());
            SaveCommand = new RelayCommand(_ => Save(), _ => IsDirty);

            LoadDraftFromLive();
        }

        // Settings shows tabs in two sections: the application tabs (Server Status, OpTool,
        // Maintenance Verification, Build Repo, Launcher, Settings) that live in the main tab strip,
        // and below them the build tabs (GMS/CMS/Live and anything from "+ Add Build Tab") that
        // are only reached through the Launcher/Build Repo dropdowns. Each list is in its own
        // display order and reorders independently - Save() writes build tabs first, then app
        // tabs, into one Order sequence (the two groups never share a list anywhere else, so
        // their relative order doesn't matter).
        public ObservableCollection<DraftTabViewModel> AppTabs { get; } = new();
        public ObservableCollection<DraftTabViewModel> BuildTabs { get; } = new();

        private IEnumerable<DraftTabViewModel> AllDrafts => BuildTabs.Concat(AppTabs);

        private ObservableCollection<DraftTabViewModel>? ListFor(DraftTabViewModel? draft) =>
            draft == null ? null
            : BuildTabs.Contains(draft) ? BuildTabs
            : AppTabs.Contains(draft) ? AppTabs
            : null;

        // True the moment any draft field changes, a tab is added/deleted/restored, or the order
        // changes - cleared by Save() or DiscardChanges(). MainViewModel reads this to decide
        // whether switching away from Settings needs to warn the user first.
        public bool IsDirty
        {
            get => _isDirty;
            private set => SetProperty(ref _isDirty, value);
        }

        // App-level (not per-tab) opt-in to relaunch elevated on next startup - see AppSettings.
        // RunAsAdministrator / App.xaml.cs's OnStartup override. Loaded/committed alongside the
        // tabs in LoadDraftFromLive/Save below; setting it marks the draft dirty just like any
        // per-tab field does, so leaving Settings with this changed but unsaved still warns first.
        public bool RunAsAdministrator
        {
            get => _runAsAdministrator;
            set
            {
                if (SetProperty(ref _runAsAdministrator, value))
                {
                    IsDirty = true;
                }
            }
        }

        // Editable "Maintenance Verification sheet URL" field - same draft pattern as
        // RunAsAdministrator above. MaintenanceVerificationViewModel.SheetUrl falls back to
        // MaintenanceDefaults.SheetUrl if this is ever blank, so clearing the field and saving
        // just resets it to the built-in sheet rather than breaking the tab.
        public string MaintenanceSheetUrl
        {
            get => _maintenanceSheetUrl;
            set
            {
                if (SetProperty(ref _maintenanceSheetUrl, value))
                {
                    IsDirty = true;
                }
            }
        }

        // Editable "Build Repo Drive folder URL" - same draft pattern as MaintenanceSheetUrl above.
        // Blank on save resets it to BuildRepoDefaults.RootFolderUrl.
        public string BuildRepoFolderUrl
        {
            get => _buildRepoFolderUrl;
            set
            {
                if (SetProperty(ref _buildRepoFolderUrl, value))
                {
                    IsDirty = true;
                }
            }
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

        // Keeps the "Saved at ..." message on screen for 10 seconds so it's readable, then clears
        // itself so it doesn't linger indefinitely.
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

        public RelayCommand AddTabCommand { get; }
        public RelayCommand SaveCommand { get; }

        // No more GMS/CMS/Live picker - extras are independent top-level tabs now, not nested
        // under a parent category, so there's nothing meaningful to pick. Every tab created here
        // is a plain SectionCategory.General build section; the user fills in its Title,
        // VersionNumber, and BuildPath directly on the row that appears (same fields any other
        // build-section tab has).
        private void AddTab()
        {
            const SectionCategory category = SectionCategory.General;
            int existingCount = BuildTabs.Count(d => d.Category == category && !d.IsMarkedForDeletion);
            var draft = DraftTabViewModel.CreateNew(category, $"New Build Tab {existingCount + 1}");
            draft.Changed += OnDraftChanged;
            BuildTabs.Add(draft);
            RefreshDeleteGuards();
            IsDirty = true;
        }

        // The last build tab not already marked for deletion can't be deleted - the Launcher and
        // Build Repo dropdowns need at least one build to point at. Re-run whenever a build tab is
        // added, deleted, or restored.
        private void RefreshDeleteGuards()
        {
            int remaining = BuildTabs.Count(d => !d.IsMarkedForDeletion);

            foreach (var draft in BuildTabs)
            {
                draft.IsLastBuildTab = remaining <= 1 && !draft.IsMarkedForDeletion;
            }
        }

        // Drag-and-drop reorder (SettingsView.xaml.cs). Only allowed within the same section -
        // dragging an app tab onto a build tab (or vice versa) is a no-op.
        public bool CanReorder(DraftTabViewModel dragged, DraftTabViewModel target) =>
            !ReferenceEquals(dragged, target) && ListFor(dragged) is { } list && ReferenceEquals(list, ListFor(target));

        // Moves dragged to just before (insertAfter = false) or just after target.
        public void MoveTabTo(DraftTabViewModel dragged, DraftTabViewModel target, bool insertAfter)
        {
            if (!CanReorder(dragged, target))
            {
                return;
            }

            var list = ListFor(dragged)!;
            int from = list.IndexOf(dragged);
            int targetIndex = list.IndexOf(target);

            // Index to land on once dragged has been pulled out of its old slot.
            int to = targetIndex + (insertAfter ? 1 : 0);
            if (from < to)
            {
                to--;
            }

            to = Math.Clamp(to, 0, list.Count - 1);
            if (to == from)
            {
                return;
            }

            list.Move(from, to);
            IsDirty = true;
        }

        // Rebuilds the draft list from whatever the live AppSettings.Tabs currently are - used
        // both at construction and whenever unsaved changes are discarded.
        private void LoadDraftFromLive()
        {
            foreach (var draft in AllDrafts)
            {
                draft.Changed -= OnDraftChanged;
            }

            AppTabs.Clear();
            BuildTabs.Clear();

            foreach (var settings in _appSettings.Tabs.OrderBy(t => t.Order))
            {
                var draft = DraftTabViewModel.FromSettings(settings);
                draft.Changed += OnDraftChanged;
                (draft.IsBuildSection ? BuildTabs : AppTabs).Add(draft);
            }

            RefreshDeleteGuards();

            // Set the backing field directly (not the RunAsAdministrator property) so this load
            // doesn't itself flip IsDirty true one line before it's reset below - same reasoning
            // as every draft's fields being set from FromSettings rather than through a setter.
            _runAsAdministrator = _appSettings.RunAsAdministrator;
            OnPropertyChanged(nameof(RunAsAdministrator));

            _maintenanceSheetUrl = _appSettings.MaintenanceSheetUrl;
            OnPropertyChanged(nameof(MaintenanceSheetUrl));

            _buildRepoFolderUrl = _appSettings.BuildRepoFolderUrl;
            OnPropertyChanged(nameof(BuildRepoFolderUrl));

            IsDirty = false;
        }

        private void OnDraftChanged()
        {
            RefreshDeleteGuards();
            IsDirty = true;
        }

        // Called by MainViewModel's tab-switch guard when the user chooses to leave Settings
        // without saving. Live AppSettings.Tabs was never touched, so this is just a fresh clone
        // of it - every unsaved edit, add, delete, and reorder simply disappears.
        public void DiscardChanges() => LoadDraftFromLive();

        // Called by MainViewModel every time the user switches INTO the Settings tab. The draft is
        // only ever otherwise refreshed at construction or after a Save/discard - so a live-side
        // change made outside the normal Settings save flow (BuildSectionViewModel writes
        // VersionNumber/LastSelectedExecutable/LastVersionPushedToLive straight onto the live
        // TabSettings for New Build/Patch/Pushed to Live - see TabSettings.cs) would otherwise sit
        // unreflected in an already-open Settings draft until the next Save/discard, even though the
        // main tab showing that same TabSettings updates immediately. Re-syncing on every visit
        // closes that gap. No-op while IsDirty, so simply clicking into Settings to peek at
        // something never discards an in-progress edit on some other tab's row.
        public void RefreshFromLiveIfClean()
        {
            if (!IsDirty)
            {
                LoadDraftFromLive();
            }
        }

        public void Save()
        {
            var liveById = _appSettings.Tabs.ToDictionary(t => t.Id);
            var newLiveList = new List<TabSettings>();
            var drafts = AllDrafts.ToList();

            for (int i = 0; i < drafts.Count; i++)
            {
                var draft = drafts[i];

                if (draft.IsMarkedForDeletion)
                {
                    continue;
                }

                // Settings itself can never be hidden - it's the only way back into Settings -
                // regardless of whatever the draft's checkbox says. Build tabs no longer have a
                // visibility toggle (they're never in the main tab strip), so they're just left
                // true rather than carrying a stale hidden flag from an older settings.json.
                bool isVisible = draft.Kind == TabKind.Settings || draft.IsBuildSection ? true : draft.IsVisible;

                if (liveById.TryGetValue(draft.Id, out var live))
                {
                    live.Title = draft.Title;
                    live.IsVisible = isVisible;
                    live.Order = i;
                    live.CustomIconPath = draft.CustomIconPath;

                    if (draft.IsBuildSection)
                    {
                        live.BuildPath = draft.BuildPath;
                        live.VersionNumber = draft.VersionNumber;
                        live.LastVersionPushedToLive = draft.LastVersionPushedToLive;
                        live.SupportsPushedToLive = draft.SupportsPushedToLive;
                        live.Servers = CommitServers(draft);
                        live.Executables = CommitExecutables(draft);
                    }

                    newLiveList.Add(live);
                }
                else
                {
                    // Brand-new tab created this session.
                    newLiveList.Add(new TabSettings
                    {
                        Id = draft.Id,
                        Kind = draft.Kind,
                        Category = draft.Category,
                        IsPermanent = false,
                        Title = draft.Title,
                        IsVisible = isVisible,
                        Order = i,
                        CustomIconPath = draft.CustomIconPath,
                        BuildPath = draft.BuildPath,
                        VersionNumber = draft.VersionNumber,
                        LastVersionPushedToLive = draft.LastVersionPushedToLive,
                        SupportsPushedToLive = draft.SupportsPushedToLive,
                        Servers = CommitServers(draft),
                        Executables = CommitExecutables(draft)
                    });
                }
            }

            _appSettings.Tabs = newLiveList;
            _appSettings.RunAsAdministrator = RunAsAdministrator;
            _appSettings.MaintenanceSheetUrl = string.IsNullOrWhiteSpace(MaintenanceSheetUrl)
                ? MaintenanceDefaults.SheetUrl
                : MaintenanceSheetUrl.Trim();
            _appSettings.BuildRepoFolderUrl = string.IsNullOrWhiteSpace(BuildRepoFolderUrl)
                ? BuildRepoDefaults.RootFolderUrl
                : BuildRepoFolderUrl.Trim();
            _settingsService.Save(_appSettings);

            // Clear IsDirty (via LoadDraftFromLive) BEFORE notifying MainViewModel of the commit.
            // OnTabsCommitted() calls TabsView.Refresh(), which can make WPF push a SelectedItem
            // change back into MainViewModel.SelectedTab's setter re-entrantly while this very
            // Save() call is still on the stack. That re-entrant call re-checks Settings.IsDirty -
            // if it were still true at that point (as it used to be, back when this ran after
            // _onTabsCommitted), it would pop a second "unsaved changes" prompt on top of this
            // save, which is what caused the "saves but won't leave the tab" loop.
            LoadDraftFromLive();
            StatusText = $"Saved at {DateTime.Now:t}.";

            _onTabsCommitted();
        }

        // Draft server rows marked for deletion (only ever possible for a Custom entry - see
        // DraftServerViewModel.CanDelete) are dropped here rather than carried into the live list;
        // everything else - built-in and custom, enabled or not - is committed as-is, in whatever
        // order the draft has them.
        private static List<TabServerEntry> CommitServers(DraftTabViewModel draft) =>
            draft.Servers.Where(s => !s.IsMarkedForDeletion).Select(s => s.ToEntry()).ToList();

        // No IsMarkedForDeletion here - DraftTabViewModel.RescanExecutables already keeps
        // draft.Executables trimmed to whatever's actually in the build folder right now, so
        // everything left in it (enabled or not) is committed as-is.
        private static List<TabExecutableEntry> CommitExecutables(DraftTabViewModel draft) =>
            draft.Executables.Select(e => e.ToEntry()).ToList();
    }
}
