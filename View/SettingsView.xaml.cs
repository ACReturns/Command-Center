using System.Windows;
using System.Windows.Controls;

namespace CommandCenter.View
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }

        // "Button dropdown" pattern: the button's own ContextMenu holds the GMS/CMS/Live choices,
        // opened programmatically on click so it reads as a dropdown rather than a right-click menu.
        private void AddBuildPathButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.IsOpen = true;
            }
        }
    }
}
