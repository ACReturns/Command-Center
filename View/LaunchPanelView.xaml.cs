using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CommandCenter.ViewModel;

namespace CommandCenter.View
{
    public partial class LaunchPanelView : UserControl
    {
        public LaunchPanelView()
        {
            InitializeComponent();
        }

        // ---------- Debug Commands list: drag a row to reorder ----------
        // Same feel as Settings' tab-card reorder and Build Repo's local file list: the dragged row
        // dims, and a blue bar on the top or bottom edge of the row under the cursor shows exactly
        // where it'll land (upper half = above that row, lower half = below it). Everything is
        // index-based because the list is plain strings that can repeat - looking rows up "by item"
        // would grab the first matching copy instead of the one actually dragged.

        // Wraps the dragged row's index so the drop target only reacts to drags from this list.
        private sealed class DebugCommandDrag
        {
            public DebugCommandDrag(int index) => Index = index;
            public int Index { get; }
        }

        private Point _dragStart;
        private int _dragIndex = -1;
        private ListBoxItem? _indicatorRow;

        private void DebugCommands_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragIndex = -1;
            if (FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) != null) return;
            var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (row == null) return;
            _dragIndex = DebugCommandsListBox.ItemContainerGenerator.IndexFromContainer(row);
            _dragStart = e.GetPosition(DebugCommandsListBox);
        }

        private void DebugCommands_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragIndex < 0 || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(DebugCommandsListBox) - _dragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            int index = _dragIndex;
            _dragIndex = -1;

            var sourceRow = DebugCommandsListBox.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            if (sourceRow != null) sourceRow.Opacity = 0.5;
            try
            {
                DragDrop.DoDragDrop(DebugCommandsListBox, new DataObject(typeof(DebugCommandDrag), new DebugCommandDrag(index)), DragDropEffects.Move);
            }
            finally
            {
                if (sourceRow != null) sourceRow.Opacity = 1.0;
                ClearDropIndicator();
            }
        }

        private void DebugCommands_DragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (DataContext is not BuildSectionViewModel vm
                || e.Data.GetData(typeof(DebugCommandDrag)) is not DebugCommandDrag drag)
            {
                e.Effects = DragDropEffects.None;
                ClearDropIndicator();
                return;
            }

            e.Effects = DragDropEffects.Move;
            AutoScrollWhileDragging(e);
            if (!TryGetDropSlot(vm, drag.Index, e, out var row, out bool after, out _))
            {
                ClearDropIndicator(); // over itself, or a spot that wouldn't change the order
                return;
            }
            ShowDropIndicator(row!, after);
        }

        private void DebugCommands_DragLeave(object sender, DragEventArgs e) => ClearDropIndicator();

        // ---------- Auto-scroll while dragging ----------
        // The list only shows a handful of rows, so holding the dragged row near its top or bottom
        // edge scrolls it - DragOver keeps firing while the cursor sits still during a drag, so this
        // keeps going until you move away from the edge. The closer to the edge, the faster it goes.
        // Throttled so it doesn't fly past everything on a fast machine.

        private const double AutoScrollEdge = 28;               // px from the top/bottom edge that triggers scrolling
        private static readonly TimeSpan AutoScrollInterval = TimeSpan.FromMilliseconds(60);
        private DateTime _lastAutoScroll = DateTime.MinValue;
        private ScrollViewer? _debugScroll;

        private void AutoScrollWhileDragging(DragEventArgs e)
        {
            _debugScroll ??= FindDescendant<ScrollViewer>(DebugCommandsListBox);
            if (_debugScroll == null || _debugScroll.ScrollableHeight <= 0) return;

            double y = e.GetPosition(DebugCommandsListBox).Y;
            double height = DebugCommandsListBox.ActualHeight;

            int direction;
            double depth;
            if (y < AutoScrollEdge)
            {
                if (_debugScroll.VerticalOffset <= 0) return;
                direction = -1;
                depth = AutoScrollEdge - y;
            }
            else if (y > height - AutoScrollEdge)
            {
                if (_debugScroll.VerticalOffset >= _debugScroll.ScrollableHeight) return;
                direction = 1;
                depth = y - (height - AutoScrollEdge);
            }
            else
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _lastAutoScroll < AutoScrollInterval) return;
            _lastAutoScroll = now;

            // 1 row near the inner edge of the zone, up to 3 right at the list's edge.
            int steps = depth > AutoScrollEdge * 0.66 ? 3 : depth > AutoScrollEdge * 0.33 ? 2 : 1;
            for (int i = 0; i < steps; i++)
            {
                if (direction < 0) _debugScroll.LineUp(); else _debugScroll.LineDown();
            }

            // Rows just moved under the cursor - lay them out now so the rest of this DragOver hit-tests
            // the rows actually there, and drop the old bar so it's redrawn on the right one.
            _debugScroll.UpdateLayout();
            ClearDropIndicator();
        }

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var found = FindDescendant<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private void DebugCommands_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            ClearDropIndicator();
            if (DataContext is not BuildSectionViewModel vm
                || e.Data.GetData(typeof(DebugCommandDrag)) is not DebugCommandDrag drag) return;

            if (!TryGetDropSlot(vm, drag.Index, e, out _, out _, out int to)) return;
            vm.MoveDebugCommand(drag.Index, to);
            DebugCommandsListBox.SelectedIndex = to;
        }

        /// <summary>
        /// Works out which row edge the drop targets and the final index it lands on. Empty space
        /// below the rows counts as "below the last row". Returns false when the drop would leave
        /// the order unchanged, so no bar is shown for a no-op.
        /// </summary>
        private bool TryGetDropSlot(BuildSectionViewModel vm, int from, DragEventArgs e,
                                    out ListBoxItem? row, out bool after, out int to)
        {
            row = null; after = false; to = -1;
            int count = vm.DebugCommands.Count;
            if (from < 0 || from >= count || count < 2) return false;

            var generator = DebugCommandsListBox.ItemContainerGenerator;
            row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            int over = row != null ? generator.IndexFromContainer(row) : -1;
            if (over >= 0)
            {
                after = e.GetPosition(row).Y > row!.ActualHeight / 2;
            }
            else
            {
                over = count - 1;
                row = generator.ContainerFromIndex(over) as ListBoxItem;
                after = true;
                if (row == null) return false;
            }

            if (over == from) return false;

            // Insert slot in the list as it stands, then account for the dragged row leaving it.
            int slot = over + (after ? 1 : 0);
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
