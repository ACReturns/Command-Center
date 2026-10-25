using System.Windows;
using System.Windows.Controls;
using CommandCenter.ViewModel;

namespace CommandCenter.View
{
    public partial class BuildRepoView : UserControl
    {
        public BuildRepoView()
        {
            InitializeComponent();
        }

        /// <summary>Fires each time the tab is shown: refreshes the target-tab list and, the first time, lists the repo.</summary>
        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            (DataContext as BuildRepoViewModel)?.OnActivated();
        }

        // Keep the newest log line in view.
        private void Log_TextChanged(object sender, TextChangedEventArgs e)
        {
            (sender as TextBox)?.ScrollToEnd();
        }
    }
}
