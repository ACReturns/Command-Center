using CommandCenter.Model;

namespace CommandCenter.ViewModel
{
    // One entry in MainViewModel.Tabs/TabsView - pairs a persisted TabSettings with the content
    // ViewModel WPF actually renders for it (BuildSectionViewModel / ServerStatusViewModel /
    // SettingsViewModel). MainWindow.xaml's TabControl binds its header to Title/IconSource here,
    // and its body to Content, which resolves to the right View via an implicit DataTemplate
    // keyed by Content's runtime type (see MainWindow.xaml's TabControl.Resources).
    //
    // Title/IsVisible/Order are simple pass-throughs onto the wrapped TabSettings, forwarded as
    // this object's own property-changed notifications so the tab strip (header text, and the
    // sort/filter MainViewModel.TabsView applies) reacts once Settings actually commits a change
    // - see SettingsViewModel.Save, which is the only thing that ever mutates a live TabSettings.
    public class TabInfo : ViewModelBase
    {
        public TabInfo(TabSettings settings, object content)
        {
            Settings = settings;
            Content = content;

            settings.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(TabSettings.Title):
                        OnPropertyChanged(nameof(Title));
                        break;
                    case nameof(TabSettings.IsVisible):
                        OnPropertyChanged(nameof(IsVisible));
                        break;
                    case nameof(TabSettings.Order):
                        OnPropertyChanged(nameof(Order));
                        break;
                    case nameof(TabSettings.CustomIconPath):
                        OnPropertyChanged(nameof(IconSource));
                        break;
                }
            };
        }

        public TabSettings Settings { get; }
        public object Content { get; }

        public string Title => Settings.Title;
        public bool IsVisible => Settings.IsVisible;
        public int Order => Settings.Order;

        // Live, not cached - reacts to CustomIconPath changing (Settings' "Change Icon"/"Use
        // Default", see DraftTabViewModel) via the PropertyChanged forwarding above, same pattern
        // Title already uses. See TabIconCatalog.IconFor for the Kind/Category default this falls
        // back to.
        public string IconSource => TabIconCatalog.IconFor(Settings);
    }
}
