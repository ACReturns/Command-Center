using System.Windows;

namespace CommandCenter.View
{
    // Replaces the old 3-way (Save/Discard/Cancel) MessageBox prompt shown when leaving the
    // Settings tab with unsaved changes. Only 2 options now: Save Settings, or Cancel (stay put).
    // There's no way to silently discard from here anymore - the only way to lose an edit is to
    // never have saved it and to keep working, same as any other form.
    public partial class UnsavedChangesDialog : Window
    {
        public UnsavedChangesDialog()
        {
            InitializeComponent();
        }

        // True only if the user clicked "Save Settings". False for Cancel, closing via the [X]
        // button, or Escape (IsCancel="True" on the Cancel button covers Escape too).
        public bool SaveRequested { get; private set; }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            SaveRequested = true;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            SaveRequested = false;
            DialogResult = false;
        }

        // Shows the dialog modally and returns true iff the user chose "Save Settings".
        public static bool PromptSave(Window? owner)
        {
            var dialog = new UnsavedChangesDialog();

            if (owner != null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            dialog.ShowDialog();
            return dialog.SaveRequested;
        }
    }
}
