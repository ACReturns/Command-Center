using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommandCenter.Model;

namespace CommandCenter.ViewModel
{
    // Owns the DRAFT copy of every tab's settings while the Settings tab is open (DraftTabs).
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
            MoveTabUpCommand = new RelayCommand(param => MoveTab(param as DraftTabViewModel, -1), param => CanMove(param as DraftTabViewModel, -1));
            MoveTabDownCommand = new RelayCommand(param => MoveTab(param as DraftTabViewModel, 1), param => CanMove(param as DraftTabViewModel, 1));

            LoadDraftFromLive();
        }

        // Every tab's draft, in display order - Settings' own row is in here like any other.
        public ObservableCollection<DraftTabViewModel> DraftTabs { get; } = new();

        // True the moment any draft field changes, a tab is added/deleted/restored, or the order
        // changes - cleared by Save() or DiscardChanges(). MainViewModel reads this to decide
        // whether switching away from Settings needs to warn the user first.
        public bool IsDirty
        {
            get => _isDirty;
            private set => SetProperty(ref _isDirty, value);
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
        public RelayCommand MoveTabUpCommand { get; }
        public RelayCommand MoveTabDownCommand { get; }

        // No more GMS/CMS/Live picker - extras are independent top-level tabs now, not nested
        // under a parent category, so there's nothing meaningful to pick. Every tab created here
        // is a plain SectionCategory.General build section; the user fills in its Title,
        // VersionNumber, and BuildPath directly on the row that appears (same fields any other
        // build-section tab has).
        private void AddTab()
        {
            const SectionCategory category = SectionCategory.General;
            int existingCount = DraftTabs.Count(d => d.Category == category && !d.IsMarkedForDeletion);
            var draft = DraftTabViewModel.CreateNew(category, $"New Tab {existingCount + 1}");
            draft.Changed += OnDraftChanged;
            DraftTabs.Add(draft);
            IsDirty = true;
        }

        private bool CanMove(DraftTabViewModel? draft, int delta)
        {
            if (draft == null)
            {
                return false;
            }

            int index = DraftTabs.IndexOf(draft);
            int target = index + delta;
            return index >= 0 && target >= 0 && target < DraftTabs.Count;
        }

        private void MoveTab(DraftTabViewModel? draft, int delta)
        {
            if (!CanMove(draft, delta))
            {
                return;
            }

            int index = DraftTabs.IndexOf(draft!);
            DraftTabs.Move(index, index + delta);
            IsDirty = true;
        }

        // Rebuilds the draft list from whatever the live AppSettings.Tabs currently are - used
        // both at construction and whenever unsaved changes are discarded.
        private void LoadDraftFromLive()
        {
            foreach (var draft in DraftTabs)
            {
                draft.Changed -= OnDraftChanged;
            }

            DraftTabs.Clear();

            foreach (var settings in _appSettings.Tabs.OrderBy(t => t.Order))
            {
                var draft = DraftTabViewModel.FromSettings(settings);
                draft.Changed += OnDraftChanged;
                DraftTabs.Add(draft);
            }

            IsDirty = false;
        }

        private void OnDraftChanged() => IsDirty = true;

        // Called by MainViewModel's tab-switch guard when the user chooses to leave Settings
        // without saving. Live AppSettings.Tabs was never touched, so this is just a fresh clone
        // of it - every unsaved edit, add, delete, and reorder simply disappears.
        public void DiscardChanges() => LoadDraftFromLive();

        public void Save()
        {
            var liveById = _appSettings.Tabs.ToDictionary(t => t.Id);
            var newLiveList = new List<TabSettings>();

            for (int i = 0; i < DraftTabs.Count; i++)
            {
                var draft = DraftTabs[i];

                if (draft.IsMarkedForDeletion)
                {
                    continue;
                }

                // Settings itself can never be hidden - it's the only way back into Settings -
                // regardless of whatever the draft's checkbox says.
                bool isVisible = draft.Kind == TabKind.Settings ? true : draft.IsVisible;

                if (liveById.TryGetValue(draft.Id, out var live))
                {
                    live.Title = draft.Title;
                    live.IsVisible = isVisible;
                    live.Order = i;

                    if (draft.IsBuildSection)
                    {
                        live.BuildPath = draft.BuildPath;
                        live.VersionNumber = draft.VersionNumber;
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
                        BuildPath = draft.BuildPath,
                        VersionNumber = draft.VersionNumber,
                        Servers = CommitServers(draft),
                        Executables = CommitExecutables(draft)
                    });
                }
            }

            _appSettings.Tabs = newLiveList;
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
