using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommandCenter.Model;
using CommandCenter.Services;
using CommandCenter.View;

namespace CommandCenter.ViewModel
{
    public enum BuildRepoNodeKind
    {
        Root,     // "MapleStory (GMS)" - the configured root Drive folder
        Version,  // a version folder under the root (v271.4.0); its children come from its "Build" subfolder
        Build,    // a direct child of a version's Build folder (1TC, 2TC, v271.4.0 (Minor Patch)) - the "head" download
        Folder,   // any folder below a build
        File,     // a downloadable file
        Message   // non-interactive placeholder ("loading", "empty", errors)
    }

    /// <summary>
    /// One node in the Build Repo tree. Folder-type nodes load their children lazily the first time
    /// they're expanded (the round "+" FAB in BuildRepoView toggles IsExpanded).
    /// </summary>
    public sealed class BuildRepoNode : ViewModelBase
    {
        private readonly Func<BuildRepoNode, Task>? _loader;
        private readonly Action<BuildRepoNode>? _onSelected;
        private bool _isExpanded;
        private bool _isSelected;
        private bool _isLoading;

        public BuildRepoNode(BuildRepoNodeKind kind, string name, string? driveId, BuildRepoNode? parent,
            Func<BuildRepoNode, Task>? loader = null, Action<BuildRepoNode>? onSelected = null)
        {
            Kind = kind;
            Name = name;
            DriveId = driveId;
            Parent = parent;
            _loader = loader;
            _onSelected = onSelected;
        }

        public BuildRepoNodeKind Kind { get; }
        public string Name { get; }
        public string? DriveId { get; }
        public BuildRepoNode? Parent { get; }
        public ObservableCollection<BuildRepoNode> Children { get; } = new();

        public bool IsFolder => Kind is BuildRepoNodeKind.Root or BuildRepoNodeKind.Version
                                     or BuildRepoNodeKind.Build or BuildRepoNodeKind.Folder;
        public bool IsExpandable => IsFolder;
        public bool IsMessage => Kind == BuildRepoNodeKind.Message;

        /// <summary>Only a build (head folder), a folder inside it, or a single file can be downloaded.</summary>
        public bool CanDownload => Kind is BuildRepoNodeKind.Build or BuildRepoNodeKind.Folder or BuildRepoNodeKind.File;

        public bool IsLoaded { get; set; }

        private string _sizeText = string.Empty;

        /// <summary>"3 files" for folders (counted recursively), "counting files…" while working.</summary>
        public string SizeText
        {
            get => _sizeText;
            set => SetProperty(ref _sizeText, value);
        }

