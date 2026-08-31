using System.Windows.Controls;

namespace CommandCenter.View
{
    // "+ Add Tab" no longer needs a code-behind dropdown handler - it's a plain
    // Command="{Binding AddTabCommand}" now that there's no GMS/CMS/Live category to pick.
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }
    }
}
