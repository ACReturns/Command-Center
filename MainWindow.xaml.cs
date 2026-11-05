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

            // Silent self-update check once the window is up (needs a real window handle for the
            // hidden WebView2). Only speaks up if an update is found - see UpdateViewModel.
            ContentRendered += async (_, _) =>
            {
                if (DataContext is CommandCenter.ViewModel.MainViewModel vm)
                {
                    await vm.Settings.Updates.CheckOnStartupAsync();
                }
            };
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
