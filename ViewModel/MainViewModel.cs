using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommandCenter.Model;
using CommandCenter.Services;
using CommandCenter.View;

namespace CommandCenter.ViewModel
{
    public class MainViewModel : ViewModelBase
    {
        private readonly AppSettings _appSettings;
        private readonly SettingsService _settingsService;

        // How often the storage tracker re-checks its current drive on its own, so free space
        // changing from outside the app (someone clearing files on that drive in Explorer, another
        // tool writing to it) eventually shows up without anyone touching Command Center. A single
        // DriveInfo.AvailableFreeSpace read is one cheap OS call, not a directory scan, so this
        // interval is chosen for a steady, unobtrusive tracker rather than to spare the system -
        // it could safely run much more often if tighter feedback is ever wanted.
        private static readonly TimeSpan StorageRecheckInterval = TimeSpan.FromHours(1);

        private readonly DispatcherTimer _storageRecheckTimer;

        private TabInfo? _selectedTab;
        private DiskSpaceStatus? _storageStatus;

        // Guards SelectedTab's setter against reentrancy. Saving from the unsaved-changes prompt
        // (Settings.Save() -> OnSettingsTabsCommitted() -> TabsView.Refresh()) can make WPF push
        // another SelectedItem change back into this same setter before the outer call has
        // finished - that inner call used to see Settings.IsDirty still true (it hadn't been
        // cleared yet) and pop a second prompt on top of the first, which is the reported
        // "saves but won't leave the tab / infinite loop" bug. While this flag is set, the outer
        // call already owns resolving the final selection, so any reentrant call is a no-op.
        private bool _isResolvingTabSwitch;

        // Build path behind the tracker's current reading (startup pick, or whichever section's
        // build/patch/push completed most recently) - the periodic recheck re-checks this same
        // path so it never jumps the tracker to a different drive on its own.
        private string _lastCheckedBuildPath = string.Empty;

        public MainViewModel()
        {
            _settingsService = new SettingsService();
            _appSettings = _settingsService.Load();

            Settings = new SettingsViewModel(_appSettings, _settingsService, OnSettingsTabsCommitted);

            // "Go back to a build tab" - used by Server Status' Close action. Falls back to any
            // visible tab in the (extremely unlikely) case no BuildSection tab is currently visible.
            ServerStatus = new ServerStatusViewModel(() =>
                SelectedTab = Tabs.FirstOrDefault(t => t.IsVisible && t.Settings.Kind == TabKind.BuildSection)
                    ?? Tabs.FirstOrDefault(t => t.IsVisible));

            foreach (var tabSettings in _appSettings.Tabs.OrderBy(t => t.Order))
            {
                Tabs.Add(CreateTabInfo(tabSettings));
            }

            // What MainWindow's TabControl actually binds to: every tab, sorted by Order and
            // filtered down to the currently-visible ones. Refreshed explicitly after Settings
            // commits a change (OnSettingsTabsCommitted) - order/visibility only ever change as
            // part of a Save, never continuously, so a one-shot Refresh() there is enough; no
            // live-shaping needed.
            TabsView = CollectionViewSource.GetDefaultView(Tabs);
            TabsView.SortDescriptions.Add(new SortDescription(nameof(TabInfo.Order), ListSortDirection.Ascending));
            TabsView.Filter = o => o is TabInfo t && t.IsVisible;

            _selectedTab = Tabs.FirstOrDefault(t => t.IsVisible);

            // Startup check so the tracker has a real number before any build ever runs, rather
            // than sitting blank until the first extraction. Uses whichever BuildSection tab
            // (in the user's own tab order) already has a configured build path.
            BuildSectionViewModel? startupSection = Tabs
                .Select(t => t.Content as BuildSectionViewModel)
                .FirstOrDefault(vm => vm != null && vm.HasBuildPath);

            if (startupSection != null)
            {
                _lastCheckedBuildPath = startupSection.CurrentBuildPath;
                StorageStatus = DiskSpaceService.CheckDiskSpace(_lastCheckedBuildPath);
            }

            _storageRecheckTimer = new DispatcherTimer { Interval = StorageRecheckInterval };
            _storageRecheckTimer.Tick += (_, _) => RecheckStorage();
            _storageRecheckTimer.Start();
        }

        public SettingsViewModel Settings { get; }
        public ServerStatusViewModel ServerStatus { get; }

        // Every top-level tab - GMS/CMS/Live/Server Status/Settings and any extra - regardless of
        // visibility. TabsView (sorted + filtered to IsVisible) is what the TabControl renders.
        public ObservableCollection<TabInfo> Tabs { get; } = new();
        public ICollectionView TabsView { get; }

        public TabInfo? SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (ReferenceEquals(_selectedTab, value))
                {
                    return;
                }

                if (_isResolvingTabSwitch)
                {
                    return;
                }

                TabInfo? target = value;

                // Leaving the Settings tab with unsaved changes - warn before allowing the
                // switch. Only 2 ways out: Save Settings (then proceed to the tab that was
                // clicked), or Cancel (stay on Settings, changes untouched).
                if (_selectedTab != null && ReferenceEquals(_selectedTab.Content, Settings) && Settings.IsDirty)
                {
                    _isResolvingTabSwitch = true;
                    try
                    {
                        bool save = UnsavedChangesDialog.PromptSave(Application.Current?.MainWindow);

                        if (!save)
                        {
                            // Cancel: nothing changes - just force the TabControl's visual
                            // selection back to Settings, since the click already moved it.
                            OnPropertyChanged(nameof(SelectedTab));
                            return;
                        }

                        Settings.Save();

                        // The tab the user was switching to might itself have just been deleted as
                        // part of that save (e.g. marked for deletion, then clicked before saving) -
                        // fall back to any visible tab rather than pointing at one that no longer exists.
                        if (target != null && !Tabs.Contains(target))
                        {
                            target = Tabs.FirstOrDefault(t => t.IsVisible);
                        }
                    }
                    finally
                    {
                        _isResolvingTabSwitch = false;
                    }
                }

