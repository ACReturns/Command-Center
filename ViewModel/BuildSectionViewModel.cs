using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommandCenter.Model;
using CommandCenter.Services;
using Microsoft.Win32;

namespace CommandCenter.ViewModel
{
    public enum SectionMode
    {
        NewBuild,
        Patch,
        PushedToLive,
        Launch
    }

    // Drives one tab's content (GMS / CMS / Live Service, or any extra tab). The same view is
    // reused for every BuildSection-kind tab; only the wrapped TabSettings instance and server
    // catalog differ. Every section launches by picking one of two fixed client executables plus
    // a server from that section's own fixed catalog (see LaunchServerCatalog).
    public class BuildSectionViewModel : ViewModelBase
    {
        private readonly SettingsService _settingsService;
        private readonly AppSettings _appSettings;
        private readonly TabSettings _settings;
        private readonly Dispatcher _uiDispatcher;

        private SectionMode _selectedMode = SectionMode.Launch;
        private string _sourceArchivePath = string.Empty;
        private string _pendingVersion = string.Empty;
        private string _pushSourceFolderPath = string.Empty;
        private bool _isBusy;
        private double _progressPercent;
        private string _statusText = string.Empty;
        private string? _selectedExecutable;
        private LaunchServerOption? _selectedServerOption;
        private CancellationTokenSource? _updateCancellation;
        private DispatcherTimer? _statusClearTimer;

        // Path of this section's Documents folder (a sibling of CurrentBuildPath - see
        // DocumentsService) and the watcher keeping its file list live. Null/None until
        // HasBuildPath is true - see SyncDocumentsFolder.
        private string? _documentsFolderPath;
        private FileSystemWatcher? _documentsWatcher;

        public BuildSectionViewModel(TabSettings settings, AppSettings appSettings, SettingsService settingsService, IReadOnlyList<LaunchServerOption> serverOptions, bool supportsPushedToLive)
        {
            _settings = settings;
            _appSettings = appSettings;
            _settingsService = settingsService;
            ServerOptions = serverOptions;
            SupportsPushedToLive = supportsPushedToLive;
            _uiDispatcher = Dispatcher.CurrentDispatcher;

            _settings.PropertyChanged += Settings_PropertyChanged;

            BrowseSourceCommand = new RelayCommand(_ => BrowseSource());
            RunUpdateCommand = new AsyncRelayCommand(_ => RunUpdateAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(SourceArchivePath) && HasBuildPath);
            CancelUpdateCommand = new RelayCommand(_ => _updateCancellation?.Cancel(), _ => IsBusy && _updateCancellation != null);
            LaunchCommand = new RelayCommand(_ => Launch(), _ => !IsBusy && HasBuildPath && SelectedExecutable != null && SelectedServerOption != null);

            BrowsePushSourceCommand = new RelayCommand(_ => BrowsePushSource());
            // Cancel is shared with RunUpdateCommand's token above - only one of these operations
            // can ever be in flight at a time per section, both gated by IsBusy.
            PushToLiveCommand = new AsyncRelayCommand(_ => PushToLiveAsync(), _ => !IsBusy && SupportsPushedToLive && !string.IsNullOrWhiteSpace(PushSourceFolderPath) && HasBuildPath);

            AddDocumentFilesCommand = new RelayCommand(_ => AddDocumentFiles(), _ => HasBuildPath);
            OpenDocumentCommand = new RelayCommand(param =>
            {
                if (param is DocumentEntry entry)
                {
                    OpenDocument(entry);
                }
            });
            Documents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDocuments));

            _selectedExecutable = ExecutableOptions.FirstOrDefault();
            _selectedServerOption = ServerOptions.FirstOrDefault();

