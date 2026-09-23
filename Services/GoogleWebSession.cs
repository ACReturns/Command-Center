using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using CommandCenter.Model;
using Microsoft.Web.WebView2.Core;

namespace CommandCenter.Services
{
    /// <summary>Thrown when Google serves a sign-in (or no-access) page instead of the file/folder.</summary>
    public sealed class SignInRequiredException : Exception
    {
        public SignInRequiredException()
            : base("Google sign-in required (or this account doesn't have access).") { }
    }

    // ModifiedHint is whatever date-like text (e.g. "Sep 20, 2026", "3 days ago") Drive's authenticated
    // UI happened to render for this row - best-effort only (see ListFolderScriptAuthenticated below);
    // null for anything from the embeddedfolderview fast path, which doesn't show dates at all.
    public sealed record DriveFolderEntry(string Id, string Name, string? ModifiedHint = null);

    /// <summary>
    /// Talks to Google through an embedded Edge (WebView2) profile stored under
    /// %LocalAppData%\CommandCenter\WebView2. The user signs in once via GoogleSignInWindow
    /// (normal work SSO); the cookies persist in that profile, and everything after that runs
    /// through a hidden WebView2 that shares the same profile.
    ///
    /// - Sheet: navigates to the CSV export URL; the response is a download, captured to disk.
    /// - Drive folder: loads the lightweight embeddedfolderview page and reads the file list from the DOM.
    /// - Drive file: navigates to the uc?export=download URL; captured like the sheet.
    ///
    /// Caveat: embeddedfolderview/uc are Google web endpoints, not a documented API. If Google changes
    /// them, ListFolderAsync is the piece most likely to need a tweak.
    ///
    /// All members must be called on the UI thread (WebView2 requirement).
    /// </summary>
    public sealed class GoogleWebSession : IDisposable
    {
        public static GoogleWebSession Shared { get; } = new();

        private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan NoDownloadGrace = TimeSpan.FromSeconds(2);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private Task<CoreWebView2Environment>? _environmentTask;
        private CoreWebView2Controller? _hiddenController;

        // Fast path - only actually returns rows for a folder shared "Anyone with the link can view".
        // For a folder shared with specific accounts (Don's case), this renders an empty/restricted
        // page with no redirect and no error - just zero rows - which is why ListFolderAsync always
        // falls through to ListFolderAuthenticatedAsync below whenever this comes back empty rather
        // than treating "0 files" as the final answer.
        private const string ListFolderScriptEmbedded = @"
(() => JSON.stringify(Array.from(document.querySelectorAll('.flip-entry')).map(e => {
    const a = e.querySelector('a');
    const href = a ? a.href : '';
    const m = href.match(/\/d\/([A-Za-z0-9_-]+)/) || href.match(/[?&]id=([A-Za-z0-9_-]+)/);
    const title = e.querySelector('.flip-entry-title');
    return {
        id: m ? m[1] : (e.id || '').replace(/^entry-/, ''),
        name: title ? title.textContent.trim() : ''
    };
})))()";

        // Fallback path - the real, authenticated Drive web app, which does honor the signed-in
        // account's actual folder permissions. Drive's list rows are div[role=row] with a data-id
        // attribute (the file id). Name extraction does NOT trust the row's own aria-label/innerText
        // (in practice this often comes back blank or as unrelated button text - "More actions", a
        // share-icon label, etc. - rather than the file name, which is what caused the tab to list a
        // folder's files but still not recognize "checksums.md5" among them). Instead it walks every
        // descendant of the row and picks the first bit of text that actually looks like a file name
        // (ends in ".something"), since Drive renders the visible name as real text SOMEWHERE in the
        // row even when the accessible name isn't reliably wired up - only falling back to aria-label
        // if nothing in the row looks like a filename at all.
        // Also picks out a date-looking bit of text per row (Drive's "Last modified" column, when the
        // view shows one) - same "search every descendant's text" approach as the name, since there's
        // no reliable class/attribute to target directly. Used by MaintenanceVerificationViewModel.
        // FindManifestEntryAsync to pick the most recently modified subfolder when a Checksum folder
        // holds nothing but subfolders (e.g. "Part 1"/"Part 2" batches) instead of the .md5 itself.
        private const string ListFolderScriptAuthenticated = @"
(() => JSON.stringify(Array.from(document.querySelectorAll('div[role=""row""][data-id]')).map(e => {
    const texts = Array.from(e.querySelectorAll('*'))
        .map(n => (n.textContent || '').trim())
        .filter(t => t.length > 0 && t.length < 200);
    const nameGuess = texts.find(t => /\.[A-Za-z0-9]{2,5}$/.test(t));
    const dateGuess = texts.find(t => /(\d{1,2}\/\d{1,2}\/\d{2,4})|((Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-zA-Z]*\.?\s+\d{1,2},?\s+\d{4})|(\d+\s+(second|minute|hour|day|week|month|year)s?\s+ago)/i.test(t));
    return {
        id: e.getAttribute('data-id') || '',
        name: nameGuess || (e.getAttribute('aria-label') || e.innerText || '').trim(),
        modifiedHint: dateGuess || null
    };
}).filter(x => x.id)))()";

