using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using CommandCenter.Model;

namespace CommandCenter.ViewModel
{
    // The Launcher tab: one place to reach every BuildSection tab's launch-side tools (Launch,
    // Debug Command List, Existing Equipment Creation, Documents) without each build needing its
    // own top-level tab. New Build / Patch now live in the Build Repo tab, so the individual build
    // tabs can be hidden in Settings and driven entirely from here + Build Repo.
    //
    // This does NOT own any build state - the dropdown just points at the existing
    // BuildSectionViewModel behind each build tab (the very same instance that tab renders), so
    // picking an executable, editing the debug list or dropping a document here is identical to
    // doing it on that tab, and both stay in sync automatically.
    //
    // Hidden build tabs are deliberately still offered (marked "(hidden)") - hiding them to
    // declutter the tab strip is the whole point of this tab.
    public sealed class LauncherViewModel : ViewModelBase
    {
        private readonly ObservableCollection<TabInfo> _allTabs;
        private readonly AppSettings _appSettings;
        private readonly SettingsService _settingsService;

        // The tab the user actually wants selected (their last pick, or the persisted one from a
        // previous session). Kept separate from _selectedSection so a rebuild that temporarily
        // can't find it (e.g. during startup, before that tab has been created yet) falls back
        // without forgetting it.
        private Guid? _preferredTabId;
        private TabInfo? _selectedSection;

        // While the Sections list is being rebuilt, the ComboBox pushes a null SelectedItem back
        // into SelectedSection the moment the list is cleared - ignore those.
        private bool _isRebuilding;

        public LauncherViewModel(ObservableCollection<TabInfo> allTabs, AppSettings appSettings, SettingsService settingsService)
        {
            _allTabs = allTabs;
            _appSettings = appSettings;
            _settingsService = settingsService;
            _preferredTabId = appSettings.LauncherLastTabId;

            // MainViewModel is still populating allTabs when this is constructed, and tabs are
            // added/removed later via Settings - rebuild on any change so the dropdown is always
            // current. Order changes (a Settings reorder) don't touch the collection itself, so
            // MainViewModel also calls RefreshSections() explicitly after every Settings save.
            _allTabs.CollectionChanged += AllTabs_CollectionChanged;
            RefreshSections();
        }

        // Every BuildSection tab, visible or hidden, in the user's tab order.
        public ObservableCollection<TabInfo> Sections { get; } = new();

        public bool HasSections => Sections.Count > 0;

        public TabInfo? SelectedSection
        {
            get => _selectedSection;
            set
            {
                if (_isRebuilding || ReferenceEquals(_selectedSection, value))
                {
                    return;
                }

                SetSelected(value);

                if (value != null && _preferredTabId != value.Settings.Id)
                {
                    _preferredTabId = value.Settings.Id;
                    _appSettings.LauncherLastTabId = value.Settings.Id;
                    TrySaveSettings();
                }
            }
        }

        // What the panels below the dropdown actually bind to - the selected tab's own view model.
        public BuildSectionViewModel? SelectedBuild => _selectedSection?.Content as BuildSectionViewModel;

        // The selected build tab's icon (custom or default). MainViewModel mirrors this onto the
        // Launcher's own tab-strip icon; null when nothing is selected.
        public string? SelectedIconSource => _selectedSection?.IconSource;

        public void RefreshSections()
        {
            var ordered = _allTabs
                .Where(t => t.Content is BuildSectionViewModel)
                .OrderBy(t => t.Order)
                .ToList();

            TabInfo? current = _selectedSection;

            _isRebuilding = true;
            try
            {
                Sections.Clear();
                foreach (var tab in ordered)
                {
                    Sections.Add(tab);
                }
            }
            finally
            {
                _isRebuilding = false;
            }

            TabInfo? next =
                Sections.FirstOrDefault(t => _preferredTabId.HasValue && t.Settings.Id == _preferredTabId.Value)
                ?? (current != null && Sections.Contains(current) ? current : null)
                ?? Sections.FirstOrDefault();

            // Set through the private path, not the property, so a fallback pick never overwrites
            // the user's saved preference. Always re-raise: the ComboBox lost its selection when
            // the list was cleared, even if the pick itself didn't change.
            _selectedSection = null;
            SetSelected(next);
            OnPropertyChanged(nameof(HasSections));
        }

        private void SetSelected(TabInfo? value)
        {
            if (_selectedSection != null)
            {
                _selectedSection.PropertyChanged -= SelectedSection_PropertyChanged;
            }

            _selectedSection = value;

            if (_selectedSection != null)
            {
                _selectedSection.PropertyChanged += SelectedSection_PropertyChanged;
            }

            OnPropertyChanged(nameof(SelectedSection));
            OnPropertyChanged(nameof(SelectedBuild));
            OnPropertyChanged(nameof(SelectedIconSource));
        }

        // The selected tab's icon can change under us (Settings' "Change Icon" on that tab).
        private void SelectedSection_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TabInfo.IconSource))
            {
                OnPropertyChanged(nameof(SelectedIconSource));
            }
        }

        private void AllTabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Only rebuild when a build tab came or went - the ephemeral Active Clients tab
            // appearing/disappearing shouldn't reset anything here.
            bool touchesBuildTabs =
                (e.NewItems?.OfType<TabInfo>().Any(t => t.Content is BuildSectionViewModel) ?? false)
                || (e.OldItems?.OfType<TabInfo>().Any(t => t.Content is BuildSectionViewModel) ?? false)
                || e.Action == NotifyCollectionChangedAction.Reset;

            if (touchesBuildTabs)
            {
                RefreshSections();
            }
        }

        private void TrySaveSettings()
        {
            try
            {
                _settingsService.Save(_appSettings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Remembering the last pick is a convenience - never worth surfacing an error for.
            }
        }
    }
}
