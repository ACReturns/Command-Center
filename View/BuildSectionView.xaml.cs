using System.Windows.Controls;

namespace CommandCenter.View
{
    public partial class BuildSectionView : UserControl
    {
        public BuildSectionView()
        {
            InitializeComponent();
        }

        // The Documents panel and Launch panel (and their event handlers) now live in their own
        // shared UserControls - DocumentsPanelView / LaunchPanelView - so the Launcher tab can reuse them.
    }
}
