using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommandCenter.Model;
using CommandCenter.Services;
using CommandCenter.View;

namespace CommandCenter.ViewModel
{
    /// <summary>
    /// Settings' "Updates" card + the update section of the bottom status bar.
    ///
    /// Two outputs, both always in-place (no pop-ups):
    ///  - StatusText: the fuller message under the Settings buttons.
    ///  - BarText: a short version shown next to the storage tracker on every tab
    ///    ("Checking for updates...", "Up to date · ADW-MC-01", ...).
    /// The ONLY pop-up is the "update available - install now?" confirmation, since that one
    /// needs a decision. The "check on startup" checkbox saves immediately (not part of the
    /// Settings draft/Save flow).
    /// </summary>
    public class UpdateViewModel : ViewModelBase
    {
        private readonly AppSettings _appSettings;
        private readonly SettingsService _settingsService;
        private readonly AppUpdateService _updates = new(ConfluenceSession.Shared);
        private string _statusText = string.Empty;
        private string _barText = string.Empty;
        private bool _isBusy;
        private double _progressPercent;
        private bool _hasProgress;
        private bool _isSignedIn;
        private CancellationTokenSource? _cts;

        public UpdateViewModel(AppSettings appSettings, SettingsService settingsService)
        {
            _appSettings = appSettings;
            _settingsService = settingsService;

            CheckForUpdatesCommand = new AsyncRelayCommand(_ => CheckAsync(interactive: true), _ => !IsBusy);
            SignInCommand = new AsyncRelayCommand(_ => SignInAsync(), _ => !IsBusy);
            ForgetSignInCommand = new AsyncRelayCommand(_ => ForgetSignInAsync(), _ => !IsBusy);
            CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        }

        public string CurrentVersionDisplay => AppVersionInfo.Current.Display;

        public string LastCheckedDisplay => _appSettings.LastUpdateCheck is { } t
            ? $"Last checked {t:g}"
            : "Not checked yet";

