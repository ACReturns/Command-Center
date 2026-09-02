using System;
using CommandCenter.Model;
using CommandCenter.View;

namespace CommandCenter.ViewModel
{
    // A scratch, editable copy of one TabServerEntry - the server-list equivalent of
    // DraftTabViewModel, and used the same way: lives only while the Settings tab is open (see
    // DraftTabViewModel.Servers), touches nothing on the live TabSettings.Servers until
    // SettingsViewModel.Save() commits it. Discarding unsaved Settings changes just means throwing
    // these away, same as for the tabs themselves.
    public class DraftServerViewModel : ViewModelBase
    {
        private string _displayName;
        private LaunchMode _mode;
        private string _host;
        private string _port;
        private string _rawArgument;
        private bool _isEnabled;
        private bool _isMarkedForDeletion;

        private DraftServerViewModel(Guid id, ServerEntrySource source, string displayName, LaunchMode mode,
            string host, string port, string rawArgument, bool isEnabled)
        {
            Id = id;
            Source = source;
            _displayName = displayName;
            _mode = mode;
            _host = host;
            _port = port;
            _rawArgument = rawArgument;
            _isEnabled = isEnabled;

            EditCommand = new RelayCommand(_ => Edit(), _ => Source == ServerEntrySource.Custom);
            DeleteCommand = new RelayCommand(_ => { IsMarkedForDeletion = true; RaiseChanged(); }, _ => CanDelete);
            RestoreCommand = new RelayCommand(_ => { IsMarkedForDeletion = false; RaiseChanged(); }, _ => IsMarkedForDeletion);
        }

        public static DraftServerViewModel FromEntry(TabServerEntry entry) => new(
            entry.Id, entry.Source, entry.DisplayName, entry.Mode, entry.Host, entry.Port, entry.RawArgument, entry.IsEnabled);

        public static DraftServerViewModel CreateCustom(string displayName, LaunchMode mode, string host, string port, string rawArgument) => new(
            Guid.NewGuid(), ServerEntrySource.Custom, displayName, mode, host, port, rawArgument, isEnabled: true);

        public Guid Id { get; }

        // BuiltIn: seeded from LaunchServerCatalog's registry - can be connected/disconnected via
        // IsEnabled but never edited or deleted, so the registry stays "known good". Custom: added
        // by hand via "+ Add Custom Server" - freely editable and removable.
        public ServerEntrySource Source { get; }
        public bool IsBuiltIn => Source == ServerEntrySource.BuiltIn;

        public bool CanDelete => Source == ServerEntrySource.Custom && !IsMarkedForDeletion;

        public bool IsMarkedForDeletion
        {
            get => _isMarkedForDeletion;
            private set
            {
                if (SetProperty(ref _isMarkedForDeletion, value))
                {
                    OnPropertyChanged(nameof(CanDelete));
                }
            }
        }

        public string DisplayName
        {
            get => _displayName;
            set { if (SetProperty(ref _displayName, value)) RaiseChanged(); }
        }

        public LaunchMode Mode
        {
            get => _mode;
            set { if (SetProperty(ref _mode, value)) { OnPropertyChanged(nameof(LaunchArgumentPreview)); RaiseChanged(); } }
        }

        public string Host
        {
            get => _host;
            set { if (SetProperty(ref _host, value)) { OnPropertyChanged(nameof(LaunchArgumentPreview)); RaiseChanged(); } }
        }

        public string Port
        {
            get => _port;
            set { if (SetProperty(ref _port, value)) { OnPropertyChanged(nameof(LaunchArgumentPreview)); RaiseChanged(); } }
        }

        public string RawArgument
        {
            get => _rawArgument;
            set { if (SetProperty(ref _rawArgument, value)) { OnPropertyChanged(nameof(LaunchArgumentPreview)); RaiseChanged(); } }
        }

        // The "Connect" toggle - Settings shows this as a checkbox for every entry, built-in or
        // custom.
        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (SetProperty(ref _isEnabled, value)) RaiseChanged(); }
        }

        // Shown under each row in Settings so the user can see exactly what will be passed to the
        // client exe without opening the edit dialog - same composition TabServerEntry.LaunchArgument
        // uses.
        public string LaunchArgumentPreview => Mode switch
        {
            LaunchMode.GameLaunching => $"GameLaunching {Host} {Port}".Trim(),
            LaunchMode.IpPort => $"ipport {Host} {Port}".Trim(),
            _ => RawArgument
        };

        public RelayCommand EditCommand { get; }
        public RelayCommand DeleteCommand { get; }
        public RelayCommand RestoreCommand { get; }

        // Fired on any edit (including delete/restore) so the owning DraftTabViewModel can bubble
        // it up to SettingsViewModel.IsDirty - same pattern as DraftTabViewModel.Changed.
        public event Action? Changed;
        private void RaiseChanged() => Changed?.Invoke();

        private void Edit()
        {
            if (AddEditServerDialog.PromptForServer("Edit Server", DisplayName, Mode, Host, Port, RawArgument,
                    out string name, out LaunchMode mode, out string host, out string port, out string raw))
            {
                DisplayName = name;
                Mode = mode;
                Host = host;
                Port = port;
                RawArgument = raw;
            }
        }

        // Applied by SettingsViewModel.Save() into the live TabSettings.Servers list.
        public TabServerEntry ToEntry() => new()
        {
            Id = Id,
            Source = Source,
            DisplayName = DisplayName,
            Mode = Mode,
            Host = Host,
            Port = Port,
            RawArgument = RawArgument,
            IsEnabled = IsEnabled
        };
    }
}
