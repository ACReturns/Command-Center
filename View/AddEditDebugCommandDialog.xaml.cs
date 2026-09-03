using System.Windows;

namespace CommandCenter.View
{
    // Launch panel -> a BuildSection tab's "Enable Debug Command List" area - collects a single
    // command line for that tab's saved cmd_uidebug.txt (see Services/DebugCommandListService).
    // Doesn't touch any tab's saved list itself - the caller (BuildSectionViewModel.AddDebugCommand
    // / EditDebugCommand) applies whatever this returns once the dialog confirms.
    public partial class AddEditDebugCommandDialog : Window
    {
        public AddEditDebugCommandDialog()
        {
            InitializeComponent();
        }

        public string Command => CommandTextBox.Text.Trim();

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CommandTextBox.Focus();
            CommandTextBox.SelectAll();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Command))
            {
                MessageBox.Show(this, "Enter a debug command.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // Shows the dialog modally, pre-filled with whatever the caller already has (blank for
        // "Add", the entry's current text for "Edit"); returns true and the entered command iff
        // the user clicked Save.
        public static bool PromptForCommand(string title, string initialCommand, out string command)
        {
            var dialog = new AddEditDebugCommandDialog { Title = title };
            dialog.CommandTextBox.Text = initialCommand;

            bool result = dialog.ShowDialog() == true;
            command = result ? dialog.Command : string.Empty;
            return result;
        }
    }
}
