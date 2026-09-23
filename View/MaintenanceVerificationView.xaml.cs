using System.Windows;
using System.Windows.Controls;
using CommandCenter.ViewModel;

namespace CommandCenter.View
{
    public partial class MaintenanceVerificationView : UserControl
    {
        public MaintenanceVerificationView()
        {
            InitializeComponent();
        }

        /// <summary>Fires each time the tab is shown: refreshes the installed-build list and, the first time, pulls the sheet.</summary>
        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            (DataContext as MaintenanceVerificationViewModel)?.OnActivated();
        }
    }
}
