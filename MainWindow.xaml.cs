using System.Windows;
using System.Windows.Shell;

namespace CommandCenter
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Elevated (Run as Administrator) windows don't receive Explorer drag-drop through
            // WPF - this re-enables it for the Documents panels. No-op when not elevated.
            ElevatedFileDropBridge.Attach(this);
            StateChanged += (_, _) => UpdateMaximizeRestoreToolTip();
            UpdateMaximizeRestoreToolTip();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                SystemCommands.RestoreWindow(this);
            }
            else
            {
                SystemCommands.MaximizeWindow(this);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

        private void UpdateMaximizeRestoreToolTip()
        {
            MaximizeRestoreButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        }
    }
}
