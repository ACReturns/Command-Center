using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// One child of a Drive folder with its type known - used by the Build Repo tab, which has to
    /// tell folders (versions/builds/subfolders) apart from downloadable files. IsFolder comes from
    /// Drive's own mimeType when it was available (the folder page's embedded _DRIVE_ivd data), and
    /// otherwise from a "name has no file extension" guess (see ListFolderItemsAsync).
    /// </summary>
    public sealed record DriveItem(string Id, string Name, bool IsFolder, bool TypeIsGuessed, string? MimeType = null);

    /// <summary>Bytes received so far + total (null when Drive didn't send a length).</summary>
    public readonly record struct DriveDownloadProgress(long BytesReceived, long? TotalBytes);

    /// <summary>
    /// Thrown by DownloadLargeFileAsync / ResumableDriveDownload.ResumeAsync when a transfer drops
    /// mid-file (NetworkFailed, NetworkTimeout, Wi-Fi/VPN hiccup...). When <see cref="Resumable"/> is
    /// non-null the partial bytes are still on disk and the hidden browser that owns them is still
    /// open - call ResumeAsync on it to continue from where it stopped, or DiscardAsync to throw the
    /// partial away. Null means it can't be resumed and the partial file has already been deleted.
    /// </summary>
    public sealed class DriveDownloadInterruptedException : IOException
    {
        /// <summary>
        /// Reason used when Resume was asked for but the partial bytes are no longer on disk (deleted,
        /// cleaned up, folder moved...). Nothing to continue from - the caller should restart that file.
        /// </summary>
        public const string PartialFileMissingReason = "PartialFileMissing";

        public DriveDownloadInterruptedException(string reason, ResumableDriveDownload? resumable)
            : base($"Download interrupted - {GoogleWebSession.DescribeInterruptReason(reason)}")
        {
            Reason = reason;
            Resumable = resumable;
        }

        public string Reason { get; }
        public ResumableDriveDownload? Resumable { get; }
    }

    /// <summary>
    /// One in-flight (or paused-after-interruption) Drive file download. Owns its short-lived hidden
    /// WebView2 controller, because WebView2 can only resume a download through the same
    /// CoreWebView2DownloadOperation that started it - closing the controller kills the resume.
    /// Must be used on the UI thread like everything else WebView2.
    /// </summary>
    public sealed class ResumableDriveDownload : IDisposable
    {
        private readonly CoreWebView2Controller _controller;
        private readonly CoreWebView2DownloadOperation _operation;
        private readonly string _partPath;
        private readonly string _destinationPath;
        private IProgress<DriveDownloadProgress>? _progress;
        private TaskCompletionSource<bool> _done = NewDone();
        private CoreWebView2? _web;
        private bool _closed;

        internal ResumableDriveDownload(CoreWebView2Controller controller, CoreWebView2DownloadOperation operation,
            string partPath, string destinationPath, IProgress<DriveDownloadProgress>? progress)
        {
            _controller = controller;
            _operation = operation;
            _partPath = partPath;
            _destinationPath = destinationPath;
            _progress = progress;
            _operation.BytesReceivedChanged += OnBytesReceivedChanged;
            _operation.StateChanged += OnStateChanged;
        }

        /// <summary>
        /// Takes over the hidden browser's DownloadStarting event once DownloadLargeFileAsync hands the
        /// paused download to the caller. On Resume, Chromium re-runs its "where should this file go?"
        /// step - with nobody answering it, WebView2 fell back to the Save As dialog, and cancelling
        /// that dialog cancelled the whole download. Answering it here with the original .part path
        /// keeps resume fully automatic.
        /// </summary>
        internal void TakeOverDownloadStarting()
        {
            if (_closed || _web != null) return;
            try
            {
                _web = _controller.CoreWebView2;
                _web.DownloadStarting += OnResumeDownloadStarting;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                _web = null;
            }
        }

        private void OnResumeDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            // Same file, same place - never ask the user.
            e.ResultFilePath = _partPath;
            e.Handled = true;
        }

        /// <summary>
        /// True when some piece of the partial download is still on disk: the .part file itself, or
        /// Chromium's intermediate .crdownload ("&lt;name&gt;.part.crdownload" or "Unconfirmed NNN.crdownload").
        /// </summary>
        public bool PartialFileExists()
        {
            try
            {
                if (File.Exists(_partPath) || File.Exists(_partPath + ".crdownload")) return true;
                var dir = Path.GetDirectoryName(_partPath);
                return dir != null
                       && Directory.Exists(dir)
                       && Directory.EnumerateFiles(dir, "Unconfirmed *.crdownload").Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public string FileName => Path.GetFileName(_destinationPath);
        public long BytesReceived => _closed ? 0 : _operation.BytesReceived;

        public long? TotalBytes
        {
            get
            {
                if (_closed) return null;
                var total = (long?)_operation.TotalBytesToReceive;
                return total is > 0 ? total : null;
            }
        }

        private static TaskCompletionSource<bool> NewDone() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void OnBytesReceivedChanged(object? sender, object e) =>
            _progress?.Report(new DriveDownloadProgress(_operation.BytesReceived, TotalBytes));

        private void OnStateChanged(object? sender, object e)
        {
            if (_operation.State == CoreWebView2DownloadState.Completed) _done.TrySetResult(true);
            else if (_operation.State == CoreWebView2DownloadState.Interrupted) _done.TrySetResult(false);
        }

        /// <summary>Waits for the current attempt to finish; moves .part into place on success.</summary>
        internal async Task RunAsync(CancellationToken ct)
        {
            var done = _done;
            bool completed;
            using (ct.Register(() => done.TrySetCanceled(ct)))
            {
                completed = await done.Task; // OperationCanceledException on cancel - caller discards
            }

            if (completed)
            {
                Close(deletePartial: false);
                File.Move(_partPath, _destinationPath, overwrite: true);
                return;
            }

            var reason = _operation.InterruptReason.ToString();
            if (_operation.CanResume)
            {
                throw new DriveDownloadInterruptedException(reason, this);
            }

            await DiscardAsync();
            throw new DriveDownloadInterruptedException(reason, null);
        }

        /// <summary>
        /// Continues an interrupted download from the bytes already on disk (an HTTP Range request
        /// under the hood). Throws DriveDownloadInterruptedException again if it drops again.
        /// </summary>
        public Task ResumeAsync(IProgress<DriveDownloadProgress>? progress, CancellationToken ct)
        {
            if (_closed) throw new ObjectDisposedException(nameof(ResumableDriveDownload));
            if (progress != null) _progress = progress;

            // Verify the bytes we'd be resuming from are still there before touching the browser. If
            // they're gone there's nothing to continue - report it so the caller restarts the file
            // instead of Chromium silently starting over (or prompting for a location).
            if (!PartialFileExists())
            {
                // Just cancel + close; no delete sweep, since the caller restarts this file in the same
                // folder right away and the sweep would race the fresh download's .crdownload.
                Close(deletePartial: true);
                throw new DriveDownloadInterruptedException(DriveDownloadInterruptedException.PartialFileMissingReason, null);
            }

            // Make sure the folder it was saving into still exists, and that Chromium's "where does this
            // go?" step on resume is answered with the original path instead of a Save As dialog.
            try { Directory.CreateDirectory(Path.GetDirectoryName(_partPath)!); } catch { }
            TakeOverDownloadStarting();

            _done = NewDone();

            try
            {
                _operation.Resume();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                var reason = _operation.InterruptReason.ToString();
                _ = DiscardAsync();
                throw new DriveDownloadInterruptedException(reason, null);
            }

            return RunAsync(ct);
        }

        /// <summary>Cancels the download, closes its browser and deletes every partial file it left.</summary>
        public async Task DiscardAsync()
        {
            if (_closed) return;
            Close(deletePartial: true);

            // Chromium releases the intermediate file a beat after Cancel/Close - retry briefly.
            for (int i = 0; i < 10 && DeletePartials(); i++)
            {
                await Task.Delay(200);
            }
        }

        public void Dispose()
        {
            if (_closed) return;
            Close(deletePartial: true);
            DeletePartials();
        }

        private void Close(bool deletePartial)
        {
            if (_closed) return;
            _closed = true;
            try
            {
                _operation.BytesReceivedChanged -= OnBytesReceivedChanged;
                _operation.StateChanged -= OnStateChanged;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }

            if (_web != null)
            {
                try { _web.DownloadStarting -= OnResumeDownloadStarting; }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
                _web = null;
            }

            if (deletePartial)
            {
                try
                {
                    if (_operation.State != CoreWebView2DownloadState.Completed) _operation.Cancel();
                }
                catch { /* already gone */ }
            }

            try { _controller.Close(); } catch { }
        }

        // Returns true while something is still left to delete (locked), false once it's clean.
        private bool DeletePartials()
        {
            var leftovers = false;
            var candidates = new List<string> { _partPath, _partPath + ".crdownload" };

            var dir = Path.GetDirectoryName(_partPath);
            if (dir != null && Directory.Exists(dir))
            {
                try { candidates.AddRange(Directory.EnumerateFiles(dir, "Unconfirmed *.crdownload")); } catch { }
            }

            foreach (var path in candidates)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    leftovers = true;
                }
            }
            return leftovers;
        }
    }

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
(() => JSON.stringify(Array.from(document.querySelectorAll('[role=""row""][data-id]')).map(e => {
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

        // Build Repo listing. Primary source: window._DRIVE_ivd - the folder page's own embedded
        // initial-listing data (the same thing gdown parses), a JSON array whose first element is the
        // list of children, each [id, [parents], name, mimeType, ...]. It carries the real mimeType, so
        // folder-vs-file is exact. Secondary source: the rendered div[role=row][data-id] rows, merged
        // in for anything _DRIVE_ivd didn't include - those only get a guessed type (no extension =
        // folder). Name for DOM-only rows: first leaf text in the row that isn't a date/size/owner.
        private const string ListFolderItemsScript = @"
(() => {
    const out = new Map();
    let ivdFound = false;
    try {
        const raw = window['_DRIVE_ivd'];
        if (typeof raw === 'string' && raw.length > 0) {
            const arr = JSON.parse(raw);
            const items = (Array.isArray(arr) && Array.isArray(arr[0])) ? arr[0] : [];
            ivdFound = true;
            for (const f of items) {
                if (!Array.isArray(f) || typeof f[0] !== 'string') continue;
                out.set(f[0], {
                    id: f[0],
                    name: typeof f[2] === 'string' ? f[2] : '',
                    mime: typeof f[3] === 'string' ? f[3] : ''
                });
            }
        }
    } catch (e) { }
    const junk = /^(me|owner|—|-|\d+(\.\d+)?\s*(bytes|KB|MB|GB|TB))$/i;
    const dateLike = /(\d{1,2}\/\d{1,2}\/\d{2,4})|((Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-zA-Z]*\.?\s+\d{1,2}(,?\s+\d{4})?)|(\d{1,2}:\d{2}\s*(AM|PM)?)|(\d+\s+(second|minute|hour|day|week|month|year)s?\s+ago)/i;
    for (const row of document.querySelectorAll('[role=""row""][data-id]')) {
        const id = row.getAttribute('data-id');
        if (!id || out.has(id)) continue;
        const leaves = Array.from(row.querySelectorAll('*'))
            .filter(n => n.children.length === 0)
            .map(n => (n.textContent || '').trim())
            .filter(t => t.length > 0 && t.length < 200 && !junk.test(t) && !dateLike.test(t));
        const withExt = leaves.find(t => /\.[A-Za-z0-9]{2,5}$/.test(t));
        out.set(id, { id: id, name: withExt || leaves[0] || (row.getAttribute('aria-label') || '').trim(), mime: '' });
    }
    return JSON.stringify({ ivd: ivdFound, items: Array.from(out.values()) });
})()";

        // Drive only puts the FIRST chunk of a folder's children into _DRIVE_ivd / the initial DOM -
        // the rest lazy-load as the list is scrolled. Which chunk you get depends on the signed-in
        // account's own Drive sort order (per-user), which is why some users only saw up to v250 while
        // others saw everything. This scrolls the list's scroll container (found by walking up from
        // the first row) to the bottom so Drive loads the next chunk. Returns whether there's any
        // scrollable list at all, and whether it was already at the bottom before this scroll.
        private const string ScrollFolderListScript = @"
(() => {
    const row = document.querySelector('[role=""row""][data-id]');
    let el = row ? row.parentElement : null;
    while (el && el !== document.body) {
        const s = getComputedStyle(el);
        if ((s.overflowY === 'auto' || s.overflowY === 'scroll') && el.scrollHeight > el.clientHeight + 1) break;
        el = el.parentElement;
    }
    if (!el || el === document.body) {
        el = document.scrollingElement || document.documentElement;
    }
    const scrollable = el.scrollHeight > el.clientHeight + 1;
    const atBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 2;
    const rows = document.querySelectorAll('[role=""row""][data-id]');
    // Step down roughly one screen at a time instead of jumping straight to the bottom: Drive's
    // list is virtualized, and a single jump can skip past the point that triggers the next chunk.
    el.scrollTop = Math.min(el.scrollHeight, el.scrollTop + Math.max(200, el.clientHeight * 0.9));
    if (rows.length > 0) rows[rows.length - 1].scrollIntoView({ block: 'end' });
    el.dispatchEvent(new Event('scroll', { bubbles: true }));
    el.dispatchEvent(new WheelEvent('wheel', { deltaY: el.clientHeight, bubbles: true }));
    return JSON.stringify({ scrollable: scrollable, atBottom: atBottom });
})()";

        // Cheap "has anything new rendered yet?" probe used while waiting after each scroll step.
        private const string RowSignatureScript = @"
(() => {
    const rows = document.querySelectorAll('[role=""row""][data-id]');
    const last = rows.length ? rows[rows.length - 1].getAttribute('data-id') : '';
    return rows.length + '|' + last;
})()";

        // Current on-screen row names in display order, plus the Name column header's aria-sort when
        // Drive exposes one. Used to check whether the folder is already sorted Name Z->A.
        private const string SortStateScript = @"
(() => {
    const junk = /^(me|owner|—|-|\d+(\.\d+)?\s*(bytes|KB|MB|GB|TB))$/i;
    const dateLike = /(\d{1,2}\/\d{1,2}\/\d{2,4})|((Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-zA-Z]*\.?\s+\d{1,2}(,?\s+\d{4})?)|(\d{1,2}:\d{2}\s*(AM|PM)?)|(\d+\s+(second|minute|hour|day|week|month|year)s?\s+ago)/i;
    const names = [];
    for (const row of document.querySelectorAll('[role=""row""][data-id]')) {
        const leaves = Array.from(row.querySelectorAll('*'))
            .filter(n => n.children.length === 0)
            .map(n => (n.textContent || '').trim())
            .filter(t => t.length > 0 && t.length < 200 && !junk.test(t) && !dateLike.test(t));
        const withExt = leaves.find(t => /\.[A-Za-z0-9]{2,5}$/.test(t));
        const name = withExt || leaves[0] || '';
        if (name) names.push(name);
    }
    const header = Array.from(document.querySelectorAll('[role=""columnheader""]'))
        .find(h => /^name\b/i.test(((h.getAttribute('aria-label') || h.innerText || '') + '').trim()));
    return JSON.stringify({ names: names, ariaSort: header ? (header.getAttribute('aria-sort') || '') : '' });
})()";

        // Clicks Drive's "Name" column header (toggles Name sort / its direction). Falls back to a
        // "Reverse sort direction" control if there's no Name header. Drive listens for the full
        // mouse sequence, not just click(), so all three events are sent.
        private const string ClickNameSortScript = @"
(() => {
    const fire = el => {
        for (const type of ['mousedown', 'mouseup', 'click']) {
            el.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: window, button: 0 }));
        }
    };
    const header = Array.from(document.querySelectorAll('[role=""columnheader""]'))
        .find(h => /^name\b/i.test(((h.getAttribute('aria-label') || h.innerText || '') + '').trim()));
    if (header) {
        fire(header.querySelector('[role=""button""], button') || header);
        return 'header';
    }
    const reverse = Array.from(document.querySelectorAll('[aria-label]'))
        .find(e => /reverse sort direction|sort direction/i.test(e.getAttribute('aria-label') || ''));
    if (reverse) {
        fire(reverse);
        return 'reverse';
    }
    return '';
})()";

        // Safety caps for the scroll-to-load loop (a few hundred version folders is ~10-20 passes).
        private const int MaxScrollPasses = 200;
        private const int ScrollSettleMs = 500;          // minimum wait after each scroll step
        private const int ScrollMaxWaitMs = 2500;        // ...extended while waiting for new rows to show up
        private const int IdlePassesBeforeDone = 5;
        private const int ScrollViewportHeight = 4000;   // taller viewport = more rows rendered per step

        // Drive's sort order is an account-wide preference, so once it's set to Name Z->A it sticks
        // (and every later _DRIVE_ivd first chunk already comes newest-first). Checked on every root
        // listing (every Refresh) so it's put back if anything - Drive in a browser, another tool -
        // changes it.
        private const int MaxSortClicksPerListing = 3;
        private const int SortRerenderTimeoutMs = 3000;

        private const string FolderMimeType = "application/vnd.google-apps.folder";

        private GoogleWebSession() { }

        public static bool IsDriveFolderPage(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("/folders/", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Direct-download URL that skips Drive's "can't scan this file for viruses" interstitial for
        /// large files (confirm=t). Cookies from the signed-in WebView2 profile ride along, so this
        /// works for files shared only with the signed-in account too.
        /// </summary>
        public static string DriveLargeFileDownloadUrl(string fileId) =>
            $"https://drive.usercontent.google.com/download?id={Uri.EscapeDataString(fileId)}&export=download&confirm=t";

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
        /// Lists a Drive folder's children WITH folder/file type (Build Repo tab). Always uses the real,
        /// authenticated Drive folder page (works for folders shared only with specific accounts).
        /// Throws SignInRequiredException if Google redirects to a login page.
        /// </summary>
        public async Task<List<DriveItem>> ListFolderItemsAsync(string folderId, CancellationToken ct, bool enforceNewestFirst = false)
        {
            await _gate.WaitAsync(ct);
            CoreWebView2? web = null;
            var navigated = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => navigated.TrySetResult(e);
            var revealedForScroll = false;

            try
            {
                web = await GetHiddenWebViewAsync();
                web.NavigationCompleted += OnNavigationCompleted;
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

                if (!Uri.TryCreate(web.Source, UriKind.Absolute, out var landed)
                    || !landed.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SignInRequiredException();
                }

                // _DRIVE_ivd is set by an inline script, so it's normally there right at
                // NavigationCompleted - but the DOM rows (fallback/merge source) render a bit later,
                // and an empty folder legitimately has neither. Poll a few times: stop as soon as
                // _DRIVE_ivd has been read (authoritative, even when it says "empty"), or rows appear.
                // Merged across every read, keyed by id. Entries with a real mimeType (from
                // _DRIVE_ivd) win over DOM-only guessed ones.
                var merged = new Dictionary<string, DriveItem>(StringComparer.Ordinal);
                var order = new List<string>();
                void Merge(List<DriveItem> batch)
                {
                    foreach (var item in batch)
                    {
                        if (merged.TryGetValue(item.Id, out var existing))
                        {
                            if (existing.TypeIsGuessed && !item.TypeIsGuessed) merged[item.Id] = item;
                        }
                        else
                        {
                            merged[item.Id] = item;
                            order.Add(item.Id);
                        }
                    }
                }

                for (var attempt = 0; attempt < 10; attempt++)
                {
                    ct.ThrowIfCancellationRequested();

                    var (ivdFound, parsed) = ParseFolderItems(await web.ExecuteScriptAsync(ListFolderItemsScript));
                    Merge(parsed);
                    if (ivdFound && attempt >= 2) break; // give the DOM a moment to add anything ivd missed
                    if (!ivdFound && merged.Count > 0) break;

                    await Task.Delay(400, ct);
                }

                // Scroll-to-load: keep scrolling the list and merging rows until several passes in a
                // row add nothing new while already at the bottom. Rows Drive recycles out of the DOM
                // while scrolling are fine - they're already in `merged`.
                // A WebView2 with IsVisible=false is a "hidden" page to Chromium: Drive's load-more
                // logic (scroll/intersection driven) never runs there, so scrolling alone did nothing.
                // Mark it visible for the scroll phase but park it far outside the window's client area
                // so nothing is ever drawn on screen; it goes back to hidden + parked in the finally.
                if (merged.Count > 0 && _hiddenController != null)
                {
                    _hiddenController.Bounds = new System.Drawing.Rectangle(-20000, -20000, 1280, ScrollViewportHeight);
                    _hiddenController.IsVisible = true;
                    revealedForScroll = true;
                    await Task.Delay(300, ct); // let the page pick up the visibility change
                }

                // Force Name Z->A so the first chunk Drive hands over is the newest versions (v271
                // before v250). Done here, while the page is "visible", so Drive actually re-renders.
                // Only for the Build Repo root (the version list): version numbers make the order
                // unambiguous there. Subfolders aren't checked - guessing Drive's order from names like
                // "Partial 1 10/28" was what flipped the sort back to A->Z on Refresh.
                if (enforceNewestFirst && merged.Count > 1 && await EnsureNameDescendingAsync(web, ct))
                {
                    var (_, resorted) = ParseFolderItems(await web.ExecuteScriptAsync(ListFolderItemsScript));
                    Merge(resorted);
                }

                if (merged.Count > 0)
                {
                    var idlePasses = 0;
                    for (var pass = 0; pass < MaxScrollPasses && idlePasses < IdlePassesBeforeDone; pass++)
                    {
                        ct.ThrowIfCancellationRequested();

                        var signatureBefore = await web.ExecuteScriptAsync(RowSignatureScript);
                        var (scrollable, wasAtBottom) = ParseScrollResult(await web.ExecuteScriptAsync(ScrollFolderListScript));
                        // Not scrollable = everything already fits on screen, nothing more to load. Give
                        // the page a couple of passes first, since layout may only just have happened.
                        if (!scrollable)
                        {
                            if (pass >= 2) break;
                            await Task.Delay(ScrollSettleMs, ct);
                            continue;
                        }

                        await WaitForNewRowsAsync(web, signatureBefore, ct);

                        var before = merged.Count;
                        var (_, parsed) = ParseFolderItems(await web.ExecuteScriptAsync(ListFolderItemsScript));
                        Merge(parsed);

                        idlePasses = (merged.Count == before && wasAtBottom) ? idlePasses + 1 : 0;
                    }
                }

                return order.Select(id => merged[id]).ToList();
            }
            finally
            {
                if (web != null)
                {
                    web.NavigationCompleted -= OnNavigationCompleted;
                }

                if (revealedForScroll && _hiddenController != null)
                {
                    _hiddenController.IsVisible = false;
                    _hiddenController.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 1600);
                }

                _gate.Release();
            }
        }

        /// <summary>
        /// After a scroll step: wait at least ScrollSettleMs, then keep waiting (up to ScrollMaxWaitMs)
        /// until the rendered rows actually change - Drive's next chunk can take a second or two over
        /// a slower connection, and the old fixed 500ms wait gave up before it arrived.
        /// </summary>
        private static async Task WaitForNewRowsAsync(CoreWebView2 web, string signatureBefore, CancellationToken ct)
        {
            await Task.Delay(ScrollSettleMs, ct);
            var waited = ScrollSettleMs;
            while (waited < ScrollMaxWaitMs)
            {
                if (await web.ExecuteScriptAsync(RowSignatureScript) != signatureBefore) return;
                await Task.Delay(250, ct);
                waited += 250;
            }
        }

        /// <summary>
        /// Makes sure the Build Repo root is sorted Name Z->A (newest version first), clicking Drive's
        /// Name column header until it is - once to switch to Name if it was sorted by date, once more to
        /// flip direction. Judged from the version numbers actually on screen; after each click it waits
        /// for the list to really re-render before judging again, so a slow re-sort can't be mistaken
        /// for "still A->Z" and clicked back. Returns true if it changed the sort.
        /// </summary>
        private async Task<bool> EnsureNameDescendingAsync(CoreWebView2 web, CancellationToken ct)
        {
            var changed = false;
            for (var click = 0; click <= MaxSortClicksPerListing; click++)
            {
                var (names, ariaSort) = ParseSortState(await web.ExecuteScriptAsync(SortStateScript));
                var descending = IsVersionDescending(names);

                // Can't tell from the names (no version numbers on screen) - fall back to the header's
                // own aria-sort if Drive exposes one, otherwise leave it alone rather than guess.
                descending ??= string.IsNullOrEmpty(ariaSort)
                    ? (bool?)null
                    : string.Equals(ariaSort, "descending", StringComparison.OrdinalIgnoreCase);

                if (descending != false) return changed;
                if (click == MaxSortClicksPerListing) return changed; // gave it every chance - stop

                var clicked = JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync(ClickNameSortScript));
                if (string.IsNullOrEmpty(clicked)) return changed;
                changed = true;

                await WaitForReorderAsync(web, names, ct);
            }
            return changed;
        }

        /// <summary>Polls until the visible row order differs from <paramref name="before"/> (or times out).</summary>
        private static async Task WaitForReorderAsync(CoreWebView2 web, List<string> before, CancellationToken ct)
        {
            var waited = 0;
            while (waited < SortRerenderTimeoutMs)
            {
                await Task.Delay(250, ct);
                waited += 250;
                var (now, _) = ParseSortState(await web.ExecuteScriptAsync(SortStateScript));
                if (now.Count > 0 && !now.Take(5).SequenceEqual(before.Take(5), StringComparer.Ordinal))
                {
                    await Task.Delay(300, ct); // let the rest of the list finish rendering
                    return;
                }
            }
        }

        private static (List<string> Names, string AriaSort) ParseSortState(string json)
        {
            try
            {
                var inner = JsonSerializer.Deserialize<string>(json) ?? "{}";
                using var doc = JsonDocument.Parse(inner);
                var names = new List<string>();
                if (doc.RootElement.TryGetProperty("names", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var n in arr.EnumerateArray())
                    {
                        var text = n.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) names.Add(text.Trim());
                    }
                }
                var aria = doc.RootElement.TryGetProperty("ariaSort", out var a) ? a.GetString() ?? string.Empty : string.Empty;
                return (names, aria);
            }
            catch (JsonException)
            {
                return (new List<string>(), string.Empty);
            }
        }

        /// <summary>
        /// true = version folders read newest-first (v271 before v250), false = oldest-first, null = can't
        /// tell (fewer than 2 rows with a version number). Uses the same version-number comparison the
        /// Build Repo tree sorts with, so "v271.4.0" vs "272 Files" is judged by number, not by text.
        /// </summary>
        private static bool? IsVersionDescending(List<string> names)
        {
            var keys = names
                .Where(n => !HasFileExtension(n))
                .Select(BuildRepoDefaults.VersionKey)
                .Where(k => k.Length > 0)
                .ToList();
            if (keys.Count < 2) return null;

            int asc = 0, desc = 0;
            for (var i = 1; i < keys.Count; i++)
            {
                var c = BuildRepoDefaults.CompareVersionKeys(keys[i - 1], keys[i]);
                if (c < 0) asc++;
                else if (c > 0) desc++;
            }
            if (asc == 0 && desc == 0) return null;
            return desc > asc;
        }

        private static (bool Scrollable, bool AtBottom) ParseScrollResult(string json)
        {
            try
            {
                var inner = JsonSerializer.Deserialize<string>(json) ?? "{}";
                using var doc = JsonDocument.Parse(inner);
                var root = doc.RootElement;
                var scrollable = root.TryGetProperty("scrollable", out var s) && s.ValueKind == JsonValueKind.True;
                var atBottom = root.TryGetProperty("atBottom", out var b) && b.ValueKind == JsonValueKind.True;
                return (scrollable, atBottom);
            }
            catch (JsonException)
            {
                return (false, true);
            }
        }

        private static (bool IvdFound, List<DriveItem> Items) ParseFolderItems(string json)
        {
            var inner = JsonSerializer.Deserialize<string>(json) ?? "{}";
            using var doc = JsonDocument.Parse(inner);
            var root = doc.RootElement;
            var ivdFound = root.TryGetProperty("ivd", out var ivd) && ivd.ValueKind == JsonValueKind.True;
            var items = new List<DriveItem>();

            if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var name = (el.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null)?.Trim();
                    var mime = el.TryGetProperty("mime", out var mimeEl) ? mimeEl.GetString() : null;
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;

                    // Shortcuts point somewhere else entirely - not something we can list/download here.
                    if (mime != null && mime.EndsWith(".shortcut", StringComparison.OrdinalIgnoreCase)) continue;

                    if (!string.IsNullOrEmpty(mime))
                    {
                        items.Add(new DriveItem(id, name, mime == FolderMimeType, TypeIsGuessed: false, MimeType: mime));
                    }
                    else
                    {
                        items.Add(new DriveItem(id, name, !HasFileExtension(name), TypeIsGuessed: true));
                    }
                }
            }

            return (ivdFound, items);
        }

        // "Full_Client.zip" -> true, "v271.4.0" / "v271.4.0 (Minor Patch)" / "1TC" -> false.
        // Extension = a final ".xxx" of 2-5 characters with at least one letter, so version-style
        // names ending in ".0" or ".10" don't count.
        public static bool HasFileExtension(string name)
        {
            var dot = name.LastIndexOf('.');
            if (dot < 0 || dot == name.Length - 1) return false;
            var ext = name.Substring(dot + 1);
            if (ext.Length < 2 || ext.Length > 5) return false;
            var hasLetter = false;
            foreach (var c in ext)
            {
                if (!char.IsLetterOrDigit(c)) return false;
                if (char.IsLetter(c)) hasLetter = true;
            }
            return hasLetter;
        }

        /// <summary>
        /// Downloads one (possibly multi-GB) Drive file to <paramref name="destinationPath"/> with
        /// byte progress. Unlike DownloadAsync this has no overall time limit - only a limit on how long
        /// Google takes to START sending the file - and runs in its own short-lived hidden WebView2
        /// (same profile/cookies), so it doesn't hold the shared gate for the whole transfer and the
        /// Build Repo tree / Maintenance tab stay usable while it runs. Writes to a .part file first.
        ///
        /// If the transfer drops mid-file and WebView2 says it can be resumed, this throws
        /// DriveDownloadInterruptedException with a live <see cref="ResumableDriveDownload"/> - the
        /// caller then owns it and must ResumeAsync or DiscardAsync it. Otherwise the partial is deleted.
        /// </summary>
        public async Task DownloadLargeFileAsync(string fileId, string destinationPath, IProgress<DriveDownloadProgress>? progress, CancellationToken ct)
        {
            CoreWebView2Controller? controller = null;
            CoreWebView2? web = null;
            ResumableDriveDownload? job = null;
            var handedOff = false;
            var partPath = destinationPath + ".part";
            var downloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); // pre-start failures only
            var triedInterstitial = false;
            var triedFallbackUrl = false;

            void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
            {
                if (job != null || controller == null)
                {
                    e.Cancel = true; // one file per job
                    return;
                }

                e.ResultFilePath = partPath;
                e.Handled = true;
                job = new ResumableDriveDownload(controller, e.DownloadOperation, partPath, destinationPath, progress);
                downloadStarted.TrySetResult(true);
            }

            async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                try
                {
                    if (downloadStarted.Task.IsCompleted) return;
                    await Task.Delay(NoDownloadGrace);
                    if (downloadStarted.Task.IsCompleted || web == null) return;

                    if (!e.IsSuccess)
                    {
                        done.TrySetException(new IOException($"Couldn't reach Google ({e.WebErrorStatus})."));
                        return;
                    }

                    if (Uri.TryCreate(web.Source, UriKind.Absolute, out var landed)
                        && landed.Host.Contains("accounts.google", StringComparison.OrdinalIgnoreCase))
                    {
                        done.TrySetException(new SignInRequiredException());
                        return;
                    }

                    // Drive's "file too large to scan" interstitial is a form with a Download anyway
                    // button - submit it once and let DownloadStarting take over.
                    if (!triedInterstitial)
                    {
                        triedInterstitial = true;
                        var submitted = await web.ExecuteScriptAsync(
                            "(() => { const f = document.querySelector('#download-form') || document.querySelector('form[action*=\"download\"]'); if (f) { f.submit(); return true; } return false; })()");
                        if (submitted == "true") return; // a new navigation/download follows
                    }

                    // Last resort before blaming sign-in: the classic uc?export=download endpoint
                    // (what the Maintenance tab's .md5 download already uses successfully).
                    if (!triedFallbackUrl)
                    {
                        triedFallbackUrl = true;
                        triedInterstitial = false; // that endpoint can show its own interstitial too
                        web.Navigate(DriveFileDownloadUrl(fileId));
                        return;
                    }

                    done.TrySetException(new SignInRequiredException()); // a page rendered instead of a file
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                if (File.Exists(partPath)) File.Delete(partPath);

                var environment = await GetEnvironmentAsync();
                var owner = Application.Current?.MainWindow
                            ?? throw new InvalidOperationException("Main window isn't available yet.");
                var hwnd = new WindowInteropHelper(owner).EnsureHandle();
                controller = await environment.CreateCoreWebView2ControllerAsync(hwnd);
                controller.IsVisible = false;
                web = controller.CoreWebView2;
                web.Settings.AreDefaultScriptDialogsEnabled = false;
                web.DownloadStarting += OnDownloadStarting;
                web.NavigationCompleted += OnNavigationCompleted;
                web.Navigate(DriveLargeFileDownloadUrl(fileId));

                // Only the START is time-limited; once bytes are flowing, wait as long as it takes.
                var first = await Task.WhenAny(downloadStarted.Task, done.Task, Task.Delay(OperationTimeout, ct));
                if (first == done.Task)
                {
                    await done.Task; // rethrows SignInRequired / IOException
                }
                if (first != downloadStarted.Task)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException("Google didn't start the download within 60 seconds.");
                }

                // Bytes are flowing - stop reacting to page navigations and wait as long as it takes.
                web.NavigationCompleted -= OnNavigationCompleted;

                try
                {
                    await job!.RunAsync(ct); // moves .part into place on success
                }
                catch (DriveDownloadInterruptedException ex) when (ex.Resumable != null)
                {
                    handedOff = true; // caller now owns the paused download (resume or discard)
                    // The finally below unhooks OUR DownloadStarting handler (which cancels anything
                    // after the first download). Hand that event to the paused job so a Resume is
                    // routed back to the same .part file instead of popping a Save As dialog.
                    ex.Resumable.TakeOverDownloadStarting();
                    throw;
                }
            }
            finally
            {
                // A completed (or discarded) job has already closed its controller, and touching
                // CoreWebView2 after that throws "CoreWebView2 members cannot be accessed after the
                // WebView2 control is disposed" - which used to turn every SUCCESSFUL download into
                // a failure. The handlers die with the closed controller anyway, so a throw here is
                // safe to ignore.
                if (web != null)
                {
                    try { web.DownloadStarting -= OnDownloadStarting; } catch (InvalidOperationException) { }
                    try { web.NavigationCompleted -= OnNavigationCompleted; } catch (InvalidOperationException) { }
                }

                if (!handedOff)
                {
                    if (job != null) job.Dispose(); // no-op if it completed
                    else controller?.Close();
                    try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
                }
            }
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

        /// <summary>Plain-English reason for a WebView2 interrupt code, for the Build Repo log/dialog.</summary>
        public static string DescribeInterruptReason(string reason) => reason switch
        {
            nameof(CoreWebView2DownloadInterruptReason.FileMalicious) =>
                "Windows security (SmartScreen or Defender) flagged the file as malicious (FileMalicious). " +
                "Check Windows Security > Virus & threat protection > Protection history to see what flagged it.",
            nameof(CoreWebView2DownloadInterruptReason.FileBlockedByPolicy) =>
                "a Windows/Edge policy blocked the file (FileBlockedByPolicy).",
            nameof(CoreWebView2DownloadInterruptReason.FileSecurityCheckFailed) =>
                "the Windows security scan of the file failed (FileSecurityCheckFailed).",
            DriveDownloadInterruptedException.PartialFileMissingReason =>
                "the partially downloaded file is no longer on disk, so there's nothing to resume from.",
            _ => reason
        };

        private async Task<CoreWebView2> GetHiddenWebViewAsync()
        {
            if (_hiddenController != null) return _hiddenController.CoreWebView2;

            var environment = await GetEnvironmentAsync();
            var owner = Application.Current?.MainWindow
                        ?? throw new InvalidOperationException("Main window isn't available yet.");
            var hwnd = new WindowInteropHelper(owner).EnsureHandle();

            _hiddenController = await environment.CreateCoreWebView2ControllerAsync(hwnd);
            _hiddenController.IsVisible = false;
            // Give the hidden page a real viewport. At the default 0x0, Drive's list has no visible
            // area, so its scroll-to-load-more logic (used by ListFolderItemsAsync) may never fire.
            _hiddenController.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 1600);
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
