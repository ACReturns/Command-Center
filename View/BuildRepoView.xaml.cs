using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommandCenter.ViewModel;

namespace CommandCenter.View
{
    public partial class BuildRepoView : UserControl
    {
        // Must match the fixed row sizes in BuildRepoView.xaml (tree row MinHeight, log row Height).
        private const double TreeMinHeight = 180;
        private const double LogHeight = 120;

        public BuildRepoView()
        {
            InitializeComponent();
        }

        /// <summary>Fires each time the tab is shown: refreshes the target-tab list and, the first time, lists the repo.</summary>
        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            (DataContext as BuildRepoViewModel)?.OnActivated();

            // After the first layout pass (so the download panel's real height is known), make sure
            // everything fits - growing the window if it's too short.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                UpdateMinHeight();
                GrowWindowToFit();
            }));
        }

        // Keep the newest log line in view.
        private void Log_TextChanged(object sender, TextChangedEventArgs e)
        {
            (sender as TextBox)?.ScrollToEnd();
        }

        // The download panel's height changes with text wrapping (window width, long paths, status
        // lines) - keep the grid's minimum in sync so nothing ever gets clipped; the ScrollViewer
        // takes over when the window is smaller than that.
        private void DownloadPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.HeightChanged) UpdateMinHeight();
        }

        /// <summary>Total height every row needs: header + tree minimum + download panel + log.</summary>
        private double RequiredHeight()
        {
            return HeaderPanel.ActualHeight + HeaderPanel.Margin.Top + HeaderPanel.Margin.Bottom
                 + TreeMinHeight
                 + DownloadPanel.ActualHeight + DownloadPanel.Margin.Top + DownloadPanel.Margin.Bottom
                 + LogHeight;
        }

        private void UpdateMinHeight()
        {
            var required = RequiredHeight();
            if (required > 0 && Math.Abs(RootGrid.MinHeight - required) > 0.5)
            {
                RootGrid.MinHeight = required;
            }
        }

        /// <summary>
        /// If the visible area is shorter than what the tab needs, grow the window by the difference
        /// (never past the screen's work area; nudged up if it would run off the bottom). Leaves a
        /// maximized window alone - the ScrollViewer handles anything that still doesn't fit.
        /// </summary>
        private void GrowWindowToFit()
        {
            var window = Window.GetWindow(this);
            if (window == null || window.WindowState != WindowState.Normal) return;

            var deficit = RequiredHeight() - RootScroll.ViewportHeight;
            if (deficit <= 1) return;

            var workArea = SystemParameters.WorkArea;
            var newHeight = Math.Min(window.ActualHeight + Math.Ceiling(deficit), workArea.Height);
            if (newHeight <= window.ActualHeight) return;

            window.Height = newHeight;
            if (window.Top + newHeight > workArea.Bottom)
            {
                window.Top = Math.Max(workArea.Top, workArea.Bottom - newHeight);
            }
        }
    }
}
