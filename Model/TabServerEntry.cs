using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CommandCenter.Model
{
    // One entry in a BuildSection tab's launch dropdown (see TabSettings.Servers and
    // BuildSectionViewModel.ServerOptions). Replaces the old static LaunchServerOption - every tab
    // now carries its own persisted, editable list instead of sharing one fixed catalog per
    // SectionCategory, which is what makes "+ Add Tab" (SectionCategory.General, previously stuck
    // with an empty, un-editable server list) able to have its own servers at all.
    //
    // Two ways an entry gets here: seeded as Source = BuiltIn from LaunchServerCatalog (the "known
    // good" registry) when a Gms/Cms/Live tab is created or migrated, or added by hand as
    // Source = Custom via Settings' "+ Add Custom Server" form. Both are edited/toggled the same
    // way from there - see DraftServerViewModel.
    public class TabServerEntry : INotifyPropertyChanged
    {
        private string _displayName = string.Empty;
        private LaunchMode _mode = LaunchMode.GameLaunching;
        private string _host = string.Empty;
        private string _port = string.Empty;
        private string _rawArgument = string.Empty;
        private bool _isEnabled = true;

        public Guid Id { get; set; } = Guid.NewGuid();

        // BuiltIn can be disabled but never deleted; Custom can be freely edited/removed - see
        // ServerEntrySource and DraftServerViewModel.CanDelete.
        public ServerEntrySource Source { get; set; } = ServerEntrySource.Custom;

        public string DisplayName
        {
            get => _displayName;
            set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
        }

        // GameLaunching/IpPort: Host+Port compose LaunchArgument below as "<keyword> <host> <port>".
        // Raw: LaunchArgument is RawArgument, typed in directly - Host/Port are unused.
        public LaunchMode Mode
        {
            get => _mode;
            set { if (_mode != value) { _mode = value; OnPropertyChanged(); OnPropertyChanged(nameof(LaunchArgument)); } }
        }

        public string Host
        {
            get => _host;
            set { if (_host != value) { _host = value; OnPropertyChanged(); OnPropertyChanged(nameof(LaunchArgument)); } }
        }

        public string Port
        {
            get => _port;
            set { if (_port != value) { _port = value; OnPropertyChanged(); OnPropertyChanged(nameof(LaunchArgument)); } }
        }

        public string RawArgument
        {
            get => _rawArgument;
            set { if (_rawArgument != value) { _rawArgument = value; OnPropertyChanged(); OnPropertyChanged(nameof(LaunchArgument)); } }
        }

        // Whether this entry shows up in its tab's launch dropdown right now. For a BuiltIn entry
        // this is the "Connect" toggle from Settings' browsable registry list; for a Custom entry
        // it's just a way to keep an entry around without it cluttering the dropdown.
        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(); } }
        }

        // What actually gets passed to the client executable at launch time - see
        // BuildSectionViewModel.Launch. Computed rather than stored so editing Mode/Host/Port can
        // never leave a stale argument behind; not persisted (there's nothing to deserialize back
        // into a computed property, and re-deriving it from Mode/Host/Port/RawArgument is trivial).
        [JsonIgnore]
        public string LaunchArgument => Mode switch
        {
            LaunchMode.GameLaunching => $"GameLaunching {Host} {Port}".Trim(),
            LaunchMode.IpPort => $"ipport {Host} {Port}".Trim(),
            _ => RawArgument
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