        public bool CheckOnStartup
        {
            get => _appSettings.CheckForUpdatesOnStartup;
            set
            {
                if (_appSettings.CheckForUpdatesOnStartup == value) return;
                _appSettings.CheckForUpdatesOnStartup = value;
                _settingsService.Save(_appSettings);
                OnPropertyChanged();
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        // Short status for the bottom bar (MainWindow.xaml) - empty hides the bar's update section.
        public string BarText
        {
            get => _barText;
            private set
            {
                if (SetProperty(ref _barText, value)) OnPropertyChanged(nameof(HasBarText));
            }
        }

        public bool HasBarText => !string.IsNullOrEmpty(_barText);

        // True once Confluence has actually answered as the signed-in user this session (a check
        // got through, or Sign in / the silent SSO refresh landed on the page). Hides the "Sign in
        // to Confluence" button; Forget Sign-in or an expired session flips it back to false so
        // the button returns. Starts false - the startup check sets it within a few seconds.
        public bool IsSignedIn
        {
            get => _isSignedIn;
            private set => SetProperty(ref _isSignedIn, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public double ProgressPercent
        {
            get => _progressPercent;
            private set => SetProperty(ref _progressPercent, value);
        }

        public bool HasProgress
        {
            get => _hasProgress;
            private set => SetProperty(ref _hasProgress, value);
        }

        public AsyncRelayCommand CheckForUpdatesCommand { get; }
        public AsyncRelayCommand SignInCommand { get; }
        public AsyncRelayCommand ForgetSignInCommand { get; }
        public RelayCommand CancelCommand { get; }

        private string BaseUrl => string.IsNullOrWhiteSpace(_appSettings.UpdateConfluenceBaseUrl)
            ? UpdateDefaults.ConfluenceBaseUrl
            : _appSettings.UpdateConfluenceBaseUrl.Trim();

        private string PageId => string.IsNullOrWhiteSpace(_appSettings.UpdatePageId)
            ? UpdateDefaults.PageId
            : _appSettings.UpdatePageId.Trim();

        /// <summary>
        /// Startup check (MainWindow.ContentRendered). Never opens the sign-in window; progress and
        /// result go to the bottom bar.
        /// </summary>
        public Task CheckOnStartupAsync() =>
            _appSettings.CheckForUpdatesOnStartup ? CheckAsync(interactive: false) : Task.CompletedTask;

        private void SetStatus(string full, string bar)
        {
            StatusText = full;
            BarText = bar;
        }

        // interactive = the user clicked "Check for Updates" in Settings, so it's OK to open the
        // sign-in window if the saved sign-in has run out. Nothing else differs.
        private async Task CheckAsync(bool interactive)
        {
            if (IsBusy) return;
            IsBusy = true;
            HasProgress = false;
            _cts = new CancellationTokenSource();

            var status = new Progress<string>(s => SetStatus(s, "Checking for updates..."));
            var percent = new Progress<double>(p =>
            {
                HasProgress = true;
                ProgressPercent = p;
                BarText = $"Checking for updates... downloading {p:0}%";
            });

            SetStatus("Checking Confluence for the latest package...", "Checking for updates...");

            try
            {
                UpdateCheckResult result;
                try
                {
                    result = await _updates.CheckAsync(BaseUrl, PageId, status, percent, _cts.Token);
                }
                catch (ConfluenceSignInRequiredException) when (interactive)
                {
                    SetStatus("Confluence sign-in needed - finish signing in in the window that opened.", "Waiting for Confluence sign-in...");
                    if (!PromptSignIn())
                    {
                        IsSignedIn = false;
                        SetStatus("Not signed in - update check skipped.", "Update check skipped (not signed in)");
                        return;
                    }
                    await ConfluenceSession.Shared.RememberSignInAsync(BaseUrl);
                    result = await _updates.CheckAsync(BaseUrl, PageId, status, percent, _cts.Token);
                }

                IsSignedIn = true;
                _appSettings.LastUpdateCheck = DateTime.Now;
                _settingsService.Save(_appSettings);
                OnPropertyChanged(nameof(LastCheckedDisplay));
                HasProgress = false;

                switch (result.Status)
                {
                    case UpdateCheckStatus.UpToDate:
                        SetStatus($"You're on the latest version ({CurrentVersionDisplay}).",
                            $"Up to date  ·  {AppVersionInfo.Current.BuildCode}");
                        break;

                    case UpdateCheckStatus.LocalIsNewer:
                        SetStatus($"This build ({CurrentVersionDisplay}) is newer than the published one ({Describe(result)}). Nothing to do.",
                            $"Up to date  ·  {AppVersionInfo.Current.BuildCode} (newer than published)");
                        break;

                    case UpdateCheckStatus.UpdateAvailable:
                        SetStatus($"Update available: {Describe(result)}", $"Update available: {Describe(result)}");
                        await OfferInstallAsync(result, status);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus("Update check cancelled.", "Update check cancelled");
            }
            catch (ConfluenceSignInRequiredException)
            {
                IsSignedIn = false;
                // Startup path (no sign-in window allowed) - point the user at Settings.
                SetStatus("Your saved Confluence sign-in has expired. Click \"Sign in to Confluence\" to check for updates.",
                    "Updates: sign in to Confluence (Settings)");
            }
            catch (Exception ex)
            {
                SetStatus($"Update check failed: {ex.Message}", "Update check failed (details in Settings)");
            }
            finally
            {
                HasProgress = false;
                IsBusy = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private async Task OfferInstallAsync(UpdateCheckResult result, IProgress<string> status)
        {
            var devWarning = AppUpdateService.IsDevBuildFolder
                ? "\n\nNote: this copy is running from a Visual Studio bin folder - the update will be copied there."
                : string.Empty;

            var answer = MessageBox.Show(Owner(),
                $"A new version of Maple Central is available.\n\n" +
                $"Current:   {CurrentVersionDisplay}\nNew:          {Describe(result)}\n" +
                $"Package:   {result.Package.FileName}\n\n" +
                $"Install now? Maple Central will close and reopen on its own.{devWarning}",
                "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                SetStatus($"Update available: {Describe(result)} (not installed - click Check for Updates to install).",
                    $"Update available: {Describe(result)} (Settings to install)");
                return;
            }

            BarText = "Installing update...";
            if (await _updates.PrepareAndLaunchInstallAsync(result, status, _cts?.Token ?? CancellationToken.None))
            {
                Application.Current.Shutdown();
            }
            else
            {
                SetStatus("Update cancelled (administrator permission was declined).", "Update cancelled");
            }
        }

        private async Task SignInAsync()
        {
            IsBusy = true;
            try
            {
                StatusText = "Checking saved Confluence sign-in...";
                if (await ConfluenceSession.Shared.RefreshSilentlyAsync(BaseUrl, PageId, CancellationToken.None))
                {
                    IsSignedIn = true;
                    StatusText = "Already signed in to Confluence (saved on this PC).";
                    return;
                }

                if (PromptSignIn())
                {
                    await ConfluenceSession.Shared.RememberSignInAsync(BaseUrl);
                    IsSignedIn = true;
                    StatusText = "Signed in to Confluence. The sign-in is saved on this PC, so update checks will log in automatically.";
                }
                else
                {
                    StatusText = "Sign-in window closed before reaching Confluence.";
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ForgetSignInAsync()
        {
            IsBusy = true;
            try
            {
                await ConfluenceSession.Shared.ForgetSignInAsync(BaseUrl);
                IsSignedIn = false;
                SetStatus("Saved Confluence sign-in removed from this PC.", string.Empty);
            }
            catch (Exception ex)
            {
                StatusText = $"Couldn't remove the saved sign-in: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool PromptSignIn() =>
            GoogleSignInWindow.Prompt(Owner(), UpdateDefaults.PageUrlFor(BaseUrl, PageId),
                url => ConfluenceSession.IsSignedInConfluencePage(url, BaseUrl),
                title: "Sign in to Confluence",
                instructions: "Sign in with your Nexon SSO account (tick \"Keep logged in\"). You only need to do this once - " +
                              "the sign-in is saved on this PC. This window closes on its own once the Maple Central page opens.");

        private static string Describe(UpdateCheckResult r)
        {
            var code = string.IsNullOrEmpty(r.RemoteBuildCode) ? null : r.RemoteBuildCode;
            var date = r.RemoteBuildDate ?? r.Package.Uploaded?.LocalDateTime;
            return (code, date) switch
            {
                ({ } c, { } d) => $"{c}  ·  {d:yyyy.MM.dd}",
                ({ } c, null) => c,
                (null, { } d) => $"uploaded {d:yyyy.MM.dd HH:mm}",
                _ => r.Package.FileName
            };
        }

        private static Window? Owner() => Application.Current?.MainWindow;
    }
}