        private GoogleWebSession() { }

        /// <summary>Shared by the hidden controller and the visible sign-in window so they use the same cookies.</summary>
        public Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            if (_environmentTask == null)
            {
                Directory.CreateDirectory(MaintenanceDefaults.WebViewDataFolder);
                _environmentTask = CoreWebView2Environment.CreateAsync(null, MaintenanceDefaults.WebViewDataFolder);
            }
            return _environmentTask;
        }

        public static bool IsSheetPage(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("/spreadsheets/d/", StringComparison.OrdinalIgnoreCase);

        public static string DriveFileDownloadUrl(string fileId) =>
            $"https://drive.google.com/uc?export=download&id={Uri.EscapeDataString(fileId)}";

        /// <summary>
        /// Navigates the hidden browser to <paramref name="url"/> and saves the resulting download to
        /// <paramref name="destinationPath"/>. Writes to a .part file first so a failed download never
        /// leaves a half-file that looks cached.
        /// </summary>
        public async Task DownloadAsync(string url, string destinationPath, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            CoreWebView2? web = null;
            CoreWebView2DownloadOperation? operation = null;
            var partPath = destinationPath + ".part";
            var downloadStarted = false;
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
            {
                downloadStarted = true;
                e.ResultFilePath = partPath;
                e.Handled = true; // suppress Edge's download UI
                operation = e.DownloadOperation;
                operation.StateChanged += (_, _) =>
                {
                    if (operation.State == CoreWebView2DownloadState.Completed)
                        done.TrySetResult(true);
                    else if (operation.State == CoreWebView2DownloadState.Interrupted)
                        done.TrySetException(new IOException($"Download interrupted ({operation.InterruptReason})."));
                };
            }

            async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (downloadStarted) return;

                // A download normally starts before/around NavigationCompleted. Give it a moment.
                await Task.Delay(NoDownloadGrace);
                if (downloadStarted) return;

                if (e.IsSuccess)
                    done.TrySetException(new SignInRequiredException()); // a page rendered instead of a file => login/no access
                else
                    done.TrySetException(new IOException($"Couldn't reach Google ({e.WebErrorStatus})."));
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                if (File.Exists(partPath)) File.Delete(partPath);

                web = await GetHiddenWebViewAsync();
                web.DownloadStarting += OnDownloadStarting;
                web.NavigationCompleted += OnNavigationCompleted;
                web.Navigate(url);

                var finished = await Task.WhenAny(done.Task, Task.Delay(OperationTimeout, ct));
                if (finished != done.Task)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException("Google didn't respond within 60 seconds.");
                }

                await done.Task; // rethrows SignInRequired / IOException
                File.Move(partPath, destinationPath, overwrite: true);
            }
            finally
            {
                if (web != null)
                {
                    web.DownloadStarting -= OnDownloadStarting;
                    web.NavigationCompleted -= OnNavigationCompleted;
                }

                if (operation != null && operation.State == CoreWebView2DownloadState.InProgress)
                {
                    try { operation.Cancel(); } catch { /* already finishing */ }
                }

                try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
                _gate.Release();
            }
        }

        /// <summary>
        /// Lists files in a Drive folder (the Checksum column links to folders, not files). Tries the
        /// fast embeddedfolderview path first (works only for "anyone with the link" folders), then
        /// falls back to the real, authenticated Drive UI (works for folders shared with specific
        /// accounts, which is the normal case for an internal checksums folder).
        /// </summary>
        public async Task<List<DriveFolderEntry>> ListFolderAsync(string folderId, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var web = await GetHiddenWebViewAsync();

                var viaEmbedded = await ListFolderEmbeddedAsync(web, folderId, ct);
                if (viaEmbedded.Count > 0)
                {
                    return viaEmbedded;
                }

                return await ListFolderAuthenticatedAsync(web, folderId, ct);
            }
            finally
            {
                _gate.Release();
            }
        }

        // Doesn't throw SignInRequiredException on a redirect or empty result - a private folder can
        // look identical to "not signed in" here (no redirect, just an empty/restricted page), so the
        // authenticated fallback above is what actually decides that.
        private async Task<List<DriveFolderEntry>> ListFolderEmbeddedAsync(CoreWebView2 web, string folderId, CancellationToken ct)
        {
            var navigated = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => navigated.TrySetResult(e);

            web.NavigationCompleted += OnNavigationCompleted;
            try
            {
                web.Navigate($"https://drive.google.com/embeddedfolderview?id={Uri.EscapeDataString(folderId)}");

                var finished = await Task.WhenAny(navigated.Task, Task.Delay(OperationTimeout, ct));
                if (finished != navigated.Task)
                {
                    ct.ThrowIfCancellationRequested();
                    return new List<DriveFolderEntry>(); // let the authenticated path have a real try
                }

                var result = await navigated.Task;
                if (!result.IsSuccess)
                {
                    return new List<DriveFolderEntry>();
                }

                if (!Uri.TryCreate(web.Source, UriKind.Absolute, out var landed)
                    || !landed.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase))
                {
                    return new List<DriveFolderEntry>();
                }

                var json = await web.ExecuteScriptAsync(ListFolderScriptEmbedded);
                return ParseFolderEntries(json);
            }
            finally
            {
                web.NavigationCompleted -= OnNavigationCompleted;
            }
        }

        // The real Drive web app - renders its own file list asynchronously well after
        // NavigationCompleted fires (that event just means the app shell's own HTML/JS loaded), so
        // this polls briefly rather than trusting the first script run to see any rows yet.
        private async Task<List<DriveFolderEntry>> ListFolderAuthenticatedAsync(CoreWebView2 web, string folderId, CancellationToken ct)
        {
            var navigated = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => navigated.TrySetResult(e);

            web.NavigationCompleted += OnNavigationCompleted;
            try
            {
                web.Navigate($"https://drive.google.com/drive/folders/{Uri.EscapeDataString(folderId)}");

                var finished = await Task.WhenAny(navigated.Task, Task.Delay(OperationTimeout, ct));
                if (finished != navigated.Task)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException("Google Drive didn't respond within 60 seconds.");
                }

                var result = await navigated.Task;
                if (!result.IsSuccess)
                {
                    throw new IOException($"Couldn't open the Drive folder ({result.WebErrorStatus}).");
                }

                // Redirected to a login/IdP page => genuinely not signed in (the real Drive UI, unlike
                // embeddedfolderview, does redirect an unauthenticated request).
                if (!Uri.TryCreate(web.Source, UriKind.Absolute, out var landed)
                    || !landed.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SignInRequiredException();
                }

                for (var attempt = 0; attempt < 10; attempt++)
                {
                    ct.ThrowIfCancellationRequested();

                    var json = await web.ExecuteScriptAsync(ListFolderScriptAuthenticated);
                    var entries = ParseFolderEntries(json);
                    if (entries.Count > 0)
                    {
                        return entries;
                    }

                    await Task.Delay(400, ct);
                }

                return new List<DriveFolderEntry>();
            }
            finally
            {
                web.NavigationCompleted -= OnNavigationCompleted;
            }
        }

        private static List<DriveFolderEntry> ParseFolderEntries(string json)
        {
            var inner = JsonSerializer.Deserialize<string>(json) ?? "[]"; // ExecuteScriptAsync JSON-encodes the returned string
            return JsonSerializer.Deserialize<List<DriveFolderEntry>>(inner,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new List<DriveFolderEntry>();
        }

        /// <summary>
        /// Cheap "does the saved WebView2 profile still have a working Google sign-in" check - used by
        /// the Sign In button to skip the visible sign-in window entirely when the cookies are already
        /// good, and safe to call speculatively (nothing here modifies anything). A timed-out or failed
        /// navigation is treated as "not signed in" so the caller falls back to the normal sign-in flow
        /// rather than getting stuck on an inconclusive check.
        /// </summary>
        public async Task<bool> IsSignedInAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            CoreWebView2? web = null;
            var navigated = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => navigated.TrySetResult(e);

            try
            {
                web = await GetHiddenWebViewAsync();
                web.NavigationCompleted += OnNavigationCompleted;
                web.Navigate("https://drive.google.com/drive/my-drive");

                var finished = await Task.WhenAny(navigated.Task, Task.Delay(OperationTimeout, ct));
                if (finished != navigated.Task)
                {
                    return false;
                }

                var result = await navigated.Task;
                return result.IsSuccess
                       && Uri.TryCreate(web.Source, UriKind.Absolute, out var landed)
                       && landed.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (web != null)
                {
                    web.NavigationCompleted -= OnNavigationCompleted;
                }

                _gate.Release();
            }
        }

        private async Task<CoreWebView2> GetHiddenWebViewAsync()
        {
            if (_hiddenController != null) return _hiddenController.CoreWebView2;

            var environment = await GetEnvironmentAsync();
            var owner = Application.Current?.MainWindow
                        ?? throw new InvalidOperationException("Main window isn't available yet.");
            var hwnd = new WindowInteropHelper(owner).EnsureHandle();

            _hiddenController = await environment.CreateCoreWebView2ControllerAsync(hwnd);
            _hiddenController.IsVisible = false;
            _hiddenController.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            return _hiddenController.CoreWebView2;
        }

        public void Dispose()
        {
            _hiddenController?.Close();
            _hiddenController = null;
        }
    }
}