        public bool SizeComputed { get; set; }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (!SetProperty(ref _isExpanded, value)) return;
                if (value && IsFolder && !IsLoaded && !IsLoading && _loader != null)
                {
                    _ = _loader(this);
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value) && value)
                {
                    _onSelected?.Invoke(this);
                }
            }
        }

        /// <summary>Segoe MDL2 Assets glyph shown before the name.</summary>
        public string Glyph => Kind switch
        {
            BuildRepoNodeKind.Root => "",    // game controller
            BuildRepoNodeKind.Version => "", // library
            BuildRepoNodeKind.Build => "",   // package
            BuildRepoNodeKind.Folder => "",  // folder
            BuildRepoNodeKind.File => "",    // document
            _ => string.Empty
        };

        /// <summary>"MapleStory (GMS) › v271.4.0 › 1TC › Partial Client"</summary>
        public string Breadcrumb
        {
            get
            {
                var parts = new List<string>();
                for (var n = this; n != null; n = n.Parent) parts.Insert(0, n.Name);
                return string.Join("  ›  ", parts);
            }
        }

        /// <summary>The build (head) folder this node sits under (itself if it is one) - e.g. 1TC - or null.</summary>
        public BuildRepoNode? BuildAncestor
        {
            get
            {
                for (var n = this; n != null; n = n.Parent)
                {
                    if (n.Kind == BuildRepoNodeKind.Build) return n;
                }
                return null;
            }
        }

        /// <summary>The version folder this node sits under (itself if it is one), or null for the root.</summary>
        public BuildRepoNode? VersionAncestor
        {
            get
            {
                for (var n = this; n != null; n = n.Parent)
                {
                    if (n.Kind == BuildRepoNodeKind.Version) return n;
                }
                return null;
            }
        }

        public void SetChildren(IEnumerable<BuildRepoNode> children)
        {
            Children.Clear();
            foreach (var c in children) Children.Add(c);
        }
    }

    /// <summary>A build tab the selected build can be downloaded into.</summary>
    public sealed class BuildRepoTargetOption
    {
        public BuildRepoTargetOption(TabSettings settings) => Settings = settings;
        public TabSettings Settings { get; }
        public Guid Id => Settings.Id;
        public string Title => Settings.Title;
        public string BuildPath => Settings.BuildPath;
        public bool HasBuildPath => !string.IsNullOrWhiteSpace(Settings.BuildPath);
        public override string ToString() => Title;
    }

    /// <summary>
    /// The "Build Repo" tab: browses a Google Drive tree (root -> version -> Build folder -> builds)
    /// through the same signed-in WebView2 session the Maintenance Verification tab uses, and
    /// downloads a build (or any folder/file inside one) into a chosen build tab's Current Build
    /// directory - optionally extracting it there (Full_*.zip as a New Build first, then every
    /// Partial_*.zip as a Patch) through the same BuildUpdateService the build tabs use.
    /// </summary>
    public sealed class BuildRepoViewModel : ViewModelBase
    {
        private readonly AppSettings _appSettings;
        private readonly SettingsService _settingsService;
        private readonly Func<IEnumerable<TabSettings>> _tabsProvider;
        private readonly Func<Guid, BuildSectionViewModel?> _findBuildSection;
        private readonly GoogleWebSession _session = GoogleWebSession.Shared;
        private readonly StringBuilder _log = new();

        private CancellationTokenSource? _cts;
        private CancellationTokenSource _browseCts = new();

        // Per-session cache (cleared by Refresh): folder id -> its children. The tree, the file counts
        // and the download all share it, so counting a build's files up front also makes downloading
        // it start faster.
        private readonly Dictionary<string, List<DriveItem>> _listingCache = new();
        private bool _initialized;

        private BuildRepoNode _root;
        private BuildRepoNode? _selectedNode;
        private BuildRepoTargetOption? _selectedTarget;
        private bool _extractAfterDownload;
        private bool _patchAfterDownload;
        private bool _isBusy;
        private bool _needsSignIn;
        private bool _isProgressIndeterminate;
        private double _progressPercent;
        private string _statusText = string.Empty;
        private string _logText = string.Empty;

        public BuildRepoViewModel(AppSettings appSettings, SettingsService settingsService,
            Func<IEnumerable<TabSettings>> tabsProvider, Func<Guid, BuildSectionViewModel?> findBuildSection)
        {
            _appSettings = appSettings;
            _settingsService = settingsService;
            _tabsProvider = tabsProvider;
            _findBuildSection = findBuildSection;
            _extractAfterDownload = appSettings.BuildRepoExtractAfterDownload;
            // The two modes are exclusive - if an older/hand-edited settings.json has both on, New Build wins.
            _patchAfterDownload = appSettings.BuildRepoPatchAfterDownload && !_extractAfterDownload;

            _root = CreateRoot();
            RootNodes.Add(_root);

            RefreshCommand = new AsyncRelayCommand(_ => RefreshTreeAsync(), _ => !IsBusy);
            SignInCommand = new AsyncRelayCommand(_ => SignInAsync(), _ => !IsBusy);
            DownloadCommand = new AsyncRelayCommand(_ => DownloadSelectedAsync(), _ => CanDownload);
            CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
            OpenBuildFolderCommand = new RelayCommand(_ => OpenFolder(SelectedTarget?.BuildPath),
                _ => SelectedTarget?.HasBuildPath == true);
        }

        // ---------- Bindable state ----------

        /// <summary>The TreeView's ItemsSource - just the one head node.</summary>
        public ObservableCollection<BuildRepoNode> RootNodes { get; } = new();

        public ObservableCollection<BuildRepoTargetOption> Targets { get; } = new();

        public string RootFolderUrl =>
            string.IsNullOrWhiteSpace(_appSettings.BuildRepoFolderUrl) ? BuildRepoDefaults.RootFolderUrl : _appSettings.BuildRepoFolderUrl;

        public BuildRepoNode? SelectedNode
        {
            get => _selectedNode;
            private set
            {
                if (SetProperty(ref _selectedNode, value))
                {
                    OnPropertyChanged(nameof(SelectionText));
                    OnPropertyChanged(nameof(CanDownload));
                    OnPropertyChanged(nameof(DownloadButtonText));
                }
            }
        }

        public string SelectionText => SelectedNode == null
            ? "Nothing selected - pick a build (e.g. 1TC), a folder inside one, or a single file."
            : SelectedNode.CanDownload
                ? SelectedNode.Breadcrumb
                : $"{SelectedNode.Breadcrumb}   (pick a build, folder or file below this to download)";

        public BuildRepoTargetOption? SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                if (!SetProperty(ref _selectedTarget, value)) return;
                OnPropertyChanged(nameof(TargetBuildPathText));
                OnPropertyChanged(nameof(TargetHasNoBuildPath));
                OnPropertyChanged(nameof(CanDownload));

                if (value != null && _appSettings.BuildRepoLastTargetTabId != value.Id)
                {
                    _appSettings.BuildRepoLastTargetTabId = value.Id;
                    TrySaveSettings();
                }
            }
        }

        public string TargetBuildPathText => SelectedTarget == null
            ? "No tab selected."
            : SelectedTarget.HasBuildPath
                ? SelectedTarget.BuildPath
                : "This tab has no Current Build directory set - set one in Settings first.";

        public bool TargetHasNoBuildPath => SelectedTarget != null && !SelectedTarget.HasBuildPath;

        /// <summary>
        /// "New Build" checkbox: download, then extract - Full_*.zip replaces the build (New Build),
        /// everything else is applied on top (Patch). Unchecks "Patch" when turned on.
        /// </summary>
        public bool ExtractAfterDownload
        {
            get => _extractAfterDownload;
            set
            {
                if (!SetProperty(ref _extractAfterDownload, value)) return;
                if (value && _patchAfterDownload)
                {
                    _patchAfterDownload = false;
                    _appSettings.BuildRepoPatchAfterDownload = false;
                    OnPropertyChanged(nameof(PatchAfterDownload));
                }
                OnPropertyChanged(nameof(DownloadButtonText));
                _appSettings.BuildRepoExtractAfterDownload = value;
                TrySaveSettings();
            }
        }

        /// <summary>
        /// "Patch" checkbox: download, then apply EVERY archive (Full_* included) with the build tabs'
        /// Patch function - the existing build is never wiped, files are overlaid on top of it.
        /// Unchecks "New Build" when turned on.
        /// </summary>
        public bool PatchAfterDownload
        {
            get => _patchAfterDownload;
            set
            {
                if (!SetProperty(ref _patchAfterDownload, value)) return;
                if (value && _extractAfterDownload)
                {
                    _extractAfterDownload = false;
                    _appSettings.BuildRepoExtractAfterDownload = false;
                    OnPropertyChanged(nameof(ExtractAfterDownload));
                }
                OnPropertyChanged(nameof(DownloadButtonText));
                _appSettings.BuildRepoPatchAfterDownload = value;
                TrySaveSettings();
            }
        }

        public string DownloadButtonText => ExtractAfterDownload ? "Download & New Build"
            : PatchAfterDownload ? "Download & Patch"
            : "Download";

        public bool CanDownload => !IsBusy && SelectedNode?.CanDownload == true && SelectedTarget?.HasBuildPath == true;

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(IsNotBusy));
                    OnPropertyChanged(nameof(CanDownload));
                }
            }
        }

        public bool IsNotBusy => !IsBusy;

        public bool NeedsSignIn { get => _needsSignIn; private set => SetProperty(ref _needsSignIn, value); }
        public bool IsProgressIndeterminate { get => _isProgressIndeterminate; private set => SetProperty(ref _isProgressIndeterminate, value); }
        public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }
        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
        public string LogText { get => _logText; private set => SetProperty(ref _logText, value); }

        public AsyncRelayCommand RefreshCommand { get; }
        public AsyncRelayCommand SignInCommand { get; }
        public AsyncRelayCommand DownloadCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand OpenBuildFolderCommand { get; }

        // ---------- Activation ----------

        /// <summary>Called from the View's Loaded event every time the tab is shown.</summary>
        public void OnActivated()
        {
            RefreshTargets();

            if (!_initialized)
            {
                _initialized = true;
                _root.IsExpanded = true; // kicks off the first listing
            }
        }

        private void RefreshTargets()
        {
            var previous = SelectedTarget?.Id ?? _appSettings.BuildRepoLastTargetTabId;

            Targets.Clear();
            foreach (var tab in _tabsProvider()
                         .Where(t => t.Kind == TabKind.BuildSection && t.IsVisible)
                         .OrderBy(t => t.Order))
            {
                Targets.Add(new BuildRepoTargetOption(tab));
            }

            // Re-assign through the field so restoring the pick doesn't re-save settings.
            _selectedTarget = Targets.FirstOrDefault(t => t.Id == previous) ?? Targets.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedTarget));
            OnPropertyChanged(nameof(TargetBuildPathText));
            OnPropertyChanged(nameof(TargetHasNoBuildPath));
            OnPropertyChanged(nameof(CanDownload));
        }

        // ---------- Tree ----------

        private BuildRepoNode CreateRoot()
        {
            BuildRepoDefaults.TryParseFolderId(RootFolderUrl, out var rootId);
            return new BuildRepoNode(BuildRepoNodeKind.Root, BuildRepoDefaults.RootTitle,
                string.IsNullOrEmpty(rootId) ? null : rootId, null, LoadChildrenAsync, OnNodeSelected);
        }

        private void OnNodeSelected(BuildRepoNode node) => SelectedNode = node;

        private BuildRepoNode Message(string text, BuildRepoNode parent) =>
            new(BuildRepoNodeKind.Message, text, null, parent);

        private async Task RefreshTreeAsync()
        {
            _browseCts.Cancel();
            _browseCts = new CancellationTokenSource();

            _listingCache.Clear();
            RootNodes.Clear();
            SelectedNode = null;
            _root = CreateRoot();
            RootNodes.Add(_root);
            _root.IsExpanded = true; // starts LoadChildrenAsync

            await Task.CompletedTask;
        }

        private async Task LoadChildrenAsync(BuildRepoNode node)
        {
            if (node.DriveId == null)
            {
                node.SetChildren(new[] { Message("The Build Repo folder URL in Settings isn't a valid Drive folder link.", node) });
                return;
            }

            var ct = _browseCts.Token;
            node.IsLoading = true;
            node.SetChildren(new[] { Message("Loading...", node) });

            try
            {
                List<BuildRepoNode> children;

                switch (node.Kind)
                {
                    case BuildRepoNodeKind.Root:
                    {
                        var items = await ListCachedAsync(node.DriveId, ct);
                        children = items
                            .Where(i => i.IsFolder)
                            .OrderByDescending(i => BuildRepoDefaults.VersionKey(i.Name), Comparer<int[]>.Create(BuildRepoDefaults.CompareVersionKeys))
                            .ThenBy(i => i.Name, NaturalStringComparer.Instance)
                            .Select(i => new BuildRepoNode(BuildRepoNodeKind.Version, i.Name, i.Id, node, LoadChildrenAsync, OnNodeSelected))
                            .ToList();
                        if (children.Count == 0) children.Add(Message("No version folders found.", node));
                        Log($"Listed {children.Count(c => !c.IsMessage)} version(s).");
                        break;
                    }

                    case BuildRepoNodeKind.Version:
                    {
                        // Step into the version's "Build" folder - its children are the builds.
                        var items = await ListCachedAsync(node.DriveId, ct);
                        var buildFolder = items.FirstOrDefault(i => i.IsFolder
                                              && string.Equals(i.Name.Trim(), BuildRepoDefaults.BuildFolderName, StringComparison.OrdinalIgnoreCase))
                                          ?? items.FirstOrDefault(i => string.Equals(i.Name.Trim(), BuildRepoDefaults.BuildFolderName, StringComparison.OrdinalIgnoreCase));

                        if (buildFolder == null)
                        {
                            var seen = items.Count == 0 ? "the folder looks empty" : "saw: " + string.Join(", ", items.Select(i => i.Name).Take(8));
                            children = new List<BuildRepoNode> { Message($"No \"{BuildRepoDefaults.BuildFolderName}\" folder in {node.Name} ({seen}).", node) };
                            break;
                        }

                        var builds = await ListCachedAsync(buildFolder.Id, ct);
                        children = SortFoldersFirst(builds)
                            .Select(i => new BuildRepoNode(i.IsFolder ? BuildRepoNodeKind.Build : BuildRepoNodeKind.File,
                                i.Name, i.Id, node, LoadChildrenAsync, OnNodeSelected))
                            .ToList();
                        if (children.Count == 0) children.Add(Message("The Build folder is empty.", node));
                        break;
                    }

                    default:
                    {
                        var items = await ListCachedAsync(node.DriveId, ct);
                        children = SortFoldersFirst(items)
                            .Select(i => new BuildRepoNode(i.IsFolder ? BuildRepoNodeKind.Folder : BuildRepoNodeKind.File,
                                i.Name, i.Id, node, LoadChildrenAsync, OnNodeSelected))
                            .ToList();
                        if (children.Count == 0) children.Add(Message("(empty)", node));
                        break;
                    }
                }

                node.SetChildren(children);
                node.IsLoaded = true;
                NeedsSignIn = false;

                // File counts: every build under a version (as soon as the version is opened), and
                // every folder under a build or folder. Runs in the background, one folder at a time.
                if (node.Kind != BuildRepoNodeKind.Root)
                {
                    _ = ComputeChildSizesAsync(node);
                }
            }
            catch (SignInRequiredException)
            {
                NeedsSignIn = true;
                node.SetChildren(new[] { Message("Sign in to Google (button above), then click Refresh.", node) });
                StatusText = "Google sign-in required.";
            }
            catch (OperationCanceledException)
            {
                node.SetChildren(Array.Empty<BuildRepoNode>());
            }
            catch (Exception ex)
            {
                node.SetChildren(new[] { Message($"Couldn't load this folder: {ex.Message}  (collapse and re-expand to retry)", node) });
                Log($"Error listing {node.Name}: {ex.Message}");
            }
            finally
            {
                node.IsLoading = false;
            }
        }

        private async Task<List<DriveItem>> ListCachedAsync(string folderId, CancellationToken ct)
        {
            if (_listingCache.TryGetValue(folderId, out var cached)) return cached;
            var items = await _session.ListFolderItemsAsync(folderId, ct);
            _listingCache[folderId] = items;
            return items;
        }

        // ---------- File counts ----------
        // (Byte sizes were tried and dropped - Google's download endpoint wouldn't report them
        // reliably for these files - so folders show how many files they hold instead.)

        private async Task ComputeChildSizesAsync(BuildRepoNode parent)
        {
            var ct = _browseCts.Token;
            try
            {
                foreach (var folder in parent.Children.Where(c => c.Kind is BuildRepoNodeKind.Build or BuildRepoNodeKind.Folder).ToList())
                {
                    await ComputeFolderSizeAsync(folder, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (SignInRequiredException)
            {
                NeedsSignIn = true;
            }
            catch (Exception ex)
            {
                Log($"Couldn't count files under {parent.Name}: {ex.Message}");
            }
        }

        // Number of files anywhere under this folder (recursive), e.g. "3 files".
        private async Task ComputeFolderSizeAsync(BuildRepoNode node, CancellationToken ct)
        {
            if (node.SizeComputed || node.DriveId == null) return;
            node.SizeText = "counting files…";

            try
            {
                var files = new List<(DriveItem Item, string RelativePath)>();
                await CollectFilesAsync(node.DriveId, string.Empty, files, ct, depth: 0, reportStatus: false);
                node.SizeText = files.Count == 0 ? "empty" : $"{files.Count} file{(files.Count == 1 ? "" : "s")}";
                node.SizeComputed = true;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SignInRequiredException)
            {
                node.SizeText = string.Empty;
                throw;
            }
            catch (Exception)
            {
                node.SizeText = string.Empty;
            }
        }

        private static IEnumerable<DriveItem> SortFoldersFirst(IEnumerable<DriveItem> items) =>
            items.OrderBy(i => i.IsFolder ? 0 : 1).ThenBy(i => i.Name, NaturalStringComparer.Instance);

        // ---------- Sign-in ----------

        private async Task SignInAsync()
        {
            if (await _session.IsSignedInAsync(CancellationToken.None))
            {
                StatusText = "Already signed in to Google.";
            }
            else
            {
                GoogleSignInWindow.Prompt(Application.Current?.MainWindow, RootFolderUrl, GoogleWebSession.IsDriveFolderPage);
            }

            await RefreshTreeAsync();
        }

        // ---------- Download (+ optional extract) ----------

        private async Task DownloadSelectedAsync()
        {
            var node = SelectedNode;
            var target = SelectedTarget;
            if (node == null || !node.CanDownload || node.DriveId == null || target == null || !target.HasBuildPath) return;

            var buildVm = _findBuildSection(target.Id);
            if (buildVm?.IsBusy == true)
            {
                MessageBox.Show($"The {target.Title} tab is busy with another update right now. Try again once it finishes.",
                    "Build Repo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var patchOnly = PatchAfterDownload;           // "Patch" checkbox - never wipe the build
            var extract = ExtractAfterDownload || patchOnly;
            var buildPath = target.BuildPath;
            var externalUpdateStarted = false;
            string? stagingRoot = null;
            var ct = Begin($"Listing files in {node.Name}...", indeterminate: true);
            ClearLog();
            Log($"Selected: {node.Breadcrumb}");
            Log($"Target tab: {target.Title}  ({buildPath})");

            try
            {
                // 1. Work out every file to fetch, with its path relative to the selected node.
                var files = new List<(DriveItem Item, string RelativePath)>();
                if (node.Kind == BuildRepoNodeKind.File)
                {
                    files.Add((new DriveItem(node.DriveId, node.Name, false, false), BuildRepoDefaults.MakeSafeFileName(node.Name)));
                }
                else
                {
                    await CollectFilesAsync(node.DriveId, string.Empty, files, ct, depth: 0);
                }

                if (files.Count == 0)
                {
                    StatusText = $"{node.Name} has no files to download.";
                    return;
                }

                Log($"Found {files.Count} file(s).");

                // 2. Nothing to extract? Offer to just download instead.
                var archiveNames = files.Select(f => f.RelativePath).Where(BuildRepoDefaults.IsArchive).ToList();
                if (extract && archiveNames.Count == 0)
                {
                    var answer = MessageBox.Show(
                        $"None of the {files.Count} file(s) in {node.Name} are .zip/.7z archives, so there's nothing to extract.\n\nDownload them into {target.Title}'s build folder instead?",
                        "Build Repo", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer != MessageBoxResult.Yes)
                    {
                        StatusText = "Download cancelled.";
                        return;
                    }
                    extract = false;
                    patchOnly = false;
                }

                // 3. Confirm. Extraction with a Full_* archive replaces the whole build.
                var fullNames = files.Select(f => f.RelativePath).Where(BuildRepoDefaults.IsFullArchive)
                    .OrderBy(n => n, NaturalStringComparer.Instance).ToList();
                var hasFull = node.Kind == BuildRepoNodeKind.File
                    ? BuildRepoDefaults.IsFullArchive(node.Name)
                    : fullNames.Count > 0;

                // In Patch mode a Full_* archive is just overlaid like any other patch - nothing is wiped.
                if (patchOnly) hasFull = false;

                string confirmText;
                if (patchOnly)
                {
                    confirmText = $"Download {files.Count} file(s) from {node.Name} and PATCH the existing {target.Title} build at:\n{buildPath}\n\n" +
                                  (fullNames.Count > 0 || (node.Kind == BuildRepoNodeKind.File && BuildRepoDefaults.IsFullArchive(node.Name))
                                      ? "Full_* archive(s) are overlaid on top of the current build instead of replacing it.\n" : "") +
                                  "Everything is applied exactly like the Patch button (Partial Client folders merged in order, nested archives unpacked, AdminClient/Bin flattened). " +
                                  "Existing files are kept - only files with the same names are overwritten.\n\nContinue?";
                }
                else if (extract && hasFull)
                {
                    confirmText = $"Download {files.Count} file(s) from {node.Name}, then REPLACE the existing {target.Title} build at:\n{buildPath}\n\n" +
                                  $"1. {(fullNames.Count > 0 ? fullNames[0] : node.Name)} - applied exactly like New Build.\n" +
                                  (node.Kind == BuildRepoNodeKind.File ? "" :
                                   "2. Everything else (Partial Client folders / Partial_*.zip) - applied exactly like Patch, using the same Partial Client rules as the Patch button.\n") +
                                  "\nContinue?";
                }
                else if (extract)
                {
                    confirmText = $"Download {files.Count} file(s) from {node.Name} and apply it as a PATCH on top of the existing {target.Title} build at:\n{buildPath}\n\n" +
                                  "Same rules as the Patch button (Partial Client folders merged in order, nested archives unpacked, AdminClient/Bin flattened). No Full_* archive was found, so the current build is NOT wiped first.\n\nContinue?";
                }
                else
                {
                    confirmText = $"Download {files.Count} file(s) from {node.Name} into {target.Title}'s Current Build directory:\n{buildPath}\n\nExisting files with the same names will be overwritten.\n\nContinue?";
                }

                var versionLabel = VersionLabelFor(node);
                if (extract && versionLabel != null)
                {
                    confirmText = confirmText.Replace("\n\nContinue?",
                        $"\n\n{target.Title}'s Version Number will be set to {versionLabel} once it finishes.\n\nContinue?");
                }

                if (MessageBox.Show(confirmText, extract && hasFull ? "Confirm Download & New Build" : patchOnly ? "Confirm Download & Patch" : "Confirm Download",
                        MessageBoxButton.YesNo, extract && hasFull ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    StatusText = "Download cancelled.";
                    return;
                }

                // 4. Download everything, one file at a time.
                var downloadRoot = extract ? (stagingRoot = StagingFolderFor(target, node.Name)) : buildPath;
                if (extract && Directory.Exists(stagingRoot!))
                {
                    TryDeleteDirectory(stagingRoot!); // stale leftovers from an earlier failed run
                }
                Directory.CreateDirectory(downloadRoot);
                Log($"Downloading to: {downloadRoot}");

                var downloadBand = extract ? 60.0 : 100.0;
                var downloaded = new List<string>();
                var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                IsProgressIndeterminate = false;

                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (item, relative) = files[i];
                    var destination = UniquePath(Path.Combine(downloadRoot, relative), usedPaths);
                    int index = i; // captured by the Progress callback below (posted async - the loop variable may have moved on)
                    int fileNumber = i + 1;
                    var name = Path.GetFileName(destination);

                    StatusText = $"Downloading {fileNumber} of {files.Count}: {name}...";
                    var progress = new Progress<DriveDownloadProgress>(p =>
                    {
                        double fraction = p.TotalBytes is > 0 ? (double)p.BytesReceived / p.TotalBytes.Value : 0;
                        ProgressPercent = (index + fraction) / files.Count * downloadBand;
                        var sizeText = p.TotalBytes is > 0
                            ? $"{FormatBytes(p.BytesReceived)} of {FormatBytes(p.TotalBytes.Value)} ({fraction:P0})"
                            : FormatBytes(p.BytesReceived);
                        StatusText = $"Downloading {fileNumber} of {files.Count}: {name} - {sizeText}";
                    });

                    await _session.DownloadLargeFileAsync(item.Id, destination, progress, ct);
                    downloaded.Add(destination);
                    Log($"  ✓ {relative}  ({FormatBytes(new FileInfo(destination).Length)})");
                }

                ProgressPercent = downloadBand;
                NeedsSignIn = false;

                if (!extract)
                {
                    StatusText = $"Downloaded {downloaded.Count} file(s) from {node.Name} into {target.Title}'s build folder.";
                    Log("Done.");
                    return;
                }

                // 5. Extract - through the exact same pipeline the build tabs' own buttons use:
                //    - a Full_* archive is applied like New Build with that archive selected
                //      (BuildUpdateService.RunAsync, NewBuild - wipes the build folder first);
                //    - whatever is left of the download (Partial Client folders, Partial_*.zip,
                //      checksums.md5, ...) is applied like Patch applied to an archive of that folder
                //      (BuildUpdateService.RunPatchFromFolderAsync - same Partial Client resolution,
                //      numbered-folder merge, nested-archive unwrapping and AdminClient/Bin flattening).
                Log(patchOnly ? "Mode: Patch (existing build is kept)." : "Mode: New Build.");
                buildVm?.BeginExternalUpdate($"Build Repo: {(patchOnly ? "patching" : "extracting")} {node.Name}...");
                externalUpdateStarted = true;

                var steps = new List<(string Label, Func<IProgress<UpdateProgress>, Task> Run)>();
                var staging = stagingRoot!;

                if (node.Kind == BuildRepoNodeKind.File)
                {
                    var file = downloaded[0];
                    var mode = !patchOnly && BuildRepoDefaults.IsFullArchive(file) ? UpdateMode.NewBuild : UpdateMode.Patch;
                    steps.Add(($"{(mode == UpdateMode.NewBuild ? "New Build" : "Patch")}: {Path.GetFileName(file)}",
                        p => BuildUpdateService.RunAsync(file, buildPath, mode, p, ct)));
                }
                else
                {
                    var fullArchives = downloaded.Where(BuildRepoDefaults.IsFullArchive)
                        .OrderBy(f => Path.GetRelativePath(staging, f), NaturalStringComparer.Instance)
                        .ToList();

                    for (int k = 0; k < fullArchives.Count; k++)
                    {
                        var full = fullArchives[k];
                        var mode = k == 0 && !patchOnly ? UpdateMode.NewBuild : UpdateMode.Patch;
                        steps.Add(($"{(mode == UpdateMode.NewBuild ? "New Build" : "Patch")}: {Path.GetFileName(full)}", async p =>
                        {
                            await BuildUpdateService.RunAsync(full, buildPath, mode, p, ct);
                            RemoveAppliedArchive(full, staging); // so the Patch step below doesn't see it again
                        }));
                    }

                    steps.Add(("Patch: remaining build files", async p =>
                    {
                        if (BuildUpdateService.HasPatchContent(staging))
                        {
                            await BuildUpdateService.RunPatchFromFolderAsync(staging, buildPath, p, ct);
                        }
                        else
                        {
                            Log(fullArchives.Count > 0
                                ? "  Nothing left to patch after the Full_* archive (only checksum files) - skipped."
                                : "  Nothing to patch (only checksum files) - skipped.");
                        }
                    }));
                }

                for (int i = 0; i < steps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (label, run) = steps[i];
                    int index = i;
                    int stepNumber = i + 1;
                    Log($"Step {stepNumber} of {steps.Count} - {label}");

                    var progress = new Progress<UpdateProgress>(p =>
                    {
                        var pct = downloadBand + (index + p.PercentComplete / 100.0) / steps.Count * (100.0 - downloadBand);
                        var text = $"Step {stepNumber} of {steps.Count} ({label}): {p.Status}";
                        ProgressPercent = pct;
                        StatusText = text;
                        buildVm?.ReportExternalProgress(pct, text);
                    });

                    await run(progress);
                }

                // 6. Refresh the target tab's launch dropdown from what's actually on disk now, and
                //    stamp its Version Number with the version this build came from. Setting
                //    VersionNumber raises the tab's own Settings_PropertyChanged, so its header and
                //    Documents folder follow along the same way a normal New Build/Patch does.
                RescanExecutables(target.Settings);
                if (versionLabel != null)
                {
                    target.Settings.VersionNumber = versionLabel;
                    Log($"Set {target.Title}'s Version Number to {versionLabel}.");
                }
                TrySaveSettings();

                TryDeleteDirectory(stagingRoot!);
                CleanupEmptyStagingParents(stagingRoot!);

                ProgressPercent = 100;
                var doneText = $"{node.Name} {(patchOnly ? "patched into" : "extracted into")} {target.Title}. Ready to launch.";
                StatusText = doneText;
                Log("Done. Downloaded files removed from the staging folder.");
                buildVm?.EndExternalUpdate(doneText, succeeded: true);
                externalUpdateStarted = false;
            }
            catch (SignInRequiredException)
            {
                NeedsSignIn = true;
                StatusText = "Google sign-in required - sign in, then try the download again.";
                Log("Stopped: Google sign-in required.");
            }
            catch (OperationCanceledException)
            {
                StatusText = "Download cancelled.";
                Log("Cancelled.");
            }
            catch (Exception ex)
            {
                StatusText = $"Failed: {ex.Message}";
                Log($"Failed: {ex.Message}");
                if (stagingRoot != null && Directory.Exists(stagingRoot))
                {
                    Log($"Downloaded files are still in: {stagingRoot}");
                }
                MessageBox.Show(ex.Message, "Build Repo", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (externalUpdateStarted)
                {
                    buildVm?.EndExternalUpdate(StatusText, succeeded: false);
                }
                End();
            }
        }

        // Recursively lists a Drive folder into (file, relative path) pairs, mirroring its structure.
        private async Task CollectFilesAsync(string folderId, string relativePrefix,
            List<(DriveItem Item, string RelativePath)> files, CancellationToken ct, int depth, bool reportStatus = true)
        {
            if (depth > 12) throw new InvalidOperationException("Folder nesting is deeper than 12 levels - stopping in case of a loop.");

            var items = await ListCachedAsync(folderId, ct);
            foreach (var item in SortFoldersFirst(items))
            {
                ct.ThrowIfCancellationRequested();
                var safeName = BuildRepoDefaults.MakeSafeFileName(item.Name);
                var relative = string.IsNullOrEmpty(relativePrefix) ? safeName : Path.Combine(relativePrefix, safeName);

                if (item.IsFolder)
                {
                    if (reportStatus) StatusText = $"Listing {relative}...";
                    await CollectFilesAsync(item.Id, relative, files, ct, depth + 1, reportStatus);
                }
                else
                {
                    files.Add((item, relative));
                }
            }
        }

        // Version Number stamped onto the target tab after an extract: version + the build (head)
        // folder being downloaded, e.g. "272 (1TC)" - the build part even when the selection is a
        // folder or file inside it. See BuildRepoDefaults.VersionLabel for the exact rules.
        private static string? VersionLabelFor(BuildRepoNode node) =>
            BuildRepoDefaults.VersionLabel(node.VersionAncestor?.Name, node.BuildAncestor?.Name);

        // Deletes a Full_* archive once it's been applied, plus its own subfolder (e.g. "Full Client\")
        // when that's left holding nothing but checksum files - otherwise those leftovers would be
        // treated as patch content by the Patch step that follows.
        private static void RemoveAppliedArchive(string archivePath, string stagingRoot)
        {
            try
            {
                if (File.Exists(archivePath)) File.Delete(archivePath);

                var parent = Path.GetDirectoryName(archivePath);
                var root = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar);
                while (parent != null
                       && !string.Equals(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)
                       && !BuildUpdateService.HasPatchContent(parent))
                {
                    var next = Path.GetDirectoryName(parent);
                    Directory.Delete(parent, recursive: true);
                    parent = next;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        // <parent of build folder>\_BuildRepoDownloads\<tab title>\<selected node name>
        private static string StagingFolderFor(BuildRepoTargetOption target, string nodeName)
        {
            var trimmed = target.BuildPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(trimmed);
            var baseDir = string.IsNullOrEmpty(parent) ? Path.GetPathRoot(target.BuildPath)! : parent;
            return Path.Combine(baseDir, BuildRepoDefaults.StagingFolderName,
                BuildRepoDefaults.MakeSafeFileName(target.Title), BuildRepoDefaults.MakeSafeFileName(nodeName));
        }

        private static void CleanupEmptyStagingParents(string stagingRoot)
        {
            try
            {
                var tabFolder = Path.GetDirectoryName(stagingRoot);
                var stagingBase = tabFolder == null ? null : Path.GetDirectoryName(tabFolder);
                foreach (var dir in new[] { tabFolder, stagingBase })
                {
                    if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private static string UniquePath(string path, HashSet<string> used)
        {
            var candidate = path;
            var n = 2;
            while (!used.Add(candidate))
            {
                candidate = Path.Combine(Path.GetDirectoryName(path)!,
                    $"{Path.GetFileNameWithoutExtension(path)} ({n++}){Path.GetExtension(path)}");
            }
            return candidate;
        }

        // Same rescan Pushed to Live does, but keeps the enabled/disabled choice for any exe that was
        // already in the list so a re-download doesn't silently re-enable something turned off in Settings.
        private static void RescanExecutables(TabSettings settings)
        {
            var previous = settings.Executables.ToDictionary(e => e.FileName, e => e.IsEnabled, StringComparer.OrdinalIgnoreCase);
            settings.Executables = ExecutableScanner.ScanExecutables(settings.BuildPath)
                .Select(name => new TabExecutableEntry
                {
                    FileName = name,
                    IsEnabled = !previous.TryGetValue(name, out var enabled) || enabled
                })
                .ToList();
        }

        // ---------- Helpers ----------

        private void OpenFolder(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                StatusText = $"Couldn't open the folder: {ex.Message}";
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private void TrySaveSettings()
        {
            try { _settingsService.Save(_appSettings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private CancellationToken Begin(string status, bool indeterminate)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            ProgressPercent = 0;
            IsProgressIndeterminate = indeterminate;
            StatusText = status;
            IsBusy = true;
            return _cts.Token;
        }

        private void End()
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            _cts?.Dispose();
            _cts = null;
        }

        private void ClearLog()
        {
            _log.Clear();
            LogText = string.Empty;
        }

        private void Log(string line)
        {
            _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");
            LogText = _log.ToString();
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} B" : $"{value:F2} {units[unit]}";
        }
    }
}
