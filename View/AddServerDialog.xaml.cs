using System.Windows;
using Microsoft.Win32;

namespace CommandCenter.View
{
    // "Add New Server" on the Server Status tab - collects a display name and a worlds json file.
    // Doesn't touch the filesystem itself beyond letting the user pick a file; the copy into the
    // Servers folder and the new group's creation happen in ServerStatusViewModel.AddServer once
    // PromptForNewServer returns true.
    public partial class AddServerDialog : Window
    {
        public AddServerDialog()
        {
            InitializeComponent();
        }

        public string ServerName => NameTextBox.Text.Trim();
        public string SourceFilePath { get; private set; } = string.Empty;

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select server status json",
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                SourceFilePath = dialog.FileName;
                FilePathTextBox.Text = dialog.FileName;
            }
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ServerName))
            {
                MessageBox.Show(this, "Enter a name for the server.", "Add New Server", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(SourceFilePath))
            {
                MessageBox.Show(this, "Choose a server status json file.", "Add New Server", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // Shows the dialog modally; returns true and the chosen name/file iff the user clicked Add.
        public static bool PromptForNewServer(Window? owner, out string title, out string sourcePath)
        {
            var dialog = new AddServerDialog();

            if (owner != null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            bool result = dialog.ShowDialog() == true;
            title = result ? dialog.ServerName : string.Empty;
            sourcePath = result ? dialog.SourceFilePath : string.Empty;
            return result;
        }
    }
}
