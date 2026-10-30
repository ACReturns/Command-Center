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

        private readonly Action<BuildRepoNode>? _onBatchToggled;
        private bool _isBatchChecked;

        public BuildRepoNode(BuildRepoNodeKind kind, string name, string? driveId, BuildRepoNode? parent,
            Func<BuildRepoNode, Task>? loader = null, Action<BuildRepoNode>? onSelected = null,
            Action<BuildRepoNode>? onBatchToggled = null)
        {
            Kind = kind;
            Name = name;
            DriveId = driveId;
            Parent = parent;
            _loader = loader;
            _onSelected = onSelected;
            _onBatchToggled = onBatchToggled;
        }

        // "Partial", "Partial 2", "Partial 1 10/28", "Partial_3", "Partial Client"... - but not "Partials".
        private static readonly System.Text.RegularExpressions.Regex PartialFolderPattern =
            new(@"^\s*Partial(?![A-Za-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // "Full", "Full Client", "Full 10/28", "Full_Client"... - but not "Fullscreen".
        private static readonly System.Text.RegularExpressions.Regex FullFolderPattern =
            new(@"^\s*Full(?![A-Za-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>A Full folder inside a build - always applied before any Partial in a batch.</summary>
        public bool IsFullFolder => Kind == BuildRepoNodeKind.Folder && FullFolderPattern.IsMatch(Name);

        /// <summary>Full folder or Full_*.zip/.7z file - tagged "New Build" in the tree.</summary>
        public bool IsFullItem => IsFullFolder || (Kind == BuildRepoNodeKind.File && BuildRepoDefaults.IsFullArchive(Name));

        /// <summary>Partial folder or Partial_*.zip/.7z file - tagged "Patch" in the tree.</summary>
        public bool IsPartialItem => (Kind == BuildRepoNodeKind.Folder && PartialFolderPattern.IsMatch(Name))
                                     || (Kind == BuildRepoNodeKind.File && BuildRepoDefaults.IsPartialArchive(Name));

        public bool HasModeTag => IsFullItem || IsPartialItem;

        /// <summary>
        /// A Full or Partial folder inside a build - these get a checkbox so several can be
        /// batch-downloaded and applied one after another (Full first, then tree order) in one run.
        /// </summary>
        public bool IsBatchEligible => Kind == BuildRepoNodeKind.Folder && DriveId != null && BuildAncestor != null
                                       && (PartialFolderPattern.IsMatch(Name) || FullFolderPattern.IsMatch(Name));

        public bool IsBatchChecked
        {
            get => _isBatchChecked;
            set
            {
                if (SetProperty(ref _isBatchChecked, value)) _onBatchToggled?.Invoke(this);
            }
        }

        /// <summary>Unchecks without notifying the view model (used when it clears/replaces the batch itself).</summary>
        public void ClearBatchSilently()
        {
            if (!_isBatchChecked) return;
            _isBatchChecked = false;
            OnPropertyChanged(nameof(IsBatchChecked));
        }

        public bool IsAncestorOf(BuildRepoNode other)
        {
            for (var n = other.Parent; n != null; n = n.Parent)
            {
                if (ReferenceEquals(n, this)) return true;
            }
            return false;
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
        public bool BuildFolderExists => HasBuildPath && Directory.Exists(Settings.BuildPath);
        public string VersionText => string.IsNullOrWhiteSpace(Settings.VersionNumber) ? "Version not set" : Settings.VersionNumber;
        // Transfer Build dropdowns: "GMS (271.0.3)".
        public string TransferDisplay => $"{Title} ({VersionText})";
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
        private bool _isAwaitingResume;
        private TaskCompletionSource<bool>? _resumeDecision;
        private bool _useLocalFiles;
        private bool _localNewBuildFirst;
        private string _localVersionNumber = string.Empty;
        // Local Files sub-mode: false = extract archives (original behavior), true = Transfer Build.
        private bool _localTransferMode;
        private BuildRepoTargetOption? _transferSource;
        private BuildRepoTargetOption? _transferTarget;

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
            ResumeCommand = new RelayCommand(_ => _resumeDecision?.TrySetResult(true), _ => IsAwaitingResume);
            OpenBuildFolderCommand = new RelayCommand(_ => OpenFolder(SelectedTarget?.BuildPath),
                _ => SelectedTarget?.HasBuildPath == true);
            ClearBatchCommand = new RelayCommand(_ => ClearBatch(), _ => IsBatchMode && !IsBusy);

            AddLocalFilesCommand = new RelayCommand(_ => AddLocalFiles(), _ => !IsBusy);
            ClearLocalFilesCommand = new RelayCommand(_ => LocalFiles.Clear(), _ => !IsBusy && LocalFiles.Count > 0);
            RemoveLocalFileCommand = new RelayCommand(p => { if (p is LocalExtractFileViewModel f) LocalFiles.Remove(f); }, _ => !IsBusy);
            LocalFiles.CollectionChanged += (_, _) => OnLocalFilesChanged();

            OpenTransferSourceFolderCommand = new RelayCommand(_ => OpenFolder(TransferSource?.BuildPath),
                _ => TransferSource?.HasBuildPath == true);
            OpenTransferTargetFolderCommand = new RelayCommand(_ => OpenFolder(TransferTarget?.BuildPath),
                _ => TransferTarget?.HasBuildPath == true);
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

        public string SelectionText => IsBatchMode
            ? $"Batch ({_batch.Count}), applied in this order:  {_batch[0].BuildAncestor?.Breadcrumb}  ›  " +
              string.Join("  →  ", _batch.Select(n => n.Name))
            : SelectedNode == null
                ? "Nothing selected - pick a build (e.g. 1TC), a folder inside one, or a single file. Tick several Full/Partial folders to batch them."
                : SelectedNode.CanDownload
                    ? SelectedNode.Breadcrumb
                    : $"{SelectedNode.Breadcrumb}   (pick a build, folder or file below this to download)";

        // ---------- Batch (several Full/Partial folders of one build) ----------

        // Checked Full/Partial folders: Full first, then tree order (= the order they get applied).
        private readonly List<BuildRepoNode> _batch = new();

        /// <summary>True once any Full/Partial folder is ticked - the download then uses the ticked folders, not the highlighted row.</summary>
        public bool IsBatchMode => _batch.Count > 0;

        private void OnBatchToggled(BuildRepoNode node)
        {
            if (node.IsBatchChecked)
            {
                // Same build only - ticking a folder under a different build starts a new batch.
                var build = node.BuildAncestor;
                var dropped = _batch.Where(n => !ReferenceEquals(n.BuildAncestor, build)
                                                || n.IsAncestorOf(node) || node.IsAncestorOf(n)).ToList();
                foreach (var d in dropped)
                {
                    d.ClearBatchSilently();
                    _batch.Remove(d);
                }
                if (dropped.Any(d => !ReferenceEquals(d.BuildAncestor, build)))
                {
                    StatusText = $"Batch reset - batched folders have to come from the same build ({build?.Name}).";
                }
                if (!_batch.Contains(node)) _batch.Add(node);
                _batch.Sort(CompareTreeOrder);
            }
            else
            {
                _batch.Remove(node);
            }
            OnBatchChanged();
        }

        private void ClearBatch()
        {
            foreach (var n in _batch) n.ClearBatchSilently();
            _batch.Clear();
            OnBatchChanged();
        }

        private void OnBatchChanged()
        {
            OnPropertyChanged(nameof(IsBatchMode));
            OnPropertyChanged(nameof(SelectionText));
            OnPropertyChanged(nameof(CanDownload));
            OnPropertyChanged(nameof(DownloadButtonText));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        // Full folders before Partials (the Full is the base the Partials patch on top of), then
        // position in the tree: compare index paths from the root (sibling order is already natural-sorted).
        private static int CompareTreeOrder(BuildRepoNode a, BuildRepoNode b)
        {
            if (a.IsFullFolder != b.IsFullFolder) return a.IsFullFolder ? -1 : 1;

            static List<int> PathOf(BuildRepoNode n)
            {
                var path = new List<int>();
                for (var c = n; c.Parent != null; c = c.Parent) path.Insert(0, c.Parent.Children.IndexOf(c));
                return path;
            }

            var pa = PathOf(a);
            var pb = PathOf(b);
            for (int i = 0; i < Math.Min(pa.Count, pb.Count); i++)
            {
                if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
            }
            return pa.Count.CompareTo(pb.Count);
        }

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

        public string DownloadButtonText => UseTransfer
            ? "Transfer Build"
            : UseLocalFiles
            ? $"Extract Local Files ({LocalFiles.Count})"
            : (ExtractAfterDownload ? "Download & New Build"
               : PatchAfterDownload ? "Download & Patch"
               : "Download") + (IsBatchMode ? $" ({_batch.Count} folders)" : "");

        public bool CanDownload => !IsBusy && (UseTransfer
            ? CanTransfer
            : SelectedTarget?.HasBuildPath == true
              && (UseLocalFiles
                  ? LocalFiles.Count > 0
                  : IsBatchMode || SelectedNode?.CanDownload == true));

        // ---------- Local Files (extract archives already on this PC) ----------

        /// <summary>
        /// "Local Files" option: skip Google Drive and apply .zip/.7z files picked from this PC, in the
        /// order they're listed. While on, the New Build / Patch download options collapse (their
        /// values are kept, not cleared) so the file list has room. Transient - not saved.
        /// </summary>
        public bool UseLocalFiles
        {
            get => _useLocalFiles;
            set
            {
                if (!SetProperty(ref _useLocalFiles, value)) return;
                if (value && _localTransferMode) RebuildTransferOptions();
                RaiseLocalSubModeChanged();
                OnPropertyChanged(nameof(IsNotLocalMode));
                OnPropertyChanged(nameof(DownloadButtonText));
                OnPropertyChanged(nameof(CanDownload));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        // True only in plain Drive-download mode - hides New Build/Patch while Local Files is on
        // (their values are kept).
        public bool IsNotLocalMode => !UseLocalFiles;

        // ---------- Transfer Build (move one build tab's content into another) ----------

        /// <summary>
        /// Local Files has two sub-modes, picked by the radio pair inside its panel:
        /// "Extract local archives" (the original Local Files behavior) and "Transfer existing build
        /// content" - move EVERYTHING from one build tab (Source) into another (Target): build files,
        /// Documents, Version Number - using the same logic as a tab-sourced Pushed to Live
        /// (BuildSectionViewModel.TransferFromTabAsync). The Target's build folder is cleared first;
        /// the Source is left empty with its Version Number cleared and its "Last version moved to
        /// ..." note set. The sub-mode is remembered while Local Files is toggled off/on. Transient.
        /// </summary>
        public bool IsLocalTransferMode
        {
            get => _localTransferMode;
            set
            {
                if (!SetProperty(ref _localTransferMode, value)) return;
                if (value) RebuildTransferOptions();
                RaiseLocalSubModeChanged();
            }
        }

        public bool IsLocalExtractMode
        {
            get => !_localTransferMode;
            set { if (value) IsLocalTransferMode = false; }
        }

        // Transfer Build is active: Local Files on AND the Transfer sub-mode picked.
        public bool UseTransfer => UseLocalFiles && _localTransferMode;
        public bool IsNotTransferMode => !UseTransfer;
        // The archive-extract panel (Add Files / list) - Local Files on with Extract picked.
        public bool ShowLocalExtractPanel => UseLocalFiles && !_localTransferMode;

        private void RaiseLocalSubModeChanged()
        {
            OnPropertyChanged(nameof(IsLocalTransferMode));
            OnPropertyChanged(nameof(IsLocalExtractMode));
            OnPropertyChanged(nameof(UseTransfer));
            OnPropertyChanged(nameof(IsNotTransferMode));
            OnPropertyChanged(nameof(ShowLocalExtractPanel));
            OnPropertyChanged(nameof(DownloadButtonText));
            OnPropertyChanged(nameof(CanDownload));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        // Each list holds every build tab except whatever the OTHER dropdown has selected, so the
        // same tab can never be picked on both sides.
        public ObservableCollection<BuildRepoTargetOption> TransferSourceOptions { get; } = new();
        public ObservableCollection<BuildRepoTargetOption> TransferTargetOptions { get; } = new();

        public BuildRepoTargetOption? TransferSource
        {
            get => _transferSource;
            set
            {
                if (!SetProperty(ref _transferSource, value)) return;
                SyncTransferList(TransferTargetOptions, exclude: value);
                OnTransferSelectionChanged();
            }
        }

        public BuildRepoTargetOption? TransferTarget
        {
            get => _transferTarget;
            set
            {
                if (!SetProperty(ref _transferTarget, value)) return;
                SyncTransferList(TransferSourceOptions, exclude: value);
                OnTransferSelectionChanged();
            }
        }

        public bool CanTransfer => TransferSource != null && TransferTarget != null
                                   && !ReferenceEquals(TransferSource, TransferTarget)
                                   && TransferSource.BuildFolderExists && TransferTarget.HasBuildPath;

        public string TransferSourcePathText => TransferSource == null
            ? "Pick the tab to move the build out of."
            : !TransferSource.HasBuildPath
                ? "This tab has no Current Build directory set - set one in Settings first."
                : !TransferSource.BuildFolderExists
                    ? $"Folder not found: {TransferSource.BuildPath}"
                    : TransferSource.BuildPath;

        public string TransferTargetPathText => TransferTarget == null
            ? "Pick the tab to move the build into."
            : TransferTarget.HasBuildPath
                ? TransferTarget.BuildPath
                : "This tab has no Current Build directory set - set one in Settings first.";

        public bool TransferSourceHasProblem => TransferSource != null && !TransferSource.BuildFolderExists;
        public bool TransferTargetHasProblem => TransferTarget != null && !TransferTarget.HasBuildPath;

        public string TransferSummary => TransferSource == null || TransferTarget == null
            ? "Pick a Source and a Target tab."
            : $"{TransferTarget.Title}'s build folder is cleared, then everything in {TransferSource.Title} (build files, Documents) " +
              $"is moved into it. {TransferTarget.Title}'s Version Number becomes {TransferSource.VersionText}; " +
              $"{TransferSource.Title} is left empty and shows \"Last version moved to {TransferTarget.Title}\".";

        public RelayCommand OpenTransferSourceFolderCommand { get; }
        public RelayCommand OpenTransferTargetFolderCommand { get; }

        private void OnTransferSelectionChanged()
        {
            OnPropertyChanged(nameof(CanTransfer));
            OnPropertyChanged(nameof(TransferSourcePathText));
            OnPropertyChanged(nameof(TransferTargetPathText));
            OnPropertyChanged(nameof(TransferSourceHasProblem));
            OnPropertyChanged(nameof(TransferTargetHasProblem));
            OnPropertyChanged(nameof(TransferSummary));
            OnPropertyChanged(nameof(CanDownload));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        // Fresh options from the current tabs (versions/paths re-read), keeping each side's pick by Id.
        private void RebuildTransferOptions()
        {
            var sourceId = _transferSource?.Id;
            var targetId = _transferTarget?.Id;

            var all = _tabsProvider()
                .Where(t => t.Kind == TabKind.BuildSection)
                .OrderBy(t => t.Order)
                .Select(t => new BuildRepoTargetOption(t))
                .ToList();

            var source = all.FirstOrDefault(o => o.Id == sourceId);
            var target = all.FirstOrDefault(o => o.Id == targetId);
            if (source != null && target != null && source.Id == target.Id) target = null;

            // Assign through the fields, then fill both lists, then announce - avoids the ComboBoxes
            // seeing their SelectedItem missing from ItemsSource mid-rebuild.
            _transferSource = null;
            _transferTarget = null;
            OnPropertyChanged(nameof(TransferSource));
            OnPropertyChanged(nameof(TransferTarget));

            TransferSourceOptions.Clear();
            TransferTargetOptions.Clear();
            foreach (var o in all)
            {
                if (target == null || o.Id != target.Id) TransferSourceOptions.Add(o);
                if (source == null || o.Id != source.Id) TransferTargetOptions.Add(o);
            }

            _transferSource = source;
            _transferTarget = target;
            OnPropertyChanged(nameof(TransferSource));
            OnPropertyChanged(nameof(TransferTarget));
            OnTransferSelectionChanged();
        }

        // Makes `list` = every option except `exclude`, in tab order, touching only the one item that
        // has to come out or go back in (so the other ComboBox's current selection is never disturbed).
        private void SyncTransferList(ObservableCollection<BuildRepoTargetOption> list, BuildRepoTargetOption? exclude)
        {
            var all = TransferSourceOptions.Concat(TransferTargetOptions)
                .GroupBy(o => o.Id).Select(g => g.First())
                .OrderBy(o => o.Settings.Order)
                .ToList();

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (exclude != null && list[i].Id == exclude.Id) list.RemoveAt(i);
            }

            int index = 0;
            foreach (var o in all)
            {
                if (exclude != null && o.Id == exclude.Id) continue;
                if (index >= list.Count || list[index].Id != o.Id)
                {
                    if (!list.Any(x => x.Id == o.Id)) list.Insert(index, o);
                }
                index++;
            }
        }

        /// <summary>
        /// When on, the FIRST file in the list runs as New Build (the build folder is wiped first) and
        /// every file after it as Patch. When off, every file is a Patch on top of the current build.
        /// </summary>
        public bool LocalNewBuildFirst
        {
            get => _localNewBuildFirst;
            set
            {
                if (SetProperty(ref _localNewBuildFirst, value)) RenumberLocalFiles();
            }
        }

        public ObservableCollection<LocalExtractFileViewModel> LocalFiles { get; } = new();

        /// <summary>
        /// Optional Version Number for a local run (shown once a file is listed). Stamped onto the
        /// target tab when the extract succeeds; blank = leave the tab's current Version Number alone.
        /// </summary>
        public string LocalVersionNumber
        {
            get => _localVersionNumber;
            set => SetProperty(ref _localVersionNumber, value ?? string.Empty);
        }

        public bool HasLocalFiles => LocalFiles.Count > 0;

        public string LocalFilesSummary => LocalFiles.Count == 0
            ? "No files added yet - click Add Files... (you can pick several at once)."
            : $"{LocalFiles.Count} file(s), {FormatBytes(LocalFiles.Sum(f => f.SizeBytes))} - applied top to bottom. Drag rows (or use ▲/▼) to change the order.";

        public RelayCommand AddLocalFilesCommand { get; }
        public RelayCommand ClearLocalFilesCommand { get; }
        public RelayCommand RemoveLocalFileCommand { get; }

        private void AddLocalFiles()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select build/patch archives to extract",
                Filter = "Build/Patch Archives (*.zip;*.7z)|*.zip;*.7z|Zip Archives (*.zip)|*.zip|7-Zip Archives (*.7z)|*.7z",
                CheckFileExists = true,
                Multiselect = true
            };
            if (dialog.ShowDialog() != true) return;

            // Natural sort the picked batch ("Partial 2" before "Partial 10") and put any Full_* first,
            // then append after whatever's already listed. Duplicates are skipped.
            var picked = dialog.FileNames
                .Where(f => LocalFiles.All(x => !string.Equals(x.FullPath, f, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => BuildRepoDefaults.IsFullArchive(f) ? 0 : 1)
                .ThenBy(f => Path.GetFileName(f), NaturalStringComparer.Instance)
                .ToList();

            // First add into an empty list that starts with a Full_* archive: default to New Build for it.
            if (LocalFiles.Count == 0 && picked.Count > 0 && BuildRepoDefaults.IsFullArchive(picked[0]))
            {
                _localNewBuildFirst = true;
                OnPropertyChanged(nameof(LocalNewBuildFirst));
            }

            foreach (var f in picked) LocalFiles.Add(new LocalExtractFileViewModel(f));
        }

        /// <summary>Moves a row - drag-and-drop in BuildRepoView lands here.</summary>
        public void MoveLocalFile(int from, int to)
        {
            if (IsBusy || from < 0 || from >= LocalFiles.Count) return;
            to = Math.Clamp(to, 0, LocalFiles.Count - 1);
            if (from == to) return;
            LocalFiles.Move(from, to);
        }

        private void OnLocalFilesChanged()
        {
            RenumberLocalFiles();
            OnPropertyChanged(nameof(HasLocalFiles));
            OnPropertyChanged(nameof(LocalFilesSummary));
            OnPropertyChanged(nameof(DownloadButtonText));
            OnPropertyChanged(nameof(CanDownload));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        private void RenumberLocalFiles()
        {
            for (int i = 0; i < LocalFiles.Count; i++)
            {
                LocalFiles[i].Position = i + 1;
                LocalFiles[i].ModeText = i == 0 && LocalNewBuildFirst ? "New Build" : "Patch";
            }
        }

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

        /// <summary>
        /// True while a download dropped mid-file and WebView2 kept the partial bytes - the Resume
        /// button shows, and Cancel becomes "Discard" (throws the partial away and resets).
        /// </summary>
        public bool IsAwaitingResume
        {
            get => _isAwaitingResume;
            private set
            {
                if (SetProperty(ref _isAwaitingResume, value))
                {
                    OnPropertyChanged(nameof(CancelButtonText));
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string CancelButtonText => IsAwaitingResume ? "Discard" : "Cancel";

        public AsyncRelayCommand RefreshCommand { get; }
        public AsyncRelayCommand SignInCommand { get; }
        public AsyncRelayCommand DownloadCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand ResumeCommand { get; }
        public RelayCommand OpenBuildFolderCommand { get; }
        public RelayCommand ClearBatchCommand { get; }

        // ---------- Activation ----------

        /// <summary>Called from the View's Loaded event every time the tab is shown.</summary>
        public void OnActivated()
        {
            RefreshTargets();
            RebuildTransferOptions();

            if (!_initialized)
            {
                _initialized = true;
                _root.IsExpanded = true; // kicks off the first listing
            }
        }

        private void RefreshTargets()
        {
            var previous = SelectedTarget?.Id ?? _appSettings.BuildRepoLastTargetTabId;

            // Every build tab, visible or hidden - the build tabs can be hidden in Settings and
            // driven from the Launcher + this tab instead, so visibility doesn't matter here. Runs
            // on every OnActivated, so a tab created in Settings shows up next time this tab opens.
            Targets.Clear();
            foreach (var tab in _tabsProvider()
                         .Where(t => t.Kind == TabKind.BuildSection)
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
                string.IsNullOrEmpty(rootId) ? null : rootId, null, LoadChildrenAsync, OnNodeSelected, OnBatchToggled);
        }

        private void OnNodeSelected(BuildRepoNode node) => SelectedNode = node;

        private BuildRepoNode Message(string text, BuildRepoNode parent) =>
            new(BuildRepoNodeKind.Message, text, null, parent);

        private async Task RefreshTreeAsync()
        {
            _browseCts.Cancel();
            _browseCts = new CancellationTokenSource();

            _listingCache.Clear();
            ClearBatch();
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
                            .Select(i => new BuildRepoNode(BuildRepoNodeKind.Version, i.Name, i.Id, node, LoadChildrenAsync, OnNodeSelected, OnBatchToggled))
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
                                i.Name, i.Id, node, LoadChildrenAsync, OnNodeSelected, OnBatchToggled))
                            .ToList();
                        if (children.Count == 0) children.Add(Message("The Build folder is empty.", node));
                        break;
                    }

                    default:
                    {
                        var items = await ListCachedAsync(node.DriveId, ct);
                        children = SortFoldersFirst(items)
                            .Select(i => new BuildRepoNode(i.IsFolder ? BuildRepoNodeKind.Folder : BuildRepoNodeKind.File,
                                i.Name, i.Id, node, LoadChildrenAsync, OnNodeSelected, OnBatchToggled))
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
            if (UseTransfer)
            {
                await TransferBuildAsync();
                return;
            }

            if (UseLocalFiles)
            {
                await ExtractLocalFilesAsync(LocalFiles.ToList());
                return;
            }

            if (IsBatchMode)
            {
                await DownloadBatchAsync(_batch.ToList());
                return;
            }

            var node = SelectedNode;
            var target = SelectedTarget;
            if (node == null || !node.CanDownload || node.DriveId == null || target == null || !target.HasBuildPath) return;

            var buildVm = _findBuildSection(target.Id);
            if (buildVm?.IsBusy == true)
            {
                ScrollableMessageBox.Show($"The {target.Title} tab is busy with another update right now. Try again once it finishes.",
                    "Build Repo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var patchOnly = PatchAfterDownload;           // "Patch" checkbox - never wipe the build
            var extract = ExtractAfterDownload || patchOnly;
            var buildPath = target.BuildPath;
            var externalUpdateStarted = false;
            var downloading = false; // true only while files are coming down (before any extract)
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
                    var answer = ScrollableMessageBox.Show(
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
                    confirmText = $"Download {files.Count} file(s) from {node.Name} for {target.Title}.\n\nExisting files with the same names will be overwritten.\n\nContinue?";
                }

                var versionLabel = VersionLabelFor(node);
                if (extract && versionLabel != null)
                {
                    confirmText = confirmText.Replace("\n\nContinue?",
                        $"\n\n{target.Title}'s Version Number will be set to {versionLabel} once it finishes.\n\nContinue?");
                }

                // Zips/7z ALWAYS land in BuildRepoDownloads; only extracted files go into the Current Build directory.
                stagingRoot = StagingFolderFor(target, node.Name);
                bool UsesStaging(string relative) => extract || BuildRepoDefaults.IsArchive(relative);
                confirmText = confirmText.Replace("\n\nContinue?",
                    "\n\n" + LocationsText(stagingRoot, buildPath, extract,
                        files.Any(f => UsesStaging(f.RelativePath)), files.Any(f => !UsesStaging(f.RelativePath))) + "\n\nContinue?");

                if (ScrollableMessageBox.Show(confirmText, extract && hasFull ? "Confirm Download & New Build" : patchOnly ? "Confirm Download & Patch" : "Confirm Download",
                        MessageBoxButton.YesNo, extract && hasFull ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    StatusText = "Download cancelled.";
                    return;
                }

                // 4. Download everything, one file at a time. Archives (and everything, in extract
                //    modes) go to BuildRepoDownloads; anything a previous failed run already finished
                //    there is verified and reused instead of being downloaded again.
                var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var plan = files.Select(f => (f.Item, f.RelativePath,
                        Destination: UniquePath(Path.Combine(UsesStaging(f.RelativePath) ? stagingRoot : buildPath, f.RelativePath), usedPaths),
                        Staged: UsesStaging(f.RelativePath)))
                    .ToList();
                PrepareStagingFolder(stagingRoot, plan.Where(p => p.Staged).Select(p => p.Destination));
                if (plan.Any(p => p.Staged)) Log($"Zip/7z downloads go to: {stagingRoot}");
                if (plan.Any(p => !p.Staged)) Log($"Other files go to: {buildPath}");

                var downloadBand = extract ? 60.0 : 100.0;
                var downloaded = new List<string>();
                IsProgressIndeterminate = false;
                downloading = true;

                for (int i = 0; i < plan.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (item, relative, destination, staged) = plan[i];
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (staged && TryReuseVerifiedDownload(destination, relative))
                    {
                        downloaded.Add(destination);
                        ProgressPercent = (i + 1.0) / plan.Count * downloadBand;
                        continue;
                    }
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

                    await DownloadFileWithResumeAsync(item.Id, destination, fileNumber, files.Count, progress, ct);
                    downloaded.Add(destination);
                    Log($"  ✓ {relative}  ({FormatBytes(new FileInfo(destination).Length)})");
                }

                downloading = false;
                ProgressPercent = downloadBand;
                NeedsSignIn = false;

                if (!extract)
                {
                    StatusText = plan.Any(p => p.Staged)
                        ? $"Downloaded {downloaded.Count} file(s) from {node.Name}. Zip/7z files are in {stagingRoot} (not extracted)."
                        : $"Downloaded {downloaded.Count} file(s) from {node.Name} into {target.Title}'s build folder.";
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
                if (downloading) KeepCompletedDownloads(stagingRoot);
                NeedsSignIn = true;
                StatusText = "Google sign-in required - sign in, then try the download again.";
                Log("Stopped: Google sign-in required.");
            }
            catch (OperationCanceledException)
            {
                if (downloading)
                {
                    ResetAfterFailedDownload(stagingRoot);
                    StatusText = $"Download cancelled - partial files removed. Click {DownloadButtonText} to start over.";
                }
                else
                {
                    StatusText = "Download cancelled.";
                }
                Log("Cancelled.");
            }
            catch (Exception ex)
            {
                Log($"Failed: {ex.Message}");
                if (downloading)
                {
                    // Couldn't resume (or something else broke mid-download): the half-written file is
                    // gone, but every file that FINISHED stays in BuildRepoDownloads. The next click
                    // verifies those, skips them, and carries on from the file that failed.
                    KeepCompletedDownloads(stagingRoot);
                    StatusText = $"Failed: {ex.Message} Completed files kept - click {DownloadButtonText} to verify them and continue.";
                    ScrollableMessageBox.Show(DownloadFailedText(ex.Message, stagingRoot), "Build Repo", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    StatusText = $"Failed: {ex.Message}";
                    if (stagingRoot != null && Directory.Exists(stagingRoot))
                    {
                        Log($"Downloaded files are still in: {stagingRoot}");
                    }
                    ScrollableMessageBox.Show(ex.Message, "Build Repo", MessageBoxButton.OK, MessageBoxImage.Error);
                }
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

        // Transfer Build: move one build tab's whole content into another, exactly like a tab-sourced
        // Pushed to Live (BuildSectionViewModel.TransferFromTabAsync does the work on the Target's
        // own view model, so Documents/Version/Executables/"Last version moved to" all behave the same).
        private async Task TransferBuildAsync()
        {
            var source = TransferSource;
            var target = TransferTarget;
            if (source == null || target == null || !CanTransfer) return;

            var targetVm = _findBuildSection(target.Id);
            var sourceVm = _findBuildSection(source.Id);
            if (targetVm == null)
            {
                ScrollableMessageBox.Show($"Couldn't find the {target.Title} tab. Reopen this tab and try again.",
                    "Build Repo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (targetVm.IsBusy || sourceVm?.IsBusy == true)
            {
                var busy = targetVm.IsBusy ? target.Title : source.Title;
                ScrollableMessageBox.Show($"The {busy} tab is busy with another update right now. Try again once it finishes.",
                    "Build Repo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = ScrollableMessageBox.Show(
                $"This will move everything from:\n{source.Title} ({source.BuildPath})\n\ninto:\n{target.Title} ({target.BuildPath})\n\n" +
                $"{target.Title}'s build folder is CLEARED first, then the build files and Documents are moved over.\n" +
                $"{target.Title}'s Version Number will be set to {source.VersionText}.\n" +
                $"{source.Title} will be left empty (Version Number cleared, last version shown as {source.VersionText}).\n\n" +
                "This can't be undone. Continue?",
                "Confirm Transfer Build", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                StatusText = "Transfer cancelled.";
                return;
            }

            var started = false;
            var ct = Begin($"Transferring {source.Title} -> {target.Title}...", indeterminate: false);
            ClearLog();
            Log($"Transfer Build: {source.Title} ({source.BuildPath}) -> {target.Title} ({target.BuildPath})");
            Log($"Version carried over: {source.VersionText}");

            try
            {
                var startText = $"Build Repo: transferring in from {source.Title}...";
                targetVm.BeginExternalUpdate(startText);
                sourceVm?.BeginExternalUpdate($"Build Repo: transferring out to {target.Title}...");
                started = true;

                var progress = new Progress<UpdateProgress>(p =>
                {
                    ProgressPercent = p.PercentComplete;
                    StatusText = p.Status;
                    targetVm.ReportExternalProgress(p.PercentComplete, p.Status);
                    sourceVm?.ReportExternalProgress(p.PercentComplete, p.Status);
                });

                var note = await targetVm.TransferFromTabAsync(source.Settings, sourceVm, progress, ct);

                ProgressPercent = 100;
                var doneText = $"{source.Title} transferred to {target.Title} ({target.Settings.VersionNumber}).{note}";
                StatusText = doneText;
                Log("Done." + note);
                targetVm.EndExternalUpdate(doneText, succeeded: true);
                sourceVm?.EndExternalUpdate($"Build moved to {target.Title}.", succeeded: true);
                started = false;
            }
            catch (OperationCanceledException)
            {
                StatusText = "Transfer cancelled - some files may already have moved. Check both build folders.";
                Log("Cancelled.");
            }
            catch (Exception ex)
            {
                Log($"Failed: {ex.Message}");
                StatusText = $"Transfer failed: {ex.Message}";
                ScrollableMessageBox.Show(ex.Message, "Transfer Build Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (started)
                {
                    targetVm.EndExternalUpdate(StatusText, succeeded: false);
                    sourceVm?.EndExternalUpdate(StatusText, succeeded: false);
                }
                End();
                // Re-read versions/paths so the dropdown labels show the new state.
                RebuildTransferOptions();
            }
        }

        // Local Files: no Drive at all - each listed .zip/.7z runs through BuildUpdateService.RunAsync
        // straight from where it sits on disk, in list order. The first one is a New Build when
        // "Start with a New Build" is ticked; every other file is a Patch onto the same build folder
        // (same chaining as the build tabs' "additional files to apply" list). A file that's a packaged
        // build folder (Full_*.zip / Full / Partial folders inside) is distributed piece by piece like
        // a Drive download instead - see ApplyBuildLayoutArchiveAsync. Source files are never moved
        // or deleted.
        private async Task ExtractLocalFilesAsync(List<LocalExtractFileViewModel> files)
        {
            var target = SelectedTarget;
            if (files.Count == 0 || target == null || !target.HasBuildPath) return;

            var missing = files.Where(f => !File.Exists(f.FullPath)).ToList();
            if (missing.Count > 0)
            {
                ScrollableMessageBox.Show("These files can't be found anymore - remove them from the list and try again:\n\n" +
                    string.Join("\n", missing.Select(f => f.FullPath)), "Build Repo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var buildVm = _findBuildSection(target.Id);
            if (buildVm?.IsBusy == true)
            {
                ScrollableMessageBox.Show($"The {target.Title} tab is busy with another update right now. Try again once it finishes.",
                    "Build Repo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var buildPath = target.BuildPath;
            var newBuildFirst = LocalNewBuildFirst;
            var plan = files.Select((f, i) => (f.FullPath, Mode: i == 0 && newBuildFirst ? UpdateMode.NewBuild : UpdateMode.Patch)).ToList();

            var lines = string.Join("\n", plan.Select((p, i) =>
                $"{i + 1}. {(p.Mode == UpdateMode.NewBuild ? "New Build" : "Patch")}: {Path.GetFileName(p.FullPath)}"));
            var confirmText = newBuildFirst
                ? $"Extract {plan.Count} local file(s) and REPLACE the existing {target.Title} build at:\n{buildPath}\n\n{lines}\n\n" +
                  "The build folder is DELETED before file 1 is extracted. Everything after it is applied on top like Patch.\n\nContinue?"
                : $"Extract {plan.Count} local file(s) as a PATCH on top of the existing {target.Title} build at:\n{buildPath}\n\n{lines}\n\n" +
                  "Existing files are kept - only files with the same names are overwritten.\n\nContinue?";

            var versionLabel = string.IsNullOrWhiteSpace(LocalVersionNumber) ? null : LocalVersionNumber.Trim();
            if (versionLabel != null)
            {
                confirmText = confirmText.Replace("\n\nContinue?",
                    $"\n\n{target.Title}'s Version Number will be set to {versionLabel} once it finishes.\n\nContinue?");
            }

            if (ScrollableMessageBox.Show(confirmText, newBuildFirst ? "Confirm Local New Build" : "Confirm Local Patch",
                    MessageBoxButton.YesNo, newBuildFirst ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                StatusText = "Extract cancelled.";
                return;
            }

            var externalUpdateStarted = false;
            var ct = Begin($"Extracting {plan.Count} local file(s)...", indeterminate: false);
            ClearLog();
            Log($"Local files ({plan.Count}), applied in this order:");
            foreach (var (path, mode) in plan) Log($"  {(mode == UpdateMode.NewBuild ? "New Build" : "Patch")}: {path}");
            Log($"Target tab: {target.Title}  ({buildPath})");

            try
            {
                buildVm?.BeginExternalUpdate($"Build Repo: extracting {plan.Count} local file(s)...");
                externalUpdateStarted = true;

                for (int i = 0; i < plan.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (path, mode) = plan[i];
                    int index = i;
                    int fileNumber = i + 1;
                    var label = $"{(mode == UpdateMode.NewBuild ? "New Build" : "Patch")}: {Path.GetFileName(path)}";
                    Log($"File {fileNumber} of {plan.Count} - {label}");

                    // start/span = this piece of work's share (0..1) of this file's slice of the bar.
                    IProgress<UpdateProgress> SliceProgress(string stepLabel, double start, double span) =>
                        new Progress<UpdateProgress>(p =>
                        {
                            var pct = (index + start + span * p.PercentComplete / 100.0) / plan.Count * 100.0;
                            var text = $"File {fileNumber} of {plan.Count} ({stepLabel}): {p.Status}";
                            ProgressPercent = pct;
                            StatusText = text;
                            buildVm?.ReportExternalProgress(pct, text);
                        });

                    if (!await BuildUpdateService.IsBuildLayoutArchiveAsync(path, ct))
                    {
                        // Plain client payload (Full_Client.zip, Partial_Client.zip, a loose patch) - unchanged.
                        await BuildUpdateService.RunAsync(path, buildPath, mode, SliceProgress(label, 0, 1), ct);
                    }
                    else
                    {
                        await ApplyBuildLayoutArchiveAsync(path, target, buildPath, mode, label, SliceProgress, ct);
                    }
                    Log("  ✓ done");
                }

                RescanExecutables(target.Settings);
                if (versionLabel != null)
                {
                    target.Settings.VersionNumber = versionLabel;
                    Log($"Set {target.Title}'s Version Number to {versionLabel}.");
                }
                TrySaveSettings();

                ProgressPercent = 100;
                var doneText = $"{plan.Count} local file(s) {(newBuildFirst ? "extracted into" : "patched into")} {target.Title}. Ready to launch.";
                StatusText = doneText;
                Log("Done. Source files were left where they are.");
                buildVm?.EndExternalUpdate(doneText, succeeded: true);
                externalUpdateStarted = false;
            }
            catch (OperationCanceledException)
            {
                StatusText = "Extract cancelled.";
                Log("Cancelled.");
            }
            catch (Exception ex)
            {
                Log($"Failed: {ex.Message}");
                StatusText = $"Failed: {ex.Message}";
                ScrollableMessageBox.Show(ex.Message, "Build Repo", MessageBoxButton.OK, MessageBoxImage.Error);
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

        // A local archive that's a packaged build FOLDER (Full_*.zip and/or Full / Partial folders
        // inside - see BuildUpdateService.IsBuildLayoutArchiveAsync) is applied the same way the Drive
        // download of that folder would be, instead of being dumped into the build folder as-is:
        //   1. unpacked into BuildRepoDownloads staging (next to the build folder);
        //   2. Full_* archive(s) applied first - the first one as New Build when this file is tagged
        //      New Build (a lone archive inside a "Full" folder counts when nothing is named Full_*);
        //   3. each top-level Partial folder applied as its own Patch, in natural order
        //      ("Partial", "Partial 2", "Partial 3 10/28"...);
        //   4. anything else left (Partial_*.zip, a loose Partial Client folder...) patched last.
        // A New Build-tagged file with no Full archive still wipes the build folder first.
        // Staging is always deleted afterwards; the source archive is never touched.
        private async Task ApplyBuildLayoutArchiveAsync(string archivePath, BuildRepoTargetOption target, string buildPath,
            UpdateMode mode, string label, Func<string, double, double, IProgress<UpdateProgress>> sliceProgress, CancellationToken ct)
        {
            const double unpackShare = 0.3;
            var staging = StagingFolderFor(target, $"Local {Path.GetFileNameWithoutExtension(archivePath)}");
            TryDeleteDirectory(staging);

            try
            {
                Log($"  Packaged build folder - unpacking to {staging} and applying Full / Partial pieces like a Drive download.");
                await BuildUpdateService.ExtractToFolderAsync(archivePath, staging, sliceProgress($"{label} - unpacking", 0, unpackShare), ct);

                // Drop single-folder wrappers ("v271.4.0\" -> its contents).
                var root = staging;
                while (Directory.GetFiles(root).Length == 0 && Directory.GetDirectories(root) is { Length: 1 } only
                       && !FullFolderRegex.IsMatch(Path.GetFileName(only[0])) && !PartialFolderRegex.IsMatch(Path.GetFileName(only[0])))
                {
                    root = only[0];
                }

                var fulls = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(BuildRepoDefaults.IsFullArchive)
                    .OrderBy(f => Path.GetRelativePath(root, f), NaturalStringComparer.Instance)
                    .ToList();
                if (fulls.Count == 0)
                {
                    foreach (var fullDir in Directory.GetDirectories(root)
                                 .Where(d => FullFolderRegex.IsMatch(Path.GetFileName(d)))
                                 .OrderBy(d => Path.GetFileName(d), NaturalStringComparer.Instance))
                    {
                        var archives = Directory.EnumerateFiles(fullDir, "*", SearchOption.AllDirectories)
                            .Where(BuildRepoDefaults.IsArchive).ToList();
                        if (archives.Count == 1) fulls.Add(archives[0]);
                    }
                }

                var steps = new List<(string Label, Func<IProgress<UpdateProgress>, Task> Run)>();

                if (mode == UpdateMode.NewBuild && fulls.Count == 0)
                {
                    steps.Add(("New Build: clear build folder", _ => BuildUpdateService.ClearBuildFolderAsync(buildPath, ct)));
                }

                for (int k = 0; k < fulls.Count; k++)
                {
                    var full = fulls[k];
                    var fullMode = k == 0 && mode == UpdateMode.NewBuild ? UpdateMode.NewBuild : UpdateMode.Patch;
                    steps.Add(($"{(fullMode == UpdateMode.NewBuild ? "New Build" : "Patch")}: {Path.GetRelativePath(root, full)}", async p =>
                    {
                        await BuildUpdateService.RunAsync(full, buildPath, fullMode, p, ct);
                        RemoveAppliedArchive(full, root); // so the Patch steps below don't see it again
                    }));
                }

                foreach (var partialDir in Directory.GetDirectories(root)
                             .Where(d => PartialFolderRegex.IsMatch(Path.GetFileName(d)))
                             .OrderBy(d => Path.GetFileName(d), NaturalStringComparer.Instance))
                {
                    var partName = Path.GetFileName(partialDir);
                    steps.Add(($"Patch: {partName}", async p =>
                    {
                        if (BuildUpdateService.HasPatchContent(partialDir))
                            await BuildUpdateService.RunPatchFromFolderAsync(partialDir, buildPath, p, ct);
                        else
                            Log($"    {partName}: nothing to patch (only checksum files) - skipped.");
                    }));
                }

                steps.Add(("Patch: remaining build files", async p =>
                {
                    if (BuildUpdateService.HasPatchContent(root))
                        await BuildUpdateService.RunPatchFromFolderAsync(root, buildPath, p, ct);
                    else
                        Log("    Nothing else left to patch - skipped.");
                }));

                var stepShare = (1 - unpackShare) / steps.Count;
                for (int s = 0; s < steps.Count; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (stepLabel, run) = steps[s];
                    Log($"    Step {s + 1} of {steps.Count} - {stepLabel}");
                    await run(sliceProgress($"{label} - {stepLabel}", unpackShare + s * stepShare, stepShare));
                }
            }
            finally
            {
                TryDeleteDirectory(staging);
                CleanupEmptyStagingParents(staging);
            }
        }

        // Same rules as BuildRepoNode's Full/Partial folder tags.
        private static readonly System.Text.RegularExpressions.Regex FullFolderRegex =
            new(@"^\s*Full(?![A-Za-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static readonly System.Text.RegularExpressions.Regex PartialFolderRegex =
            new(@"^\s*Partial(?![A-Za-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Batch: several Full/Partial folders of ONE build (ticked in the tree), Full first. Everything is downloaded
        // first - each Partial into its own numbered staging subfolder so their contents never mix -
        // then each one is applied as its own Patch, in tree order ("Partial" → "Partial 1 10/28" →
        // "Partial 2 10/28"), so a later Partial overwrites an earlier one exactly as if they'd been
        // run one after another by hand. Download-only mode writes them straight into the build
        // folder in the same order.
        private async Task DownloadBatchAsync(List<BuildRepoNode> nodes)
        {
            var target = SelectedTarget;
            if (nodes.Count == 0 || target == null || !target.HasBuildPath) return;

            var build = nodes[0].BuildAncestor;
            if (build == null || nodes.Any(n => n.DriveId == null || !ReferenceEquals(n.BuildAncestor, build)))
            {
                ScrollableMessageBox.Show("Batched folders all have to come from the same build.", "Build Repo",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var buildVm = _findBuildSection(target.Id);
            if (buildVm?.IsBusy == true)
            {
                ScrollableMessageBox.Show($"The {target.Title} tab is busy with another update right now. Try again once it finishes.",
                    "Build Repo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var patchOnly = PatchAfterDownload;
            var extract = ExtractAfterDownload || patchOnly;
            var buildPath = target.BuildPath;
            var batchLabel = $"{build.Name} ({string.Join(", ", nodes.Select(n => n.Name))})";
            var externalUpdateStarted = false;
            var downloading = false;
            string? stagingRoot = null;
            var ct = Begin($"Listing files in {nodes.Count} folders...", indeterminate: true);
            ClearLog();
            Log($"Batch from: {build.Breadcrumb}");
            for (int i = 0; i < nodes.Count; i++) Log($"  {i + 1}. {nodes[i].Name}");
            Log($"Target tab: {target.Title}  ({buildPath})");

            try
            {
                // 1. List every Partial's files.
                var groups = new List<(BuildRepoNode Node, List<(DriveItem Item, string RelativePath)> Files)>();
                foreach (var node in nodes)
                {
                    var files = new List<(DriveItem Item, string RelativePath)>();
                    await CollectFilesAsync(node.DriveId!, string.Empty, files, ct, depth: 0);
                    if (files.Count == 0)
                    {
                        Log($"{node.Name}: empty - skipped.");
                        continue;
                    }
                    Log($"{node.Name}: {files.Count} file(s).");
                    groups.Add((node, files));
                }

                var totalFiles = groups.Sum(g => g.Files.Count);
                if (totalFiles == 0)
                {
                    StatusText = "None of the selected folders have any files to download.";
                    return;
                }

                // 2. Nothing to extract? Offer a plain download instead (same as a single selection).
                var allRelative = groups.SelectMany(g => g.Files.Select(f => f.RelativePath)).ToList();
                if (extract && !allRelative.Any(BuildRepoDefaults.IsArchive))
                {
                    var answer = ScrollableMessageBox.Show(
                        $"None of the {totalFiles} file(s) in the selected folders are .zip/.7z archives, so there's nothing to extract.\n\nDownload them into {target.Title}'s build folder instead?",
                        "Build Repo", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer != MessageBoxResult.Yes)
                    {
                        StatusText = "Download cancelled.";
                        return;
                    }
                    extract = false;
                    patchOnly = false;
                }

                var hasFull = extract && !patchOnly && groups.Any(g =>
                    g.Files.Any(f => BuildRepoDefaults.IsFullArchive(f.RelativePath))
                    || (g.Node.IsFullFolder && g.Files.Count(f => BuildRepoDefaults.IsArchive(f.RelativePath)) == 1));

                // 3. Confirm, showing the order they'll be applied in.
                var orderText = string.Join("\n", groups.Select((g, i) => $"  {i + 1}. {g.Node.Name}  ({g.Files.Count} file(s))"));
                string confirmText;
                if (!extract)
                {
                    confirmText = $"Download {groups.Count} folders ({totalFiles} file(s)) from {build.Name} for {target.Title}, in this order:\n\n{orderText}\n\n" +
                                  "Later folders overwrite files with the same names from earlier ones.\n\nContinue?";
                }
                else if (hasFull)
                {
                    confirmText = $"Download {groups.Count} folders ({totalFiles} file(s)) from {build.Name}, then REPLACE the existing {target.Title} build at:\n{buildPath}\n\n" +
                                  "The Full archive is applied like New Build (the current build is wiped), then everything else is applied like Patch in this order:\n\n" +
                                  $"{orderText}\n\nContinue?";
                }
                else
                {
                    confirmText = $"Download {groups.Count} folders ({totalFiles} file(s)) from {build.Name} and PATCH the existing {target.Title} build at:\n{buildPath}\n\n" +
                                  $"Each folder is applied like the Patch button (Full included - overlaid, not wiped), one after another, in this order:\n\n{orderText}\n\n" +
                                  "The current build is kept - later folders overwrite files with the same names.\n\nContinue?";
                }

                var versionLabel = VersionLabelFor(groups[0].Node);
                if (extract && versionLabel != null)
                {
                    confirmText = confirmText.Replace("\n\nContinue?",
                        $"\n\n{target.Title}'s Version Number will be set to {versionLabel} once it finishes.\n\nContinue?");
                }

                // Zips/7z ALWAYS land in BuildRepoDownloads; only extracted files go into the Current Build directory.
                stagingRoot = StagingFolderFor(target, $"{build.Name} batch");
                bool UsesStaging(string relative) => extract || BuildRepoDefaults.IsArchive(relative);
                confirmText = confirmText.Replace("\n\nContinue?",
                    "\n\n" + LocationsText(stagingRoot, buildPath, extract,
                        allRelative.Any(UsesStaging), allRelative.Any(r => !UsesStaging(r))) + "\n\nContinue?");

                if (ScrollableMessageBox.Show(confirmText, hasFull ? "Confirm Batch Download & New Build" : extract ? "Confirm Batch Download & Patch" : "Confirm Batch Download",
                        MessageBoxButton.YesNo, hasFull ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    StatusText = "Download cancelled.";
                    return;
                }

                // 4. Download everything - each Partial into its own BuildRepoDownloads folder ("01 Partial",
                //    "02 Partial 1 10_28", ...). Anything a previous failed run already finished there is
                //    verified and reused instead of being downloaded again.
                var groupRoots = groups.Select((grp, g) =>
                    Path.Combine(stagingRoot, $"{g + 1:00} {BuildRepoDefaults.MakeSafeFileName(grp.Node.Name)}")).ToList();
                var groupPlans = new List<List<(DriveItem Item, string RelativePath, string Destination, bool Staged)>>();
                for (int g = 0; g < groups.Count; g++)
                {
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    groupPlans.Add(groups[g].Files.Select(f => UsesStaging(f.RelativePath)
                            ? (f.Item, f.RelativePath, UniquePath(Path.Combine(groupRoots[g], f.RelativePath), used), true)
                            : (f.Item, f.RelativePath, Path.Combine(buildPath, f.RelativePath), false)) // later folders overwrite earlier ones
                        .ToList());
                }
                PrepareStagingFolder(stagingRoot, groupPlans.SelectMany(p => p).Where(p => p.Staged).Select(p => p.Destination));
                if (allRelative.Any(UsesStaging)) Log($"Zip/7z downloads go to: {stagingRoot}");
                if (allRelative.Any(r => !UsesStaging(r))) Log($"Other files go to: {buildPath}");

                var downloadBand = extract ? 60.0 : 100.0;
                var groupDownloads = new List<List<string>>();
                IsProgressIndeterminate = false;
                downloading = true;
                int fileIndex = 0;

                for (int g = 0; g < groups.Count; g++)
                {
                    var node = groups[g].Node;
                    Log($"Downloading {node.Name}...");

                    var downloaded = new List<string>();
                    foreach (var (item, relative, destination, staged) in groupPlans[g])
                    {
                        ct.ThrowIfCancellationRequested();
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        if (staged && TryReuseVerifiedDownload(destination, $"{node.Name}{Path.DirectorySeparatorChar}{relative}"))
                        {
                            downloaded.Add(destination);
                            fileIndex++;
                            ProgressPercent = (double)fileIndex / totalFiles * downloadBand;
                            continue;
                        }
                        int index = fileIndex++;
                        int fileNumber = index + 1;
                        var name = Path.GetFileName(destination);
                        var partName = node.Name;

                        StatusText = $"Downloading {fileNumber} of {totalFiles} ({partName}): {name}...";
                        var progress = new Progress<DriveDownloadProgress>(p =>
                        {
                            double fraction = p.TotalBytes is > 0 ? (double)p.BytesReceived / p.TotalBytes.Value : 0;
                            ProgressPercent = (index + fraction) / totalFiles * downloadBand;
                            var sizeText = p.TotalBytes is > 0
                                ? $"{FormatBytes(p.BytesReceived)} of {FormatBytes(p.TotalBytes.Value)} ({fraction:P0})"
                                : FormatBytes(p.BytesReceived);
                            StatusText = $"Downloading {fileNumber} of {totalFiles} ({partName}): {name} - {sizeText}";
                        });

                        await DownloadFileWithResumeAsync(item.Id, destination, fileNumber, totalFiles, progress, ct);
                        downloaded.Add(destination);
                        Log($"  ✓ {node.Name}{Path.DirectorySeparatorChar}{relative}  ({FormatBytes(new FileInfo(destination).Length)})");
                    }
                    groupDownloads.Add(downloaded);
                }

                downloading = false;
                ProgressPercent = downloadBand;
                NeedsSignIn = false;

                if (!extract)
                {
                    StatusText = allRelative.Any(UsesStaging)
                        ? $"Downloaded {groups.Count} folders ({totalFiles} file(s)) from {build.Name}. Zip/7z files are in {stagingRoot} (not extracted)."
                        : $"Downloaded {groups.Count} folders ({totalFiles} file(s)) from {build.Name} into {target.Title}'s build folder.";
                    Log("Done.");
                    return;
                }

                // 5. Apply. Optional New Build first (first Full_* across the batch), then one Patch per Partial, in order.
                Log(patchOnly ? "Mode: Patch, one Partial at a time (existing build is kept)." : "Mode: New Build + Patches.");
                buildVm?.BeginExternalUpdate($"Build Repo: {(patchOnly ? "patching" : "extracting")} {batchLabel}...");
                externalUpdateStarted = true;

                var steps = new List<(string Label, Func<IProgress<UpdateProgress>, Task> Run)>();
                var newBuildQueued = false;

                for (int g = 0; g < groups.Count; g++)
                {
                    var groupRoot = groupRoots[g];
                    var partName = groups[g].Node.Name;
                    var fulls = groupDownloads[g].Where(BuildRepoDefaults.IsFullArchive)
                        .OrderBy(f => Path.GetRelativePath(groupRoot, f), NaturalStringComparer.Instance)
                        .ToList();

                    // A ticked Full folder whose archive isn't named Full_* (e.g. "Client.zip") - if it
                    // holds exactly one archive, that's the full client.
                    if (fulls.Count == 0 && groups[g].Node.IsFullFolder)
                    {
                        var archives = groupDownloads[g].Where(BuildRepoDefaults.IsArchive).ToList();
                        if (archives.Count == 1) fulls.Add(archives[0]);
                    }

                    foreach (var full in fulls)
                    {
                        var mode = !patchOnly && !newBuildQueued ? UpdateMode.NewBuild : UpdateMode.Patch;
                        var step = ($"{(mode == UpdateMode.NewBuild ? "New Build" : "Patch")}: {partName} / {Path.GetFileName(full)}",
                            (Func<IProgress<UpdateProgress>, Task>)(async p =>
                            {
                                await BuildUpdateService.RunAsync(full, buildPath, mode, p, ct);
                                RemoveAppliedArchive(full, groupRoot);
                            }));

                        if (mode == UpdateMode.NewBuild)
                        {
                            steps.Insert(0, step); // the wipe has to come before ANY patch
                            newBuildQueued = true;
                        }
                        else
                        {
                            steps.Add(step);
                        }
                    }

                    steps.Add(($"Patch: {partName}", async p =>
                    {
                        if (BuildUpdateService.HasPatchContent(groupRoot))
                        {
                            await BuildUpdateService.RunPatchFromFolderAsync(groupRoot, buildPath, p, ct);
                        }
                        else
                        {
                            Log($"  {partName}: nothing to patch (only checksum files) - skipped.");
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

                // 6. Same wrap-up as a single download.
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
                var doneText = $"{groups.Count} folders from {build.Name} {(patchOnly ? "patched into" : "extracted into")} {target.Title}. Ready to launch.";
                StatusText = doneText;
                Log("Done. Downloaded files removed from the staging folder.");
                buildVm?.EndExternalUpdate(doneText, succeeded: true);
                externalUpdateStarted = false;
                ClearBatch();
            }
            catch (SignInRequiredException)
            {
                if (downloading) KeepCompletedDownloads(stagingRoot);
                NeedsSignIn = true;
                StatusText = "Google sign-in required - sign in, then try the download again.";
                Log("Stopped: Google sign-in required.");
            }
            catch (OperationCanceledException)
            {
                if (downloading)
                {
                    ResetAfterFailedDownload(stagingRoot);
                    StatusText = $"Download cancelled - partial files removed. Click {DownloadButtonText} to start over.";
                }
                else
                {
                    StatusText = "Download cancelled.";
                }
                Log("Cancelled.");
            }
            catch (Exception ex)
            {
                Log($"Failed: {ex.Message}");
                if (downloading)
                {
                    KeepCompletedDownloads(stagingRoot);
                    StatusText = $"Failed: {ex.Message} Completed files kept - click {DownloadButtonText} to verify them and continue.";
                    ScrollableMessageBox.Show(DownloadFailedText(ex.Message, stagingRoot), "Build Repo", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    StatusText = $"Failed: {ex.Message}";
                    if (stagingRoot != null && Directory.Exists(stagingRoot))
                    {
                        Log($"Downloaded files are still in: {stagingRoot}");
                    }
                    ScrollableMessageBox.Show(ex.Message, "Build Repo", MessageBoxButton.OK, MessageBoxImage.Error);
                }
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

        // Downloads one file. If the connection drops and WebView2 kept the partial bytes, pauses
        // and waits for the user: Resume picks up from the bytes already on disk (as many times as
        // needed), Discard/Cancel throws them away. A drop that can't be resumed just throws - the
        // caller's catch wipes the partials and resets the UI.
        private async Task DownloadFileWithResumeAsync(string fileId, string destination, int fileNumber, int fileCount,
            IProgress<DriveDownloadProgress> progress, CancellationToken ct)
        {
            var name = Path.GetFileName(destination);
            ResumableDriveDownload? pending = null;
            string reason;
            var restartsLeft = 2; // fresh restarts allowed when the partial vanished from disk

            try
            {
            StartFresh:
                try
                {
                    await _session.DownloadLargeFileAsync(fileId, destination, progress, ct);
                    return;
                }
                catch (DriveDownloadInterruptedException ex) when (ex.Resumable != null)
                {
                    pending = ex.Resumable;
                    reason = ex.Reason;
                }

                while (true)
                {
                    var got = pending.TotalBytes is > 0
                        ? $"{FormatBytes(pending.BytesReceived)} of {FormatBytes(pending.TotalBytes.Value)}"
                        : FormatBytes(pending.BytesReceived);
                    Log($"  ! {name}: interrupted ({reason}) at {got} - partial kept, waiting for Resume or Discard.");
                    StatusText = $"Paused - {name} interrupted ({reason}) at {got}. Resume to continue from there, or Discard to start over.";

                    await WaitForResumeDecisionAsync(ct); // Discard/Cancel => OperationCanceledException

                    Log($"  Resuming {name} from {FormatBytes(pending.BytesReceived)}...");
                    StatusText = $"Resuming {fileNumber} of {fileCount}: {name}...";
                    try
                    {
                        await pending.ResumeAsync(progress, ct);
                        pending = null; // completed and moved into place
                        Log($"  Resumed {name} successfully.");
                        return;
                    }
                    catch (DriveDownloadInterruptedException ex) when (ex.Resumable != null)
                    {
                        pending = ex.Resumable; // dropped again - ask again
                        reason = ex.Reason;
                    }
                    catch (DriveDownloadInterruptedException ex)
                        when (ex.Reason == DriveDownloadInterruptedException.PartialFileMissingReason && restartsLeft > 0)
                    {
                        // The partial was deleted/cleaned up while paused - nothing to resume from, so
                        // start this file over on its own instead of failing the whole run.
                        restartsLeft--;
                        pending = null; // already discarded by ResumeAsync
                        Log($"  ! {name}: partial file is gone from disk - restarting this file from the beginning.");
                        StatusText = $"Restarting {fileNumber} of {fileCount}: {name} (partial file was missing)...";
                        goto StartFresh;
                    }
                }
            }
            finally
            {
                if (pending != null) await pending.DiscardAsync(); // no-op if it already closed itself
            }
        }

        private async Task WaitForResumeDecisionAsync(CancellationToken ct)
        {
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _resumeDecision = decision;
            IsAwaitingResume = true;
            try
            {
                using (ct.Register(() => decision.TrySetCanceled(ct)))
                {
                    await decision.Task;
                }
            }
            finally
            {
                _resumeDecision = null;
                IsAwaitingResume = false;
            }
        }

        // A download the user CANCELLED/discarded mid-way: delete its BuildRepoDownloads folder (it only
        // holds this run's downloads; non-archive files written straight into the build folder in
        // Download-only mode are complete and stay) and zero the bar. Errors use KeepCompletedDownloads instead.
        private void ResetAfterFailedDownload(string? stagingRoot)
        {
            if (stagingRoot != null && Directory.Exists(stagingRoot))
            {
                TryDeleteDirectory(stagingRoot);
                CleanupEmptyStagingParents(stagingRoot);
                Log($"Removed partial download folder: {stagingRoot}");
            }
            else
            {
                Log("Partial download removed.");
            }
            ProgressPercent = 0;
        }

        // A download that ERRORED (dropped connection that couldn't resume, sign-in expired...): keep every
        // file that finished. DownloadLargeFileAsync only moves a file into place once it's complete, so
        // what's left in BuildRepoDownloads is whole; the next click verifies it and continues.
        private void KeepCompletedDownloads(string? stagingRoot)
        {
            if (stagingRoot != null && Directory.Exists(stagingRoot))
            {
                var kept = Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories)
                    .Count(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
                Log($"Kept {kept} completed file(s) in: {stagingRoot}");
                Log($"Click {DownloadButtonText} again to verify them and continue from where it stopped.");
            }
            ProgressPercent = 0;
        }

        private string DownloadFailedText(string error, string? stagingRoot) =>
            $"{error}\n\nFiles that finished downloading were kept in:\n{stagingRoot}\n\n" +
            $"Click {DownloadButtonText} again - those files are verified and skipped, and the download continues from the file that failed.";

        // Confirmation-dialog lines showing where things land.
        private static string LocationsText(string stagingRoot, string buildPath, bool extract, bool anyStaged, bool anyDirect)
        {
            var sb = new StringBuilder();
            if (anyStaged)
            {
                sb.Append(extract ? "Zip/7z downloads land in:\n" : "Zip/7z downloads land in (kept there, NOT extracted - Extract after download is off):\n")
                  .Append(stagingRoot);
            }
            if (extract)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append("Extracted into the Current Build directory:\n").Append(buildPath);
            }
            else if (anyDirect)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append("Other (non-archive) files go to the Current Build directory:\n").Append(buildPath);
            }
            return sb.ToString();
        }

        // Before downloading into BuildRepoDownloads: throw away half-written .part files and anything
        // that isn't part of THIS download (so stray archives from another run can't be applied), but
        // keep every completed file that is - TryReuseVerifiedDownload checks each one before it's skipped.
        private void PrepareStagingFolder(string stagingRoot, IEnumerable<string> expectedPaths)
        {
            if (!Directory.Exists(stagingRoot)) return;
            var expected = new HashSet<string>(expectedPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            int removed = 0, existing = 0;

            foreach (var file in Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories).ToList())
            {
                if (!file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && expected.Contains(Path.GetFullPath(file)))
                {
                    existing++;
                    continue;
                }
                try { File.Delete(file); removed++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            foreach (var dir in Directory.EnumerateDirectories(stagingRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length).ToList())
            {
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            if (existing > 0) Log($"Found {existing} file(s) from an earlier attempt in {stagingRoot} - verifying before reuse.");
            if (removed > 0) Log($"Removed {removed} leftover/partial file(s) from {stagingRoot}.");
        }

        // True if destination already holds a complete download from an earlier attempt.
        private bool TryReuseVerifiedDownload(string destination, string label)
        {
            if (!File.Exists(destination)) return false;
            if (IsVerifiedDownload(destination, out var why))
            {
                Log($"  ✓ {label}  ({FormatBytes(new FileInfo(destination).Length)}) - already downloaded, verified, skipped");
                return true;
            }

            Log($"  ! {label}: existing copy failed verification ({why}) - downloading again.");
            try { File.Delete(destination); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return false;
        }

        // Cheap integrity check: non-empty, and for archives the structure has to be readable all the
        // way to the end (zip central directory / 7z header), which a truncated file fails.
        private static bool IsVerifiedDownload(string path, out string reason)
        {
            reason = string.Empty;
            try
            {
                var info = new FileInfo(path);
                if (info.Length == 0) { reason = "empty file"; return false; }

                var ext = Path.GetExtension(path);
                if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                    _ = zip.Entries.Count;
                    return true;
                }

                if (ext.Equals(".7z", StringComparison.OrdinalIgnoreCase))
                {
                    using var fs = File.OpenRead(path);
                    var header = new byte[32];
                    if (fs.Read(header, 0, 32) < 32) { reason = "truncated header"; return false; }
                    byte[] sig = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };
                    for (int i = 0; i < sig.Length; i++)
                    {
                        if (header[i] != sig[i]) { reason = "not a 7z file"; return false; }
                    }
                    var nextHeaderOffset = BitConverter.ToInt64(header, 12);
                    var nextHeaderSize = BitConverter.ToInt64(header, 20);
                    if (nextHeaderOffset < 0 || nextHeaderSize < 0 || 32 + nextHeaderOffset + nextHeaderSize > info.Length)
                    {
                        reason = "file is cut short";
                        return false;
                    }
                    return true;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
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

        // <parent of build folder>\BuildRepoDownloads\<tab title>\<selected node name>
        private static string StagingFolderFor(BuildRepoTargetOption target, string nodeName)
        {
            var trimmed = target.BuildPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(trimmed);
            var baseDir = string.IsNullOrEmpty(parent) ? Path.GetPathRoot(target.BuildPath)! : parent;

            // One-time move of the old "_BuildRepoDownloads" folder to the new name, so earlier downloads aren't orphaned.
            try
            {
                var legacy = Path.Combine(baseDir, BuildRepoDefaults.LegacyStagingFolderName);
                var current = Path.Combine(baseDir, BuildRepoDefaults.StagingFolderName);
                if (Directory.Exists(legacy) && !Directory.Exists(current)) Directory.Move(legacy, current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

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
