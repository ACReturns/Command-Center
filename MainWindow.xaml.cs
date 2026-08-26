using System.Windows;
using System.Windows.Media;
using CommandCenter.Helpers;

namespace CommandCenter
{
    public partial class MainWindow : Window
    {
        // Orange caption with black text so both the title text and the window icon read clearly
        // against it. Windows 11 only (see TitleBarColorHelper) - no-ops harmlessly on Windows 10.
        private static readonly Color TitleBarCaptionColor = Color.FromRgb(0xFF, 0x8C, 0x00);
        private static readonly Color TitleBarTextColor = Colors.Black;

        public MainWindow()
        {
            InitializeComponent();
            TitleBarColorHelper.TryApplyTitleBarColors(this, TitleBarCaptionColor, TitleBarTextColor);
        }
    }
}
