using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WpfImage = System.Windows.Controls.Image;

namespace CommandCenter.View
{
    /// <summary>
    /// Drop-in replacement for MessageBox.Show (same arguments, same MessageBoxResult) whose text
    /// area is capped to the monitor: short messages size to fit like a normal MessageBox, long ones
    /// (stack traces, giant Drive/WebView2 errors, long file lists) get a vertical scroll bar instead
    /// of stretching the dialog - and the app behind it - off the bottom of the screen.
    /// The text is a read-only TextBox, so it can be selected and copied (Ctrl+A, Ctrl+C).
    /// </summary>
    public static class ScrollableMessageBox
    {
        private const double MaxDialogWidth = 720;
        private const double MinDialogWidth = 360;

        public static MessageBoxResult Show(string text, string caption,
            MessageBoxButton button = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            var owner = Application.Current?.MainWindow;
            if (owner != null && !owner.IsVisible) owner = null;

            var workArea = SystemParameters.WorkArea;
            var result = button is MessageBoxButton.OK ? MessageBoxResult.OK
                       : button is MessageBoxButton.YesNo ? MessageBoxResult.No
                       : MessageBoxResult.Cancel;

            var window = new Window
            {
                Title = caption,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                ShowInTaskbar = owner == null,
                WindowStyle = WindowStyle.SingleBorderWindow,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                MinWidth = MinDialogWidth,
                MaxWidth = Math.Min(MaxDialogWidth, workArea.Width * 0.9),
                MaxHeight = workArea.Height * 0.85,
                Owner = owner
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ---- Message (icon + scrolling text) ----
            var body = new Grid { Margin = new Thickness(16, 16, 16, 12) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconSource = IconFor(icon);
            if (iconSource != null)
            {
                var image = new WpfImage
                {
                    Source = iconSource,
                    Width = 32,
                    Height = 32,
                    Margin = new Thickness(0, 0, 14, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                Grid.SetColumn(image, 0);
                body.Children.Add(image);
            }

            var textBox = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                FontSize = 13,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                // Room for the icon, buttons and title bar; anything taller scrolls.
                MaxHeight = Math.Max(120, workArea.Height * 0.85 - 140),
                VerticalAlignment = VerticalAlignment.Top,
                Focusable = true,
                IsTabStop = false
            };
            Grid.SetColumn(textBox, 1);
            body.Children.Add(textBox);
            Grid.SetRow(body, 0);
            root.Children.Add(body);

            // ---- Buttons ----
            var footer = new Border
            {
                Background = SystemColors_ControlBrush(),
                Padding = new Thickness(16, 10, 16, 10)
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            footer.Child = buttons;
            Grid.SetRow(footer, 1);
            root.Children.Add(footer);

            void AddButton(string label, MessageBoxResult value, bool isCancel)
            {
                var b = new Button
                {
                    Content = label,
                    MinWidth = 88,
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(8, 0, 0, 0),
                    IsCancel = isCancel
                };
                b.IsDefault = defaultResult == MessageBoxResult.None ? buttons.Children.Count == 0 : defaultResult == value;
                b.Click += (_, _) =>
                {
                    result = value;
                    window.Close();
                };
                buttons.Children.Add(b);
            }

            switch (button)
            {
                case MessageBoxButton.YesNo:
                    AddButton("Yes", MessageBoxResult.Yes, false);
                    AddButton("No", MessageBoxResult.No, true);
                    break;
                case MessageBoxButton.YesNoCancel:
                    AddButton("Yes", MessageBoxResult.Yes, false);
                    AddButton("No", MessageBoxResult.No, false);
                    AddButton("Cancel", MessageBoxResult.Cancel, true);
                    break;
                case MessageBoxButton.OKCancel:
                    AddButton("OK", MessageBoxResult.OK, false);
                    AddButton("Cancel", MessageBoxResult.Cancel, true);
                    break;
                default:
                    AddButton("OK", MessageBoxResult.OK, true);
                    break;
            }

            window.Content = root;
            window.Loaded += (_, _) =>
            {
                // Once it has measured itself, let the user drag it bigger/smaller but never
                // past the monitor; the TextBox keeps scrolling inside.
                window.SizeToContent = SizeToContent.Manual;
                textBox.MaxHeight = double.PositiveInfinity;
                textBox.VerticalAlignment = VerticalAlignment.Stretch;
                foreach (var child in buttons.Children)
                {
                    if (child is Button { IsDefault: true } def) { def.Focus(); break; }
                }
            };

            Beep(icon);
            window.ShowDialog();
            return result;
        }

        private static System.Windows.Media.Brush SystemColors_ControlBrush() => System.Windows.SystemColors.ControlBrush;

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        // Standard Windows message-box icons (IDI_HAND / IDI_QUESTION / IDI_EXCLAMATION / IDI_ASTERISK).
        private static BitmapSource? IconFor(MessageBoxImage icon)
        {
            int id = icon switch
            {
                MessageBoxImage.Error => 32513,       // also Hand / Stop
                MessageBoxImage.Question => 32514,
                MessageBoxImage.Warning => 32515,     // also Exclamation
                MessageBoxImage.Information => 32516, // also Asterisk
                _ => 0
            };
            if (id == 0) return null;
            try
            {
                var handle = LoadIcon(IntPtr.Zero, new IntPtr(id)); // shared system icon - never destroy it
                return handle == IntPtr.Zero
                    ? null
                    : Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch
            {
                return null;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);

        private static void Beep(MessageBoxImage icon)
        {
            uint type = icon switch
            {
                MessageBoxImage.Error => 0x10,       // MB_ICONHAND
                MessageBoxImage.Question => 0x20,    // MB_ICONQUESTION
                MessageBoxImage.Warning => 0x30,     // MB_ICONEXCLAMATION
                MessageBoxImage.Information => 0x40, // MB_ICONASTERISK
                _ => 0xFFFFFFFF
            };
            if (type != 0xFFFFFFFF)
            {
                try { MessageBeep(type); } catch { }
            }
        }
    }
}
