using System;
using System.Threading.Tasks;
using System.Windows;
using CommandCenter.Services;
using Microsoft.Web.WebView2.Core;

namespace CommandCenter.View
{
    /// <summary>
    /// Visible WebView2 for the one-time Google sign-in. Shares GoogleWebSession's environment, so the
    /// cookies saved here are what the hidden downloader uses afterward. Auto-closes once the sheet loads.
    /// </summary>
    public partial class GoogleSignInWindow : Window
    {
        private readonly string _startUrl;
        private bool _signedIn;

        public GoogleSignInWindow(string startUrl)
        {
            InitializeComponent();
            _startUrl = startUrl;
            Loaded += OnLoaded;
            Closed += (_, _) => Web.Dispose();
        }

        /// <summary>Returns true if the sheet page was reached (i.e. sign-in worked).</summary>
        public static bool Prompt(Window? owner, string startUrl)
        {
            var window = new GoogleSignInWindow(startUrl);
            if (owner != null && owner.IsVisible) window.Owner = owner;
            window.ShowDialog();
            return window._signedIn;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var environment = await GoogleWebSession.Shared.GetEnvironmentAsync();
                await Web.EnsureCoreWebView2Async(environment);
                Web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                Web.CoreWebView2.Navigate(_startUrl);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Couldn't start the embedded browser: {ex.Message} (is the WebView2 Runtime installed?)";
            }
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_signedIn || !e.IsSuccess) return;
            if (!GoogleWebSession.IsSheetPage(Web.CoreWebView2.Source)) return;

            _signedIn = true;
            StatusText.Text = "Signed in. Closing...";
            await Task.Delay(800);
            if (IsLoaded) Close();
        }

        private void Done_Click(object sender, RoutedEventArgs e)
        {
            if (!_signedIn && Web.CoreWebView2 != null)
                _signedIn = GoogleWebSession.IsSheetPage(Web.CoreWebView2.Source);
            Close();
        }
    }
}
