using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CommandCenter.ViewModel;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CommandCenter.View
{
    // Drives the embedded WebView2 for the OpTool tab. OpToolViewModel only says "go to this URL"
    // (NavigationRequested); everything browser-specific - lazy init, back/reload, status text,
    // runtime-missing handling - lives here since it's control state, not app state.
    //
    // Security posture (the whole point of doing it this way):
    //  - No credentials anywhere in Command Center. You type them into the OpTool's real login page.
    //  - InPrivate session: cookies/cache/history are in-memory only and wiped when Command Center
    //    closes, so nobody opening the app later inherits your logged-in OpTool session.
    //  - WebView2's own password-save prompt and form autofill are switched off, so the browser
    //    itself can't quietly become a credential store either.
    public partial class OpToolView : UserControl
    {
        private const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

        private OpToolViewModel? _viewModel;

        // Created on the first Go, then reused - cached as a Task so two quick Go clicks during
        // startup share one initialization instead of racing two. A failed init stays failed for
        // this app session (installing the runtime needs a Command Center restart to pick it up).
        private Task<bool>? _initTask;

        public OpToolView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.NavigationRequested -= OnNavigationRequested;
            }

            _viewModel = e.NewValue as OpToolViewModel;

            if (_viewModel != null)
            {
                _viewModel.NavigationRequested += OnNavigationRequested;
            }
        }

        private async void OnNavigationRequested(object? sender, Uri uri)
        {
            try
            {
                ShowBrowser();

                if (!await EnsureBrowserReadyAsync())
                {
                    return;
                }

                Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
            }
            catch (Exception ex)
            {
                // async void - anything escaping here would take the whole app down.
                StatusText.Text = $"Couldn't open {uri.Host}: {ex.Message}";
            }
        }

        private Task<bool> EnsureBrowserReadyAsync() => _initTask ??= InitializeBrowserAsync();

        private async Task<bool> InitializeBrowserAsync()
        {
            StatusText.Text = "Starting browser…";

            try
            {
                // Explicit data folder under %AppData%\CommandCenter - WebView2's default is next
                // to the exe, which isn't writable if Command Center lives somewhere like
                // Program Files. InPrivate keeps session data out of it; the folder only holds
                // the browser process's own housekeeping.
                Directory.CreateDirectory(AppPaths.WebView2DataFolder);
                Browser.CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = AppPaths.WebView2DataFolder,
                    IsInPrivateModeEnabled = true
                };

                await Browser.EnsureCoreWebView2Async();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                ShowError(
                    "The WebView2 Runtime isn't installed on this PC.",
                    "The OpTool tab needs Microsoft's Evergreen WebView2 Runtime (it ships with Windows 11 and most up-to-date Windows 10 machines). Install it, then restart Command Center.",
                    showRuntimeButton: true);
                return false;
            }
            catch (Exception ex)
            {
                ShowError("Couldn't start the embedded browser.", ex.Message, showRuntimeButton: false);
                return false;
            }

            CoreWebView2 core = Browser.CoreWebView2;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;

            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.HistoryChanged += (_, _) => BackButton.IsEnabled = core.CanGoBack;

            ReloadButton.IsEnabled = true;
            StatusText.Text = string.Empty;
            return true;
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            string target = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? uri.Authority : e.Uri;
            StatusText.Text = $"Loading {target}…";
            StatusText.ToolTip = e.Uri;
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            // A navigation cut short by a newer one (Go clicked again mid-load) - the newer
            // navigation's own events own the status text, so don't flash a bogus error.
            if (!e.IsSuccess && e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            {
                return;
            }

            string url = Browser.CoreWebView2?.Source ?? string.Empty;
            StatusText.ToolTip = url;
            StatusText.Text = e.IsSuccess
                ? url
                : $"Couldn't load {url} ({e.WebErrorStatus}) - check VPN/network access to that environment.";
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Browser.CoreWebView2 is { CanGoBack: true } core)
            {
                core.GoBack();
            }
        }

        private void ReloadButton_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();

        private void GetRuntimeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(RuntimeDownloadUrl) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                ErrorDetail.Text += $"\n\nDownload it from: {RuntimeDownloadUrl}";
            }
        }

        private void ShowBrowser()
        {
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            Browser.Visibility = Visibility.Visible;
        }

        private void ShowError(string title, string detail, bool showRuntimeButton)
        {
            Browser.Visibility = Visibility.Collapsed;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            ErrorTitle.Text = title;
            ErrorDetail.Text = detail;
            GetRuntimeButton.Visibility = showRuntimeButton ? Visibility.Visible : Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            StatusText.Text = string.Empty;
        }
    }
}
