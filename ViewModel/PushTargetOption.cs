using System;
using System.ComponentModel;
using CommandCenter.Model;

namespace CommandCenter.ViewModel
{
    // One radio-button choice in the Live Service section's "Pushed to Live" mode: an existing
    // build-section tab (GMS, CMS, or any extra - see BuildSectionViewModel.RebuildPushTargets for
    // how the list is built and kept excluding Server Status/Settings/Live itself) whose current
    // build folder can be moved straight into Live instead of browsing to a folder manually.
    //
    // Wraps the tab's live TabSettings (not a snapshot) so DisplayName stays current if the tab is
    // renamed or its version number changes while this option is on screen - important since the
    // whole point of picking one of these is to read whatever version that tab is *currently* set
    // to at the moment Push to Live actually runs.
    public class PushTargetOption : ViewModelBase
    {
        private bool _isSelected;

        // The actual live BuildSectionViewModel for this candidate tab (tabInfo.Content in
        // MainViewModel.Tabs), when the caller has one to hand over - null only for a
        // PushTargetOption constructed without it (there's no other caller today, but this stays
        // optional rather than required so a future caller isn't forced to have one).
        //
        // Added specifically so BuildSectionViewModel.PushToLiveAsync can ask the SOURCE tab's own
        // ViewModel what it actually thinks its current Documents folder is (SourceViewModel?.
        // DocumentsFolderPath) instead of only trusting a value recomputed from scratch via
        // DocumentsService formulas. Recomputing "should" always agree (both read the very same
        // TabSettings instance - see RebuildPushTargets/CreateBuildSectionViewModel, which both use
        // the identical TabSettings reference), but this exact area has produced two different real
        // bugs that "should always agree" reasoning didn't predict - see
        // pushed_to_live_documents_and_executables_sync.md CORRECTIONS. Going straight to the
        // source of truth removes that whole class of doubt rather than re-deriving it and hoping.
        public BuildSectionViewModel? SourceViewModel { get; }

        public PushTargetOption(TabSettings settings, BuildSectionViewModel? sourceViewModel = null)
        {
            Settings = settings;
            SourceViewModel = sourceViewModel;
            Settings.PropertyChanged += Settings_PropertyChanged;
        }

        public TabSettings Settings { get; }

        // Bound two-way to this option's RadioButton, sharing a GroupName with every other push-
        // source RadioButton (including "Folder to Push to Live") in BuildSectionView.xaml. WPF's
        // own radio-group mutual exclusion unchecks the others automatically, which flows back
        // through their own two-way bindings - see BuildSectionViewModel.SelectedPushTarget, which
        // only needs to react to a checked=true transition (Selected, below).
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value) && value)
                {
                    Selected?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        // Applies a selection state decided elsewhere (BuildSectionViewModel.SelectedPushTarget's
        // setter, reconciling every option after one of them changes) without re-raising Selected -
        // that event is only for a RadioButton the user actually just checked.
        internal void SetSelectedSilently(bool value) => SetProperty(ref _isSelected, value, nameof(IsSelected));

        // e.g. "GMS (271.0.3)" or "GMS (Version not set)" - shown as the RadioButton's own label,
        // per "show the version number set by the user" alongside the tab's name.
        public string DisplayName => string.IsNullOrWhiteSpace(Settings.VersionNumber)
            ? $"{Settings.Title} (Version not set)"
            : $"{Settings.Title} ({Settings.VersionNumber})";

        // Just the version half, for the "Version Number Will Be Set To" readout under the radio
        // list once this option is selected - see BuildSectionView.xaml.
        public string VersionDisplay => string.IsNullOrWhiteSpace(Settings.VersionNumber) ? "Not set" : Settings.VersionNumber;

        public event EventHandler? Selected;

        private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TabSettings.Title) || e.PropertyName == nameof(TabSettings.VersionNumber))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(VersionDisplay));
            }
        }

        // Called by BuildSectionViewModel.RebuildPushTargets right before discarding an option (its
        // tab was deleted, or the whole list is being rebuilt from scratch) so it stops reacting to
        // a TabSettings instance no longer shown on screen - otherwise this would keep listening
        // via Settings.PropertyChanged for as long as the tab itself lives, well past its own
        // removal from PushTargets.
        public void Detach() => Settings.PropertyChanged -= Settings_PropertyChanged;
    }
}
