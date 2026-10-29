using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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

        // ---------- Local Files list: drag a row to reorder ----------
        // Same feel as Settings' tab-card reorder: the dragged row dims, and a blue bar on the
        // top or bottom edge of the row under the cursor shows exactly where it'll land
        // (upper half = above that row, lower half = below it). The bars are the DropTop /
        // DropBottom overlays in the row template, so showing one never shifts the layout.

        private Point _dragStart;
        private LocalExtractFileViewModel? _dragItem;
        private ListBoxItem? _indicatorRow;

        private void LocalFiles_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragItem = null;
            // Clicking the row's ✕ button shouldn't start a drag.
            if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) != null) return;
            var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (container?.DataContext is LocalExtractFileViewModel item)
            {
                _dragItem = item;
                _dragStart = e.GetPosition(LocalFilesList);
            }
        }

        private void LocalFiles_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(LocalFilesList) - _dragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            var item = _dragItem;
            _dragItem = null;

            var sourceRow = LocalFilesList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
            if (sourceRow != null) sourceRow.Opacity = 0.5;
            try
            {
                DragDrop.DoDragDrop(LocalFilesList, new DataObject(typeof(LocalExtractFileViewModel), item), DragDropEffects.Move);
            }
            finally
            {
                if (sourceRow != null) sourceRow.Opacity = 1.0;
                ClearDropIndicator();
            }
        }

        private void LocalFiles_DragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (DataContext is not BuildRepoViewModel vm
                || e.Data.GetData(typeof(LocalExtractFileViewModel)) is not LocalExtractFileViewModel dragged)
            {
                e.Effects = DragDropEffects.None;
                ClearDropIndicator();
                return;
            }

            e.Effects = DragDropEffects.Move;
            if (!TryGetDropSlot(vm, dragged, e, out var row, out bool after, out _))
            {
                ClearDropIndicator(); // over itself, or a spot that wouldn't change the order
                return;
            }
            ShowDropIndicator(row!, after);
        }

        private void LocalFiles_DragLeave(object sender, DragEventArgs e) => ClearDropIndicator();

        private void LocalFiles_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            ClearDropIndicator();
            if (DataContext is not BuildRepoViewModel vm
                || e.Data.GetData(typeof(LocalExtractFileViewModel)) is not LocalExtractFileViewModel dragged) return;

            if (!TryGetDropSlot(vm, dragged, e, out _, out _, out int to)) return;
            vm.MoveLocalFile(vm.LocalFiles.IndexOf(dragged), to);
            LocalFilesList.SelectedItem = dragged;
        }

        /// <summary>
        /// Works out which row edge the drop targets and the final index it lands on. Empty space
        /// below the rows counts as "below the last row". Returns false when the drop would leave
        /// the order unchanged, so no bar is shown for a no-op.
        /// </summary>
        private bool TryGetDropSlot(BuildRepoViewModel vm, LocalExtractFileViewModel dragged, DragEventArgs e,
                                    out ListBoxItem? row, out bool after, out int to)
        {
            row = null; after = false; to = -1;
            int from = vm.LocalFiles.IndexOf(dragged);
            if (from < 0 || vm.LocalFiles.Count < 2) return false;

            row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            LocalExtractFileViewModel over;
            if (row?.DataContext is LocalExtractFileViewModel hit)
            {
                over = hit;
                after = e.GetPosition(row).Y > row.ActualHeight / 2;
            }
            else
            {
                over = vm.LocalFiles[^1];
                row = LocalFilesList.ItemContainerGenerator.ContainerFromItem(over) as ListBoxItem;
                after = true;
                if (row == null) return false;
            }

            if (ReferenceEquals(over, dragged)) return false;

            // Insert slot in the list as it stands, then account for the dragged row leaving it.
            int slot = vm.LocalFiles.IndexOf(over) + (after ? 1 : 0);
            to = slot > from ? slot - 1 : slot;
            return to != from;
        }

        private void ShowDropIndicator(ListBoxItem row, bool after)
        {
            if (!ReferenceEquals(_indicatorRow, row)) ClearDropIndicator();
            _indicatorRow = row;
            SetBar(row, "DropTop", !after);
            SetBar(row, "DropBottom", after);
        }

        private void ClearDropIndicator()
        {
            if (_indicatorRow == null) return;
            SetBar(_indicatorRow, "DropTop", false);
            SetBar(_indicatorRow, "DropBottom", false);
            _indicatorRow = null;
        }

        private static void SetBar(ListBoxItem row, string name, bool visible)
        {
            if (FindDescendantByName(row, name) is UIElement bar)
                bar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private static FrameworkElement? FindDescendantByName(DependencyObject parent, string name)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement fe && fe.Name == name) return fe;
                var found = FindDescendantByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