                _selectedTab = target;
                OnPropertyChanged(nameof(SelectedTab));
            }
        }

        public DiskSpaceStatus? StorageStatus
        {
            get => _storageStatus;
            set
            {
                if (SetProperty(ref _storageStatus, value))
                {
                    OnPropertyChanged(nameof(StorageTrackerText));
                    OnPropertyChanged(nameof(IsStorageLow));
                }
            }
        }

        public string StorageTrackerText => StorageStatus switch
        {
            null => "Storage: no build path configured yet.",
            { IsLow: true } s => $"Low disk space on drive {s.DriveLabel} — only {s.FreeGigabytes:F1} GB left. Be mindful if you need to pull another build; you'll need to free up space first.",
            { } s => $"Drive {s.DriveLabel}: {s.FreeGigabytes:F1} GB free"
        };

        public bool IsStorageLow => StorageStatus?.IsLow ?? false;

        private void OnDiskSpaceStatusChanged(object? sender, DiskSpaceStatus? status)
        {
            if (sender is BuildSectionViewModel section)
            {
                _lastCheckedBuildPath = section.CurrentBuildPath;
            }

            StorageStatus = status;
        }

        // Timer tick: re-reads free space on whatever drive the tracker is currently showing, so
        // external changes (space freed up or eaten elsewhere) surface without a build having to
        // run. No-ops until something has actually been checked at least once.
        private void RecheckStorage()
        {
            if (string.IsNullOrWhiteSpace(_lastCheckedBuildPath))
            {
                return;
            }

            StorageStatus = DiskSpaceService.CheckDiskSpace(_lastCheckedBuildPath);
        }

        // Called by SettingsViewModel right after a Save has committed - _appSettings.Tabs now
        // reflects the final set/order/visibility/content. Reconciles the live Tabs collection to
        // match: tears down and removes any tab that's gone, creates a fresh TabInfo (and, for a
        // BuildSection tab, a fresh BuildSectionViewModel) for anything brand new. Everything that
        // survived already picked up its Title/BuildPath/VersionNumber/IsVisible/Order changes on
        // its own, since Settings mutated the very same TabSettings instance that tab's TabInfo/
        // BuildSectionViewModel already wraps - only the sort/filter needs a nudge (Refresh) since
        // those don't re-run automatically just because a property on an existing item changed.
        private void OnSettingsTabsCommitted()
        {
            var liveIds = new HashSet<Guid>(_appSettings.Tabs.Select(t => t.Id));

            foreach (var tabInfo in Tabs.Where(t => !liveIds.Contains(t.Settings.Id)).ToList())
            {
                TeardownTab(tabInfo);
                Tabs.Remove(tabInfo);

                if (ReferenceEquals(SelectedTab, tabInfo))
                {
                    SelectedTab = Tabs.FirstOrDefault(t => t.IsVisible);
                }
            }

            var existingIds = new HashSet<Guid>(Tabs.Select(t => t.Settings.Id));
            foreach (var settings in _appSettings.Tabs.Where(s => !existingIds.Contains(s.Id)))
            {
                Tabs.Add(CreateTabInfo(settings));
            }

            TabsView.Refresh();
        }

        private TabInfo CreateTabInfo(TabSettings settings)
        {
            object content = settings.Kind switch
            {
                TabKind.BuildSection => CreateBuildSectionViewModel(settings),
                TabKind.ServerStatus => ServerStatus,
                TabKind.Settings => Settings,
                _ => throw new InvalidOperationException($"Unknown tab kind: {settings.Kind}")
            };

            if (content is BuildSectionViewModel vm)
            {
                vm.DiskSpaceStatusChanged += OnDiskSpaceStatusChanged;
            }

            return new TabInfo(settings, content, IconFor(settings));
        }

        // A General-category tab (everything "+ Add Tab" creates now) gets no preset server list
        // and no "Pushed to Live" option - both are still tied to a real Gms/Cms/Live category,
        // which a plain extra tab no longer has.
        private BuildSectionViewModel CreateBuildSectionViewModel(TabSettings settings) =>
            new BuildSectionViewModel(settings, _appSettings, _settingsService,
                LaunchServerCatalog.ServersFor(settings.Category), supportsPushedToLive: settings.Category == SectionCategory.Live);

        // A deleted tab's documents shouldn't outlive it - "we don't want to keep anything from
        // the extra builds." Never actually reached for the 5 permanent tabs, which can't be
        // marked for deletion in the first place (DraftTabViewModel.DeleteCommand is disabled for
        // IsPermanent tabs).
        private void TeardownTab(TabInfo tabInfo)
        {
            if (tabInfo.Content is BuildSectionViewModel vm)
            {
                vm.DiskSpaceStatusChanged -= OnDiskSpaceStatusChanged;
                vm.StopWatching();
                vm.DeleteDocumentsFolder();
            }
        }

        private static string IconFor(TabSettings settings) => settings.Kind switch
        {
            TabKind.ServerStatus => "img/Servers.ico",
            TabKind.Settings => "img/Settings.ico",
            TabKind.BuildSection => settings.Category switch
            {
                SectionCategory.Gms => "img/Maple.ico",
                SectionCategory.Cms => "img/Classic.ico",
                SectionCategory.Live => "img/Live.ico",
                SectionCategory.General => "img/AppIcon.ico",
                _ => "img/AppIcon.ico"
            },
            _ => "img/AppIcon.ico"
        };
    }
}
