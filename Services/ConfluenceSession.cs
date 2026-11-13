using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using CommandCenter.Model;
using Microsoft.Web.WebView2.Core;

namespace CommandCenter.Services
{
    /// <summary>Confluence answered with the Nexon SSO login (or no access) instead of the page data.</summary>
    public sealed class ConfluenceSignInRequiredException : Exception
    {
        public ConfluenceSignInRequiredException()
            : base("Confluence sign-in required (Nexon SSO), or this account can't see the Maple Central page.") { }
    }

    /// <summary>
    /// Talks to confluence.nexon.com as the signed-in user. Confluence sits behind Nexon SSO
    /// (nxas.nexon.com, SAML), so there's no API key to use - instead the user signs in once in an
    /// embedded WebView2 window (GoogleSignInWindow, reused with Confluence text), and this class
    /// copies that profile's confluence.nexon.com cookies into an HttpClient for the REST calls and
    /// the .7z download. Same WebView2 profile/environment as GoogleWebSession, so "Keep logged in"
    /// on the SSO page survives app restarts.
    ///
    /// When the Confluence session cookie has expired but the SSO one hasn't, RefreshSilentlyAsync
    /// lets a hidden WebView2 replay the SAML round-trip on its own - no window, no typing.
    ///
    /// Must be called on the UI thread (WebView2 requirement); the HTTP work itself is async.
    /// </summary>
    public sealed class ConfluenceSession : IDisposable
    {
        public static ConfluenceSession Shared { get; } = new();

        private static readonly TimeSpan SilentRefreshTimeout = TimeSpan.FromSeconds(25);

        // The Nexon SSO host Confluence bounces to (SAML). Its cookies are what let a hidden
        // WebView2 sign back in without the user once the Confluence session itself has expired.
        public const string SsoBaseUrl = "https://nxas.nexon.com";

        // How long the saved sign-in is kept on this PC after the last successful check. Every
        // successful check pushes this out again (sliding), so a regularly used install never
        // has to sign in again unless the server side ends the session (password change, IT
        // revoking SSO sessions, etc.).
        private static readonly TimeSpan RememberSignInFor = TimeSpan.FromDays(30);
        private CoreWebView2Controller? _hiddenController;

        private ConfluenceSession() { }

        /// <summary>True once the sign-in window has reached a real Confluence page.</summary>
        public static bool IsSignedInConfluencePage(string? url, string baseUrl)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
                !uri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var path = uri.AbsolutePath;
            return !path.Contains("login", StringComparison.OrdinalIgnoreCase)
                   && !path.Contains("nxsaml", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Finds the newest .7z on the page: attachments first (Confluence REST, sorted by upload
        /// time), falling back to scanning the rendered page body for a link ending in .7z. Throws
        /// ConfluenceSignInRequiredException when the cookies aren't good (after one silent refresh).
        /// </summary>
        public async Task<RemoteUpdatePackage> GetLatestPackageAsync(string baseUrl, string pageId, CancellationToken ct)
        {
            RemoteUpdatePackage package;
            try
            {
                package = await GetLatestPackageCoreAsync(baseUrl, pageId, ct);
            }
            catch (ConfluenceSignInRequiredException)
            {
                if (!await RefreshSilentlyAsync(baseUrl, pageId, ct)) throw;
                package = await GetLatestPackageCoreAsync(baseUrl, pageId, ct);
            }

            await RememberSignInAsync(baseUrl);
            return package;
        }

        private async Task<RemoteUpdatePackage> GetLatestPackageCoreAsync(string baseUrl, string pageId, CancellationToken ct)
        {
            using var client = await CreateClientAsync(baseUrl);
            var root = baseUrl.TrimEnd('/');

            // 1) Attachments on the page. The page carries lots of screenshot attachments too, so
            //    follow _links.next until every page of results has been seen.
            var candidates = new List<RemoteUpdatePackage>();
            string? next = $"/rest/api/content/{Uri.EscapeDataString(pageId)}/child/attachment?limit=200&expand=version";
            for (int pageGuard = 0; next != null && pageGuard < 50; pageGuard++)
            {
                var attachmentsJson = await GetJsonAsync(client, MakeAbsolute(root, next), ct);
                using var doc = JsonDocument.Parse(attachmentsJson);
                var linkBase = TryGetString(doc.RootElement, "_links", "base") ?? root;
                next = TryGetString(doc.RootElement, "_links", "next");

                if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in results.EnumerateArray())
                    {
                        var title = TryGetString(a, "title") ?? string.Empty;
                        if (!title.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) continue;

                        var download = TryGetString(a, "_links", "download");
                        if (string.IsNullOrEmpty(download)) continue;

                        candidates.Add(new RemoteUpdatePackage(
                            Id: TryGetString(a, "id") ?? title,
                            FileName: title,
                            DownloadUrl: MakeAbsolute(linkBase, download),
                            Uploaded: TryGetDate(a, "version", "when"),
                            AttachmentVersion: TryGetInt(a, "version", "number"),
                            SizeBytes: TryGetLong(a, "extensions", "fileSize")));
                    }
                }
            }

            if (candidates.Count > 0)
            {
                return candidates.OrderByDescending(c => c.Uploaded ?? DateTimeOffset.MinValue).First();
            }

            // 2) No .7z attached - look for one linked from the page body instead.
            var pageJson = await GetJsonAsync(client,
                $"{root}/rest/api/content/{Uri.EscapeDataString(pageId)}?expand=body.view,version", ct);

            using (var doc = JsonDocument.Parse(pageJson))
            {
                var html = TryGetString(doc.RootElement, "body", "view", "value") ?? string.Empty;
                var pageUpdated = TryGetDate(doc.RootElement, "version", "when");
                var pageVersion = TryGetInt(doc.RootElement, "version", "number");

                var link = Regex.Matches(html, "href=\"([^\"]+?\\.7z(?:\\?[^\"]*)?)\"", RegexOptions.IgnoreCase)
                    .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value))
                    .LastOrDefault(); // newest entries are usually added at the bottom of a release list

