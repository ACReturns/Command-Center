using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CommandCenter.ViewModel;

namespace CommandCenter.View
{
    // "+ Add Build Tab" is a plain Command="{Binding AddTabCommand}" - no code-behind needed.
    // What lives here is drag-and-drop reordering of the tab cards: hold the left mouse button on
    // a card and drag it onto another card in the SAME section (Application Tabs or Build Tabs).
    // A blue bar on the target's top or bottom edge shows where it'll land; dropping calls
    // SettingsViewModel.MoveTabTo, which marks Settings dirty like any other reorder.
    public partial class SettingsView : UserControl
    {
        private const string DragFormat = "CommandCenter.DraftTab";
        private static readonly Brush DropIndicatorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)));
        private const double AutoScrollEdge = 40;
        private const double AutoScrollStep = 12;

        private Point _dragStart;
        private DraftTabViewModel? _pendingDrag;
        private Border? _pendingCard;

        public SettingsView()
        {
            InitializeComponent();
        }

        private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

        private void TabCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pendingDrag = null;
            _pendingCard = null;

            if (sender is not Border card || card.DataContext is not DraftTabViewModel draft)
            {
                return;
            }

            // Leave normal clicks on controls alone - only empty card space (or the grip) can
            // start a drag, so typing in a title or clicking Delete never picks the card up.
            if (IsInteractive(e.OriginalSource as DependencyObject, card))
            {
                return;
            }

            _dragStart = e.GetPosition(this);
            _pendingDrag = draft;
            _pendingCard = card;
        }

        private void TabCard_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _pendingDrag = null;
            _pendingCard = null;
        }

        private void TabCard_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_pendingDrag == null || _pendingCard == null || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Vector moved = e.GetPosition(this) - _dragStart;
            if (System.Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
                System.Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            var draft = _pendingDrag;
            var card = _pendingCard;
            _pendingDrag = null;
            _pendingCard = null;

            card.Opacity = 0.5;
            try
            {
                DragDrop.DoDragDrop(card, new DataObject(DragFormat, draft), DragDropEffects.Move);
            }
            finally
            {
                card.Opacity = 1.0;
            }
        }

        private void TabCard_DragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;

            if (sender is not Border card || !TryGetDrop(e, card, out _, out _))
            {
                e.Effects = DragDropEffects.None;
                ClearIndicator(sender as Border);
                return;
            }

            e.Effects = DragDropEffects.Move;
            bool after = e.GetPosition(card).Y > card.ActualHeight / 2;
            card.BorderBrush = DropIndicatorBrush;
            card.BorderThickness = after ? new Thickness(1, 1, 1, 4) : new Thickness(1, 4, 1, 1);
        }

        private void TabCard_DragLeave(object sender, DragEventArgs e) => ClearIndicator(sender as Border);

        private void TabCard_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;

            if (sender is not Border card)
            {
                return;
            }

            ClearIndicator(card);

            if (TryGetDrop(e, card, out var dragged, out var target))
            {
                bool after = e.GetPosition(card).Y > card.ActualHeight / 2;
                ViewModel!.MoveTabTo(dragged!, target!, after);
            }
        }

        // Build tab cards can be tall (expanded server/executable lists), so scroll the page when
        // dragging near its top or bottom edge.
        private void RootScroll_PreviewDragOver(object sender, DragEventArgs e)
        {
            double y = e.GetPosition(RootScroll).Y;

            if (y < AutoScrollEdge)
            {
                RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset - AutoScrollStep);
            }
            else if (y > RootScroll.ActualHeight - AutoScrollEdge)
            {
                RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset + AutoScrollStep);
            }
        }

        private bool TryGetDrop(DragEventArgs e, Border card, out DraftTabViewModel? dragged, out DraftTabViewModel? target)
        {
            dragged = e.Data.GetDataPresent(DragFormat) ? e.Data.GetData(DragFormat) as DraftTabViewModel : null;
            target = card.DataContext as DraftTabViewModel;
            return dragged != null && target != null && ViewModel != null && ViewModel.CanReorder(dragged, target);
        }

        // Falls back to the SectionCard style's own border.
        private static void ClearIndicator(Border? card)
        {
            card?.ClearValue(Border.BorderBrushProperty);
            card?.ClearValue(Border.BorderThicknessProperty);
        }

        private static bool IsInteractive(DependencyObject? source, Border card)
        {
            for (var node = source; node != null && !ReferenceEquals(node, card); node = GetParent(node))
            {
                if (node is ButtonBase or TextBoxBase or ComboBox or ScrollBar or Thumb or Slider)
                {
                    return true;
                }
            }

            return false;
        }

        // Runs inside TextBlocks are ContentElements, not Visuals - VisualTreeHelper can't walk them.
        private static DependencyObject? GetParent(DependencyObject node) =>
            node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);

        private static Brush Freeze(Brush b)
        {
            b.Freeze();
            return b;
        }
    }
}
