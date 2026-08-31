using System.Windows;

namespace CommandCenter.View
{
    // Server Status → a custom group's "Rename" button - collects the new display name.
    // ServerStatusViewModel.RenameServer applies it to the group and its settings entry once
    // PromptForName returns true.
    public partial class RenameServerDialog : Window
    {
        public RenameServerDialog()
        {
            InitializeComponent();
        }

        public string ServerName => NameTextBox.Text.Trim();

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ServerName))
            {
                MessageBox.Show(this, "Enter a name for the server.", "Rename Server", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // Shows the dialog modally, pre-filled with the current name; returns true and the new
        // name iff the user clicked Rename.
        public static bool PromptForName(Window? owner, string currentName, out string newName)
        {
            var dialog = new RenameServerDialog();
            dialog.NameTextBox.Text = currentName;

            if (owner != null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            bool result = dialog.ShowDialog() == true;
            newName = result ? dialog.ServerName : string.Empty;
            return result;
        }
    }
}
