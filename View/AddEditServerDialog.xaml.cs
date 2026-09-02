using System.Windows;
using CommandCenter.Model;

namespace CommandCenter.View
{
    // Settings -> a BuildSection tab's "+ Add Custom Server", or a custom entry's "Edit" - collects
    // a display name plus enough connection detail to build a LaunchArgument (see
    // TabServerEntry.LaunchArgument / DraftServerViewModel). Doesn't touch any tab's settings
    // itself - the caller (DraftTabViewModel.AddCustomServer / DraftServerViewModel.Edit) applies
    // whatever this returns once the dialog confirms. Same static-prompt convention as
    // AddServerDialog/RenameServerDialog elsewhere in View.
    public partial class AddEditServerDialog : Window
    {
        public AddEditServerDialog()
        {
            InitializeComponent();
        }

        public string ServerName => NameTextBox.Text.Trim();

        public LaunchMode SelectedMode =>
            IpPortRadio.IsChecked == true ? LaunchMode.IpPort :
            RawRadio.IsChecked == true ? LaunchMode.Raw :
            LaunchMode.GameLaunching;

        public string Host => HostTextBox.Text.Trim();
        public string Port => PortTextBox.Text.Trim();
        public string RawArgument => RawTextBox.Text.Trim();

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        }

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            // The RadioButtons' IsChecked (GameLaunchingRadio="True" in XAML) can fire this during
            // InitializeComponent, before HostPortPanel/RawPanel have been assigned yet.
            if (HostPortPanel == null || RawPanel == null)
            {
                return;
            }

            bool isRaw = RawRadio.IsChecked == true;
            RawPanel.Visibility = isRaw ? Visibility.Visible : Visibility.Collapsed;
            HostPortPanel.Visibility = isRaw ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ServerName))
            {
                MessageBox.Show(this, "Enter a name for the server.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (SelectedMode == LaunchMode.Raw)
            {
                if (string.IsNullOrWhiteSpace(RawArgument))
                {
                    MessageBox.Show(this, "Enter the launch argument to pass to the client executable.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            else if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Port))
            {
                MessageBox.Show(this, "Enter both a host/IP and a port.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // Shows the dialog modally, pre-filled with whatever the caller already has (blank for
        // "+ Add Custom Server", the entry's current values for "Edit"); returns true and the
        // entered values iff the user clicked Save.
        public static bool PromptForServer(string title, string initialName, LaunchMode initialMode,
            string initialHost, string initialPort, string initialRawArgument,
            out string displayName, out LaunchMode mode, out string host, out string port, out string rawArgument)
        {
            var dialog = new AddEditServerDialog { Title = title };
            dialog.NameTextBox.Text = initialName;
            dialog.HostTextBox.Text = initialHost;
            dialog.PortTextBox.Text = initialPort;
            dialog.RawTextBox.Text = initialRawArgument;

            switch (initialMode)
            {
                case LaunchMode.IpPort:
                    dialog.IpPortRadio.IsChecked = true;
                    break;
                case LaunchMode.Raw:
                    dialog.RawRadio.IsChecked = true;
                    break;
                default:
                    dialog.GameLaunchingRadio.IsChecked = true;
                    break;
            }

            bool result = dialog.ShowDialog() == true;
            displayName = result ? dialog.ServerName : string.Empty;
            mode = result ? dialog.SelectedMode : initialMode;
            host = result ? dialog.Host : string.Empty;
            port = result ? dialog.Port : string.Empty;
            rawArgument = result ? dialog.RawArgument : string.Empty;
            return result;
        }
    }
}
