using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CommandCenter.ViewModel;

namespace CommandCenter.View
{
    // Why this exists: MainWindow's TabControl only keeps the SELECTED tab's content in the visual
    // tree - switching tabs throws the old tab's view away and rebuilds it from its DataTemplate on
    // the way back. That's fine for every other tab, but for OpTool it would mean a brand-new
    // WebView2 on every switch: blank page, and (since the session is InPrivate) logged out again.
    //
    // So the OpToolViewModel DataTemplate renders this thin host instead of OpToolView directly.
    // Every host instance shows the SAME OpToolView (one per OpToolViewModel, i.e. one per app
    // run), moving it in on Loaded and releasing it on Unloaded. WPF's HwndHost parks the
    // WebView2's window while it's out of the tree rather than destroying it, so the page, scroll
    // position and login all survive tab switches.
    //
    // A WPF element can only have one logical parent at a time, so a new host explicitly takes the
    // view away from whatever host still holds it - Loaded/Unloaded ordering between the outgoing
    // and incoming template isn't guaranteed, so this can't rely on Unloaded having run first.
    public sealed class OpToolHost : ContentControl
    {
        private static readonly ConditionalWeakTable<OpToolViewModel, OpToolView> Views = new();

        public OpToolHost()
        {
            Focusable = false;
            IsTabStop = false;
            Loaded += (_, _) => Attach();
            Unloaded += (_, _) => Release();
            DataContextChanged += (_, _) => Attach();
        }

        private void Attach()
        {
            if (DataContext is not OpToolViewModel viewModel)
            {
                Release();
                return;
            }

            OpToolView view = Views.GetValue(viewModel, vm => new OpToolView { DataContext = vm });

            if (ReferenceEquals(Content, view))
            {
                return;
            }

            if (view.Parent is ContentControl previousHost)
            {
                previousHost.Content = null;
            }

            Content = view;
        }

        private void Release()
        {
            if (Content is OpToolView)
            {
                Content = null;
            }
        }
    }
}
