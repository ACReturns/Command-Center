using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommandCenter.Model;
using Microsoft.Win32;

// AppPaths (Helpers/AppPaths.cs) lives in the top-level CommandCenter namespace, not
// CommandCenter.Helpers - same reasoning DraftTabViewModel.cs already documents for its own
// "using CommandCenter;".
using CommandCenter;

namespace CommandCenter.View
{
    // Settings -> a non-permanent tab's "Change Icon" button (see DraftTabViewModel.
    // ChooseIconCommand). Presented as 4 built-in presets (the GMS/CMS/Live/app icons - see
    // TabIconCatalog.Presets; Server Status' and Settings' icons are deliberately excluded, since
    // both are one-of-a-kind tabs) plus a "Browse for image..." escape hatch for anything else,
    // gated by TabIconCatalog.RequiredCustomIconSize so
    // a custom pick can never end up blurrier in the tab strip than a built-in icon. A picked
    // custom file is copied into AppPaths.TabIconsFolder (named after the owning tab's Id)
    // immediately on selection, before OK is even clicked, so the preview always reflects a file
    // Command Center actually owns rather than wherever the user originally browsed to (which
    // could move or be deleted later - see TabSettings.CustomIconPath).
    public partial class ChooseIconDialog : Window
    {
        // Highlight for whichever preset (if any) matches the current selection - a plain, un-
        // templated Button already renders BorderBrush/BorderThickness via its default chrome, so
        // toggling these directly is enough to show a selection ring without a custom ControlTemplate.
        private static readonly Brush SelectedBorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF));
        private static readonly Brush SelectedBackgroundBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0xEE, 0xFF));

        private readonly Guid _tabId;
        private string? _selectedPath;

        public ChooseIconDialog(Guid tabId, string currentIconSource)
        {
            InitializeComponent();
            _tabId = tabId;

            // Every built-in thumbnail (and the big Preview image) is loaded through the same
            // ResolveUri/BitmapImage path - see the class comment. Populated here rather than as a
            // bare "img/…ico" string in XAML.
            MapleThumb.Source = new BitmapImage(ResolveUri("img/Maple.ico"));
            ClassicThumb.Source = new BitmapImage(ResolveUri("img/Classic.ico"));
            LiveThumb.Source = new BitmapImage(ResolveUri("img/Live.ico"));
            AppIconThumb.Source = new BitmapImage(ResolveUri("img/AppIcon.ico"));

            // Opens with whatever this tab is showing right now already selected - both so the
            // dialog can just be closed with "Use This Icon" as a no-op confirm, and so it's
            // obvious at a glance which of the 4 presets (if any) is the current one, e.g. every
            // brand-new tab defaults to "App Icon" - see TabIconCatalog.DefaultIconFor.
            Select(currentIconSource, TabIconCatalog.LabelFor(currentIconSource));
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string path })
            {
                Select(path, TabIconCatalog.LabelFor(path));
            }
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose a custom tab icon",
                Filter = "Icon or image files (*.ico;*.png;*.jpg;*.jpeg)|*.ico;*.png;*.jpg;*.jpeg"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            if (!TryValidateAndCopy(dialog.FileName, out string? copiedPath, out string? error))
            {
                MessageBox.Show(this, error, "Choose Tab Icon", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Select(copiedPath!, Path.GetFileName(dialog.FileName));
        }

        // Copies sourcePath into AppPaths.TabIconsFolder as "<tabId><ext>" (overwriting any earlier
        // custom icon this same tab had) iff it decodes as a square image at least
        // TabIconCatalog.RequiredCustomIconSize on a side. The largest embedded frame is what's
        // checked, so a multi-resolution .ico (like the built-in tab icons) only needs ONE embedded size
        // to qualify, same as a plain single-frame .png/.jpg would.
        private bool TryValidateAndCopy(string sourcePath, out string? destinationPath, out string? error)
        {
            destinationPath = null;
            error = null;

            int width;
            int height;

            try
            {
                var decoder = BitmapDecoder.Create(new Uri(sourcePath, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight).First();
                width = frame.PixelWidth;
                height = frame.PixelHeight;
            }
            catch (Exception)
            {
                error = "Couldn't read that file as an image. Choose a .ico, .png, or .jpg file.";
                return false;
            }

            if (width != height || width < TabIconCatalog.RequiredCustomIconSize)
            {
                error = $"Icon must be square and at least {TabIconCatalog.RequiredCustomIconSize}x{TabIconCatalog.RequiredCustomIconSize} pixels " +
                        $"(this file is {width}x{height}).";
                return false;
            }

            try
            {
                Directory.CreateDirectory(AppPaths.TabIconsFolder);
                string destination = Path.Combine(AppPaths.TabIconsFolder, $"{_tabId:N}{Path.GetExtension(sourcePath)}");
                File.Copy(sourcePath, destination, overwrite: true);
                destinationPath = destination;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = "Couldn't save that icon to Command Center's settings folder. Try again or pick a different file.";
                return false;
            }
        }

        private void Select(string path, string label)
        {
            _selectedPath = path;
            PreviewImage.Source = new BitmapImage(ResolveUri(path));
            SelectionText.Text = label;
            OkButton.IsEnabled = true;
            HighlightSelectedPreset(path);
        }

        // Rings whichever preset button's Tag matches path, clears the ring from every other one -
        // a custom (Browse'd) pick matches none of them, which is the correct look (nothing in the
        // built-in row claims to be what a custom upload is).
        private void HighlightSelectedPreset(string path)
        {
            foreach (var button in PresetsPanel.Children.OfType<Button>())
            {
                bool isSelected = button.Tag is string tag && string.Equals(tag, path, StringComparison.OrdinalIgnoreCase);

                if (isSelected)
                {
                    button.Background = SelectedBackgroundBrush;
                    button.BorderBrush = SelectedBorderBrush;
                }
                else
                {
                    // Back to whatever the current theme's default button chrome is, rather than a
                    // hard-coded color that might not match it.
                    button.ClearValue(Control.BackgroundProperty);
                    button.ClearValue(Control.BorderBrushProperty);
                }
            }
        }

        // Built-in presets are pack-relative resource strings ("img/…ico") the same way MainWindow.
        // xaml's own Image sources are - those only resolve automatically inside XAML's markup
        // conversion, so loading one from code needs the full pack URI spelled out. A custom pick
        // is already an absolute file path on disk, which BitmapImage accepts as-is.
        private static Uri ResolveUri(string path) =>
            path.StartsWith("img/", StringComparison.OrdinalIgnoreCase)
                ? new Uri($"pack://application:,,,/{path}")
                : new Uri(path, UriKind.Absolute);

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        // Shows the dialog modally, pre-selected to currentIconSource (see the constructor);
        // returns true and the chosen icon path (a built-in "img/…ico" resource path, or an
        // absolute path under AppPaths.TabIconsFolder for a custom upload) iff the user clicked
        // "Use This Icon" - including when they just confirmed the pre-selected current icon
        // without changing anything.
        public static bool PromptForIcon(Window? owner, Guid tabId, string currentIconSource, out string? chosenIconPath)
        {
            var dialog = new ChooseIconDialog(tabId, currentIconSource);

            if (owner != null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            bool result = dialog.ShowDialog() == true;
            chosenIconPath = result ? dialog._selectedPath : null;
            return result;
        }
    }
}
