using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace CommandCenter
{
    // Turns a TabInfo.IconSource string ("img/Maple.ico", or an absolute path to a custom icon
    // under AppData) into an ImageSource that works from ANY view. A bare relative "img/…" string
    // only resolves correctly from MainWindow.xaml (which sits next to the img folder); from a
    // view under View/ it would resolve against "View/img/…" and silently show nothing - the same
    // reason ChooseIconDialog builds explicit pack:// URIs.
    public class TabIconImageConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string source || string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            try
            {
                Uri uri = Path.IsPathRooted(source)
                    ? new Uri(source, UriKind.Absolute)
                    : new Uri("pack://application:,,,/" + source.TrimStart('/'), UriKind.Absolute);

                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = uri;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception)
            {
                // A missing/unreadable custom icon just shows no image rather than breaking the view.
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