                if (link == null)
                {
                    throw new InvalidOperationException(
                        "Signed in fine, but the Maple Central page has no .7z attached or linked.");
                }

                var absolute = MakeAbsolute(root, link);
                var name = Path.GetFileName(new Uri(absolute).AbsolutePath);
                return new RemoteUpdatePackage(absolute, Uri.UnescapeDataString(name), absolute, pageUpdated, pageVersion, null);
            }
        }

        /// <summary>
        /// The page's rendered HTML (REST body.view) - used to read the "Build Release / Version
        /// Updates" notes for the update dialog. Called right after GetLatestPackageAsync, so the
        /// cookies are already known-good; no silent refresh here.
        /// </summary>
        public async Task<string> GetPageBodyHtmlAsync(string baseUrl, string pageId, CancellationToken ct)
        {
            using var client = await CreateClientAsync(baseUrl);
            var root = baseUrl.TrimEnd('/');
            var json = await GetJsonAsync(client,
                $"{root}/rest/api/content/{Uri.EscapeDataString(pageId)}?expand=body.view", ct);
            using var doc = JsonDocument.Parse(json);
            return TryGetString(doc.RootElement, "body", "view", "value") ?? string.Empty;
        }

        /// <summary>Streams the package to destinationPath (via a .part file).</summary>
        public async Task DownloadAsync(string baseUrl, RemoteUpdatePackage package, string destinationPath,
            IProgress<(long received, long? total)>? progress, CancellationToken ct)
        {
            var target = new Uri(package.DownloadUrl);
            var isConfluence = target.Host.Equals(new Uri(baseUrl).Host, StringComparison.OrdinalIgnoreCase);
            if (!isConfluence)
            {
                // A .7z linked to another host (Drive, SharePoint...) would need that site's own
                // sign-in, which this session doesn't have.
                throw new NotSupportedException(
                    $"The newest .7z on the page is linked from {target.Host}, not attached to the page. " +
                    "Attach the .7z to the Confluence page itself so the updater can download it.");
            }

            using var client = await CreateClientAsync(baseUrl);
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
            ThrowIfSignInPage(response);
            response.EnsureSuccessStatusCode();

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            var part = destinationPath + ".part";
            long? total = response.Content.Headers.ContentLength ?? package.SizeBytes;

            try
            {
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = File.Create(part))
                {
                    var buffer = new byte[1 << 16];
                    long received = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        received += read;
                        progress?.Report((received, total));
                    }
                }

                File.Move(part, destinationPath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                throw;
            }
        }

        /// <summary>
        /// Hidden-WebView2 replay of the SSO round-trip. Works whenever the nxas.nexon.com session
        /// ("Keep logged in") is still valid even though the Confluence one expired. Returns true if
        /// it ended up on a real Confluence page.
        /// </summary>
        public async Task<bool> RefreshSilentlyAsync(string baseUrl, string pageId, CancellationToken ct)
        {
            try
            {
                var web = await GetHiddenWebViewAsync();
                var landed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (e.IsSuccess && IsSignedInConfluencePage(web.Source, baseUrl)) landed.TrySetResult(true);
                }

                web.NavigationCompleted += OnCompleted;
                try
                {
                    web.Navigate(UpdateDefaults.PageUrlFor(baseUrl, pageId));
                    var done = await Task.WhenAny(landed.Task, Task.Delay(SilentRefreshTimeout, ct));
                    if (done != landed.Task) return false;
                    await RememberSignInAsync(baseUrl);
                    return true;
                }
                finally
                {
                    web.NavigationCompleted -= OnCompleted;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Makes the current sign-in survive closing the app. Both Confluence and Nexon SSO hand
        /// out *session* cookies (no expiry), which Chromium/WebView2 deletes whenever the browser
        /// process exits - that's why the sign-in would otherwise be asked for again on the next
        /// launch. This re-saves every session cookie for both hosts with an expiry
        /// RememberSignInFor from now. They stay inside this Windows user's WebView2 profile
        /// (%AppData%\CommandCenter\WebView2), which Chromium encrypts on disk with Windows DPAPI -
        /// readable only by this Windows account on this PC. No ID/password is ever stored.
        /// Persistent cookies already on the jar just get their expiry slid forward if it's sooner.
        /// </summary>
        public async Task RememberSignInAsync(string baseUrl)
        {
            try
            {
                var web = await GetHiddenWebViewAsync();
                var manager = web.CookieManager;
                var keepUntil = DateTime.Now.Add(RememberSignInFor);

                foreach (var url in new[] { baseUrl, SsoBaseUrl })
                {
                    foreach (var c in await manager.GetCookiesAsync(url))
                    {
                        if (!c.IsSession && c.Expires >= keepUntil) continue;

                        var copy = manager.CreateCookie(c.Name, c.Value, c.Domain, c.Path);
                        copy.IsHttpOnly = c.IsHttpOnly;
                        copy.IsSecure = c.IsSecure;
                        copy.SameSite = c.SameSite;
                        copy.Expires = keepUntil;
                        manager.AddOrUpdateCookie(copy);
                    }
                }
            }
            catch
            {
                // Best-effort - worst case the user is asked to sign in again next launch.
            }
        }

        /// <summary>Signs out of the saved Confluence/SSO session on this PC (Settings button).</summary>
        public async Task ForgetSignInAsync(string baseUrl)
        {
            var web = await GetHiddenWebViewAsync();
            foreach (var url in new[] { baseUrl, SsoBaseUrl })
            {
                foreach (var c in await web.CookieManager.GetCookiesAsync(url))
                {
                    web.CookieManager.DeleteCookie(c);
                }
            }
        }

        private async Task<HttpClient> CreateClientAsync(string baseUrl)
        {
            var web = await GetHiddenWebViewAsync();
            var cookies = await web.CookieManager.GetCookiesAsync(baseUrl);

            var jar = new CookieContainer();
            foreach (var c in cookies)
            {
                try { jar.Add(c.ToSystemNetCookie()); } catch { /* skip odd cookies */ }
            }

            var handler = new HttpClientHandler
            {
                CookieContainer = jar,
                UseCookies = true,
                // Redirects are how SSO says "not signed in" - see ThrowIfSignInPage. Confluence's
                // own attachment downloads don't redirect, so turning this off loses nothing.
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            };

            var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MapleCentral-Updater/1.0");
            client.DefaultRequestHeaders.Add("X-Atlassian-Token", "no-check");
            return client;
        }

        private static async Task<string> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
        {
            using var response = await client.GetAsync(url, ct);
            ThrowIfSignInPage(response);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Anonymous/unauthorized REST calls to a restricted page come back as 404, not 401.
                throw new ConfluenceSignInRequiredException();
            }

            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfluenceSignInRequiredException(); // got an HTML login page back
            }

            return await response.Content.ReadAsStringAsync(ct);
        }

        private static void ThrowIfSignInPage(HttpResponseMessage response)
        {
            var code = (int)response.StatusCode;
            if (code is >= 300 and < 400 || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ConfluenceSignInRequiredException();
            }
        }

        private async Task<CoreWebView2> GetHiddenWebViewAsync()
        {
            if (_hiddenController != null) return _hiddenController.CoreWebView2;

            var environment = await GoogleWebSession.Shared.GetEnvironmentAsync();
            var owner = Application.Current?.MainWindow
                        ?? throw new InvalidOperationException("Main window isn't available yet.");
            var hwnd = new WindowInteropHelper(owner).EnsureHandle();

            _hiddenController = await environment.CreateCoreWebView2ControllerAsync(hwnd);
            _hiddenController.IsVisible = false;
            _hiddenController.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            return _hiddenController.CoreWebView2;
        }

        private static string MakeAbsolute(string baseUrl, string link) =>
            Uri.TryCreate(link, UriKind.Absolute, out var abs)
                ? abs.ToString()
                : baseUrl.TrimEnd('/') + "/" + link.TrimStart('/');

        private static bool TryGet(JsonElement e, out JsonElement value, params string[] path)
        {
            value = e;
            foreach (var p in path)
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(p, out value)) return false;
            }
            return true;
        }

        private static string? TryGetString(JsonElement e, params string[] path) =>
            TryGet(e, out var v, path) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int? TryGetInt(JsonElement e, params string[] path) =>
            TryGet(e, out var v, path) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

        private static long? TryGetLong(JsonElement e, params string[] path)
        {
            if (!TryGet(e, out var v, path)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
            return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out l) ? l : null;
        }

        private static DateTimeOffset? TryGetDate(JsonElement e, params string[] path) =>
            DateTimeOffset.TryParse(TryGetString(e, path), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d) ? d : null;

        public void Dispose()
        {
            _hiddenController?.Close();
            _hiddenController = null;
        }
    }
}