            // Rehydrate the Documents folder (and start watching it) if this section already had
            // a build path configured from a previous session.
            SyncDocumentsFolder();
        }

        // Reads straight from the tab's settings, so renaming a tab in Settings (GMS/CMS/Live can
        // be renamed, same as any extra tab) is reflected here immediately once saved - see
        // Settings_PropertyChanged - including in the Documents folder's fallback name.
        public string SectionTitle => _settings.Title;

        public string CurrentBuildPath => _settings.BuildPath;
        public string VersionNumber => string.IsNullOrWhiteSpace(_settings.VersionNumber) ? "Not set" : _settings.VersionNumber;
        public bool HasBuildPath => !string.IsNullOrWhiteSpace(_settings.BuildPath) && Directory.Exists(_settings.BuildPath);

        public SectionMode SelectedMode
        {
            get => _selectedMode;
            set
            {
                if (SetProperty(ref _selectedMode, value))
                {
                    OnPropertyChanged(nameof(IsNewBuild));
                    OnPropertyChanged(nameof(IsPatch));
                    OnPropertyChanged(nameof(IsPushedToLive));
                    OnPropertyChanged(nameof(IsLaunch));
                    OnPropertyChanged(nameof(IsUpdatePanelVisible));
                    OnPropertyChanged(nameof(IsPushedToLivePanelVisible));
                    OnPropertyChanged(nameof(IsLaunchPanelVisible));
                    OnPropertyChanged(nameof(RunButtonLabel));
                }
            }
        }

        public bool IsNewBuild
        {
            get => _selectedMode == SectionMode.NewBuild;
            set { if (value) SelectedMode = SectionMode.NewBuild; }
        }

        public bool IsPatch
        {
            get => _selectedMode == SectionMode.Patch;
            set { if (value) SelectedMode = SectionMode.Patch; }
        }

        // Only offered on Live Service sections - see SupportsPushedToLive. Moves the contents of
        // a picked folder into this section's Current Build folder instead of extracting an archive.
        public bool IsPushedToLive
        {
            get => _selectedMode == SectionMode.PushedToLive;
            set { if (value) SelectedMode = SectionMode.PushedToLive; }
        }

        public bool IsLaunch
        {
            get => _selectedMode == SectionMode.Launch;
            set { if (value) SelectedMode = SectionMode.Launch; }
        }

        // True only for the Live Service section (permanent or extra) - GMS/CMS never show the
        // "Pushed to Live" mode or its radio button.
        public bool SupportsPushedToLive { get; }

        public bool IsUpdatePanelVisible => SelectedMode == SectionMode.NewBuild || SelectedMode == SectionMode.Patch;
        public bool IsPushedToLivePanelVisible => SelectedMode == SectionMode.PushedToLive;
        public bool IsLaunchPanelVisible => SelectedMode == SectionMode.Launch;
        public string RunButtonLabel => SelectedMode == SectionMode.NewBuild ? "Apply New Build" : "Apply Patch";

        public string SourceArchivePath
        {
            get => _sourceArchivePath;
            set => SetProperty(ref _sourceArchivePath, value);
        }

        public string PendingVersion
        {
            get => _pendingVersion;
            set => SetProperty(ref _pendingVersion, value);
        }

        // Folder picked for "Pushed to Live" - its entire contents get moved into CurrentBuildPath.
        public string PushSourceFolderPath
        {
            get => _pushSourceFolderPath;
            set => SetProperty(ref _pushSourceFolderPath, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(IsNotBusy));
                }
            }
        }

        public bool IsNotBusy => !IsBusy;

        public double ProgressPercent
        {
            get => _progressPercent;
            set => SetProperty(ref _progressPercent, value);
        }

        public string StatusText
        {
            get => _statusText;
            set
            {
                if (SetProperty(ref _statusText, value))
                {
                    RestartStatusClearTimer();
                }
            }
        }

        // Keeps a status message on screen for 10 seconds so it's readable, then clears itself so
        // nothing lingers indefinitely. Restarts on every new message, which is harmless during a
        // burst of frequent progress updates - each one just pushes the clear back out, so it only
        // actually fires once updates stop (i.e. 10 seconds after the final message of a run).
        private void RestartStatusClearTimer()
        {
            _statusClearTimer?.Stop();

            if (string.IsNullOrEmpty(_statusText))
            {
                return;
            }

            if (_statusClearTimer == null)
            {
                _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                _statusClearTimer.Tick += (_, _) =>
                {
                    _statusClearTimer!.Stop();
                    StatusText = string.Empty;
                };
            }

            _statusClearTimer.Start();
        }

        // Same 2 client executables for every section (GMS / CMS / Live Service).
        public IReadOnlyList<string> ExecutableOptions => LaunchServerCatalog.Executables;

        // This section's own fixed server catalog (GMS, CMS, and Live each get their own).
        public IReadOnlyList<LaunchServerOption> ServerOptions { get; }

        public string? SelectedExecutable
        {
            get => _selectedExecutable;
            set
            {
                if (SetProperty(ref _selectedExecutable, value))
                {
                    OnPropertyChanged(nameof(IsSelectedExecutableMissing));
                }
            }
        }

        public LaunchServerOption? SelectedServerOption
        {
            get => _selectedServerOption;
            set => SetProperty(ref _selectedServerOption, value);
        }

        public bool IsSelectedExecutableMissing =>
            HasBuildPath && !string.IsNullOrEmpty(SelectedExecutable) &&
            !File.Exists(Path.Combine(CurrentBuildPath, SelectedExecutable));

        // Documents attached to whichever build this section currently points at - see
        // DocumentsService and SyncDocumentsFolder for how the backing folder is chosen,
        // created, and kept in sync with the build path/version number.
        public ObservableCollection<DocumentEntry> Documents { get; } = new();
        public bool HasDocuments => Documents.Count > 0;

        // What the Documents folder is currently named (without the containing path) - shown in
        // the UI so it's obvious which folder on disk these files live in, e.g. "1.2.3 Documents"
        // or, before a version number is set, "<tab name> Documents".
        public string DocumentsFolderLabel => HasBuildPath
            ? (string.IsNullOrWhiteSpace(_settings.VersionNumber)
                ? DocumentsService.FallbackFolderName(SectionTitle)
                : DocumentsService.VersionedFolderName(_settings.VersionNumber))
            : $"{SectionTitle} Documents";

        public RelayCommand BrowseSourceCommand { get; }
        public AsyncRelayCommand RunUpdateCommand { get; }
        public RelayCommand CancelUpdateCommand { get; }
        public RelayCommand LaunchCommand { get; }
        public RelayCommand BrowsePushSourceCommand { get; }
        public AsyncRelayCommand PushToLiveCommand { get; }
        public RelayCommand AddDocumentFilesCommand { get; }
        public RelayCommand OpenDocumentCommand { get; }

        // Raised right after a build/patch finishes extracting, with this section's build-drive
        // free-space status (or null if it couldn't be checked). MainViewModel subscribes to this
        // on every section to drive the storage tracker at the bottom of the window.
        public event EventHandler<DiskSpaceStatus?>? DiskSpaceStatusChanged;

        private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(SectionTitle));
            OnPropertyChanged(nameof(CurrentBuildPath));
            OnPropertyChanged(nameof(VersionNumber));
            OnPropertyChanged(nameof(HasBuildPath));
            OnPropertyChanged(nameof(IsSelectedExecutableMissing));
            OnPropertyChanged(nameof(DocumentsFolderLabel));

            // Covers a build path, version number, or title being set/changed (whether typed
            // directly in Settings and saved, or a version applied from PendingVersion after a
            // build/patch/push) - any of these can change where/what this section's Documents
            // folder should be.
            SyncDocumentsFolder();
        }

        private void BrowseSource()
        {
            var dialog = new OpenFileDialog
            {
                Title = $"Select {SectionTitle} build/patch archive",
                Filter = "Build/Patch Archives (*.zip;*.7z)|*.zip;*.7z|Zip Archives (*.zip)|*.zip|7-Zip Archives (*.7z)|*.7z",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                SourceArchivePath = dialog.FileName;
            }
        }

        private async Task RunUpdateAsync()
        {
            var mode = SelectedMode == SectionMode.NewBuild ? UpdateMode.NewBuild : UpdateMode.Patch;

            if (mode == UpdateMode.NewBuild)
            {
                var confirm = MessageBox.Show(
                    $"This will remove the existing {SectionTitle} build at:\n{CurrentBuildPath}\n\nand replace it entirely. Continue?",
                    "Confirm New Build",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            IsBusy = true;
            ProgressPercent = 0;
            StatusText = "Starting...";

            // BuildUpdateService now runs the whole extraction/copy on a background thread, so the
            // window stays movable and every tab (including this section's own) stays usable while
            // this runs. The token lets the user back out of a long-running one via CancelUpdateCommand.
            _updateCancellation = new CancellationTokenSource();

            var progress = new Progress<UpdateProgress>(p =>
            {
                ProgressPercent = p.PercentComplete;
                StatusText = p.Status;
            });

            try
            {
                await BuildUpdateService.RunAsync(SourceArchivePath, CurrentBuildPath, mode, progress, _updateCancellation.Token);

                if (!string.IsNullOrWhiteSpace(PendingVersion))
                {
                    _settings.VersionNumber = PendingVersion;
                    _settingsService.Save(_appSettings);
                    PendingVersion = string.Empty;
                }

                StatusText = $"{SectionTitle} updated successfully.";

                // Refresh the storage tracker with this section's build drive now that the extraction landed.
                DiskSpaceStatusChanged?.Invoke(this, DiskSpaceService.CheckDiskSpace(CurrentBuildPath));
            }
            catch (OperationCanceledException)
            {
                StatusText = $"{SectionTitle} update cancelled.";
            }
            catch (Exception ex)
            {
                StatusText = $"Update failed: {ex.Message}";
                MessageBox.Show(ex.Message, "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                _updateCancellation?.Dispose();
                _updateCancellation = null;
            }
        }

        private void BrowsePushSource()
        {
            var dialog = new OpenFolderDialog
            {
                Title = $"Select folder to push to {SectionTitle}",
                InitialDirectory = string.IsNullOrWhiteSpace(PushSourceFolderPath) || !Directory.Exists(PushSourceFolderPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                    : PushSourceFolderPath
            };

            if (dialog.ShowDialog() == true)
            {
                PushSourceFolderPath = dialog.FolderName;
            }
        }

        private async Task PushToLiveAsync()
        {
            var confirm = MessageBox.Show(
                $"This will move everything from:\n{PushSourceFolderPath}\n\ninto the {SectionTitle} Current Build folder:\n{CurrentBuildPath}\n\n" +
                $"The {SectionTitle} folder will be cleared first, and then the entire contents of the source folder will be moved into it. This can't be undone. Continue?",
                "Confirm Push to Live",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            IsBusy = true;
            ProgressPercent = 0;
            StatusText = "Starting...";

            // Shares _updateCancellation with RunUpdateAsync - only one long-running operation can
            // be in flight per section at a time (both gated by IsBusy), so one token covers both.
            _updateCancellation = new CancellationTokenSource();

            var progress = new Progress<UpdateProgress>(p =>
            {
                ProgressPercent = p.PercentComplete;
                StatusText = p.Status;
            });

            try
            {
                await PushToLiveService.RunAsync(PushSourceFolderPath, CurrentBuildPath, progress, _updateCancellation.Token);

                if (!string.IsNullOrWhiteSpace(PendingVersion))
                {
                    // Setting VersionNumber raises Settings_PropertyChanged -> SyncDocumentsFolder,
                    // which carries this section's existing Documents folder over to the new
                    // version-named folder - "along with the build, keep the documents with the
                    // build" for Pushed to Live specifically asked for.
                    _settings.VersionNumber = PendingVersion;
                    _settingsService.Save(_appSettings);
                    PendingVersion = string.Empty;
                }

                StatusText = $"{SectionTitle} pushed to Live successfully.";
                PushSourceFolderPath = string.Empty;

                // Refresh the storage tracker now that the move landed.
                DiskSpaceStatusChanged?.Invoke(this, DiskSpaceService.CheckDiskSpace(CurrentBuildPath));
            }
            catch (OperationCanceledException)
            {
                StatusText = "Push to Live cancelled.";
            }
            catch (Exception ex)
            {
                StatusText = $"Push to Live failed: {ex.Message}";
                MessageBox.Show(ex.Message, "Push to Live Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                _updateCancellation?.Dispose();
                _updateCancellation = null;
            }
        }

        private void Launch()
        {
            if (string.IsNullOrEmpty(SelectedExecutable) || SelectedServerOption == null)
            {
                return;
            }

            string fullPath = Path.Combine(CurrentBuildPath, SelectedExecutable);

            if (!File.Exists(fullPath))
            {
                StatusText = $"{SelectedExecutable} was not found in the build folder.";
                MessageBox.Show(StatusText, "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                var process = new Process();
                process.StartInfo.FileName = fullPath;
                process.StartInfo.Arguments = SelectedServerOption.LaunchArgument;
                process.StartInfo.WorkingDirectory = CurrentBuildPath;
                process.StartInfo.UseShellExecute = true;
                process.Start();

                StatusText = $"Launched {SelectedExecutable} -> {SelectedServerOption.DisplayName}.";
            }
            catch (Exception ex)
            {
                StatusText = $"Launch failed: {ex.Message}";
                MessageBox.Show(ex.Message, "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Recomputes where this section's Documents folder should be (a sibling of
        // CurrentBuildPath, named from VersionNumber or SectionTitle - see DocumentsService), and
        // reconciles reality with that: carries an existing folder over to a new name if the
        // version number (or the tab's title) just changed, creates it if it doesn't exist yet,
        // and (re)starts the watcher pointed at wherever it ends up. Called once from the
        // constructor (to rehydrate a section that already had a build path from a previous
        // session) and on every Settings_PropertyChanged after that. No-ops until HasBuildPath is
        // true - "the folder gets created once the selection and build name are made," not before.
        private void SyncDocumentsFolder()
        {
            if (!HasBuildPath)
            {
                StopWatching();
                _documentsFolderPath = null;
                Documents.Clear();
                return;
            }

            string? newPath = DocumentsService.FolderPathFor(CurrentBuildPath, DocumentsFolderLabel);
            if (newPath == null)
            {
                return;
            }

            if (string.Equals(_documentsFolderPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DocumentsService.RenameFolder(_documentsFolderPath, newPath);
            DocumentsService.EnsureFolder(newPath);
            _documentsFolderPath = newPath;
            StartWatching(newPath);
        }

        private void StartWatching(string path)
        {
            StopWatching();

            try
            {
                _documentsWatcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
                _documentsWatcher.Created += OnDocumentsFolderChanged;
                _documentsWatcher.Deleted += OnDocumentsFolderChanged;
                _documentsWatcher.Renamed += OnDocumentsFolderChanged;
                _documentsWatcher.EnableRaisingEvents = true;
            }
            catch (IOException)
            {
                // Folder vanished out from under us between EnsureFolder and here (e.g. deleted
                // externally) - the list just won't auto-refresh until the next settings change
                // re-runs SyncDocumentsFolder.
                _documentsWatcher = null;
            }

            RefreshDocumentsList();
        }

        // Stops and disposes the watcher, if any. Public so MainViewModel can call it when a tab
        // is deleted (its BuildSectionViewModel is about to be dropped entirely).
        public void StopWatching()
        {
            if (_documentsWatcher == null)
            {
                return;
            }

            _documentsWatcher.EnableRaisingEvents = false;
            _documentsWatcher.Created -= OnDocumentsFolderChanged;
            _documentsWatcher.Deleted -= OnDocumentsFolderChanged;
            _documentsWatcher.Renamed -= OnDocumentsFolderChanged;
            _documentsWatcher.Dispose();
            _documentsWatcher = null;
        }

        // FileSystemWatcher raises events on a thread-pool thread, never the UI thread - marshal
        // back before touching the ObservableCollection bound to the UI.
        private void OnDocumentsFolderChanged(object sender, FileSystemEventArgs e)
        {
            _uiDispatcher.BeginInvoke(new Action(RefreshDocumentsList));
        }

        private void RefreshDocumentsList()
        {
            Documents.Clear();

            if (_documentsFolderPath == null)
            {
                return;
            }

            foreach (var entry in DocumentsService.ListEntries(_documentsFolderPath))
            {
                Documents.Add(entry);
            }
        }

        private void AddDocumentFiles()
        {
            var dialog = new OpenFileDialog
            {
                Title = $"Add documents to {SectionTitle}",
                Multiselect = true,
                CheckFileExists = true,
                Filter = "All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                ImportDocumentPaths(dialog.FileNames);
            }
        }

        // Copies the given files/folders (from the "Add File..." dialog, or dropped in from
        // Explorer - see BuildSectionView's drag-and-drop handlers) into this section's Documents
        // folder, creating it first if this is the very first document added.
        public void ImportDocumentPaths(IEnumerable<string> paths)
        {
            if (!HasBuildPath)
            {
                return;
            }

            if (_documentsFolderPath == null)
            {
                SyncDocumentsFolder();
                if (_documentsFolderPath == null)
                {
                    return;
                }
            }

            DocumentsService.AddPaths(_documentsFolderPath, paths);
            RefreshDocumentsList();
        }

        private void OpenDocument(DocumentEntry entry)
        {
            try
            {
                var process = new Process();
                process.StartInfo.FileName = entry.FullPath;
                process.StartInfo.UseShellExecute = true;
                process.Start();
            }
            catch (Exception ex)
            {
                StatusText = $"Couldn't open {entry.Name}: {ex.Message}";
            }
        }

        // Called by MainViewModel when this tab is deleted (via Settings) - its documents
        // shouldn't outlive the tab itself. Never called for the 5 permanent tabs, which can't
        // be deleted.
        public void DeleteDocumentsFolder()
        {
            DocumentsService.DeleteFolder(_documentsFolderPath);
            _documentsFolderPath = null;
        }
    }
}
