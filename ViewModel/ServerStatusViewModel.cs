using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using CommandCenter.Model;
using CommandCenter.View;

namespace CommandCenter.ViewModel
{
    public class ServerStatusViewModel : ViewModelBase
    {
        private readonly AppSettings _appSettings;
        private readonly SettingsService _settingsService;

        public ServerStatusViewModel(AppSettings appSettings, SettingsService settingsService)
        {
            _appSettings = appSettings;
            _settingsService = settingsService;

            var groupSettings = appSettings.ServerStatus;

            // Display order is Test, Staging, Live (per user request - was Live/Staging/Test).
            Test = new ServerGroupViewModel("Test", AppPaths.TestWorldsFile, groupSettings.TestExpanded);
            Staging = new ServerGroupViewModel("Staging", AppPaths.StagingWorldsFile, groupSettings.StagingExpanded);
            Live = new ServerGroupViewModel("Live", AppPaths.LiveWorldsFile, groupSettings.LiveExpanded);

            Groups = new ObservableCollection<ServerGroupViewModel> { Test, Staging, Live };

            // User-added groups (see AddServer) - loaded in the order they were saved, always
            // after the 3 built-in ones so a freshly-added server lands at the bottom, same place
            // it was appended when it was created.
            foreach (var custom in groupSettings.CustomGroups)
            {
                string path = Path.Combine(AppPaths.ServersFolder, custom.FileName);
                Groups.Add(new ServerGroupViewModel(custom.Title, path, custom.IsExpanded, custom.Id));
            }

            RefreshAllCommand = new AsyncRelayCommand(_ => Task.WhenAll(Groups.Select(g => g.RefreshAsync())));
            AddServerCommand = new RelayCommand(_ => AddServer());
            RenameServerCommand = new RelayCommand(RenameServer);
            DeleteServerCommand = new RelayCommand(DeleteServer);

            // Give the user an at-a-glance status as soon as the app starts, without requiring a click.
            RefreshAllCommand.Execute(null);
        }

        public ServerGroupViewModel Live { get; }
        public ServerGroupViewModel Staging { get; }
        public ServerGroupViewModel Test { get; }

        // What the view actually renders, in display order - the 3 built-in groups above plus any
        // user-added ones. Live/Staging/Test are kept as named properties too since nothing else
        // needs to change to keep referring to them individually.
        public ObservableCollection<ServerGroupViewModel> Groups { get; }

        public AsyncRelayCommand RefreshAllCommand { get; }
        public RelayCommand AddServerCommand { get; }
        public RelayCommand RenameServerCommand { get; }
        public RelayCommand DeleteServerCommand { get; }

        // Legend icons at the top of the view - same gifs used per-world, bound once here so the
        // XAML doesn't need to know the app's file layout.
        public string ServerUpGifPath => AppPaths.ServerUpGif;
        public string ServerDownGifPath => AppPaths.ServerDownGif;

        // Called by MainViewModel.SelectedTab when the user navigates away from the Server Status
        // tab - persists whichever groups are currently expanded/collapsed (built-in and
        // user-added alike) so it's remembered next launch. Not written on every toggle; only once,
        // on leaving the tab. Adding a server (below) saves immediately instead, since that's an
        // explicit, one-shot action rather than something to batch up.
        public void SaveExpandedState()
        {
            _appSettings.ServerStatus.LiveExpanded = Live.IsExpanded;
            _appSettings.ServerStatus.StagingExpanded = Staging.IsExpanded;
            _appSettings.ServerStatus.TestExpanded = Test.IsExpanded;

            foreach (var group in Groups)
            {
                if (group.CustomGroupId is not Guid id)
                {
                    continue;
                }

                var entry = _appSettings.ServerStatus.CustomGroups.FirstOrDefault(c => c.Id == id);
                if (entry != null)
                {
                    entry.IsExpanded = group.IsExpanded;
                }
            }

            _settingsService.Save(_appSettings);
        }

        // "Add New Server": name it, browse for its worlds json, copy that file into the Servers
        // folder (so it survives independently of wherever the user originally picked it from),
        // load it as a new group at the bottom of the visible list, refresh it immediately, and
        // persist it so it's back next launch too.
        private void AddServer()
        {
            if (!AddServerDialog.PromptForNewServer(Application.Current?.MainWindow, out string title, out string sourcePath))
            {
                return;
            }

            if (!TryValidateWorldsFile(sourcePath, out string validationError))
            {
                MessageBox.Show(Application.Current?.MainWindow, $"Couldn't add \"{title}\": {validationError}",
                    "Add New Server", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string destPath;
            try
            {
                destPath = CopyIntoServersFolder(title, sourcePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Application.Current?.MainWindow, $"Couldn't add \"{title}\": {ex.Message}",
                    "Add New Server", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var customId = Guid.NewGuid();
            var group = new ServerGroupViewModel(title, destPath, initiallyExpanded: true, customGroupId: customId);
            Groups.Add(group);

            _appSettings.ServerStatus.CustomGroups.Add(new CustomServerGroupSettings
            {
                Id = customId,
                Title = title,
                FileName = Path.GetFileName(destPath),
                IsExpanded = true
            });
            _settingsService.Save(_appSettings);

            // Refresh just the new group so its worlds' up/down state shows immediately, matching
            // what the 3 built-in groups get at startup - fire-and-forget is fine here, same as
            // RefreshCommand elsewhere; IsRefreshing on the group drives its own "Checking..." UI.
            _ = group.RefreshAsync();
        }

        // Rename/Delete only ever apply to a group added via "Add New Server" - the buttons that
        // send these commands are only visible for those (ServerGroupViewModel.IsCustom), but the
        // CustomGroupId check here is the actual guard, in case a command parameter ever comes from
        // somewhere else.
        private void RenameServer(object? parameter)
        {
            if (parameter is not ServerGroupViewModel group || group.CustomGroupId is not Guid id)
            {
                return;
            }

            if (!RenameServerDialog.PromptForName(Application.Current?.MainWindow, group.Title, out string newTitle))
            {
                return;
            }

            group.Title = newTitle;

            var entry = _appSettings.ServerStatus.CustomGroups.FirstOrDefault(c => c.Id == id);
            if (entry != null)
            {
                entry.Title = newTitle;
            }

            _settingsService.Save(_appSettings);
        }

        private void DeleteServer(object? parameter)
        {
            if (parameter is not ServerGroupViewModel group || group.CustomGroupId is not Guid id)
            {
                return;
            }

            var confirm = MessageBox.Show(Application.Current?.MainWindow,
                $"Remove \"{group.Title}\" from Server Status? This also deletes its server status json from the Servers folder.",
                "Delete Server", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            Groups.Remove(group);

            var entry = _appSettings.ServerStatus.CustomGroups.FirstOrDefault(c => c.Id == id);
            if (entry != null)
            {
                _appSettings.ServerStatus.CustomGroups.Remove(entry);

                try
                {
                    string path = Path.Combine(AppPaths.ServersFolder, entry.FileName);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // Best-effort - the group's already gone from the UI/settings either way, and a
                    // locked/missing file shouldn't block removing it from the app.
                }
            }

            _settingsService.Save(_appSettings);
        }

        // Sanity-checks the picked file actually looks like a worlds json (same shape ServerGroupViewModel.LoadWorlds
        // expects) before it's copied anywhere - a bad pick should fail loudly here, not silently
        // produce an empty group card.
        private static bool TryValidateWorldsFile(string path, out string error)
        {
            try
            {
                string json = File.ReadAllText(path);
                var worlds = JsonSerializer.Deserialize<System.Collections.Generic.List<ServerWorld>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (worlds == null || worlds.Count == 0)
                {
                    error = "The file doesn't contain any server entries.";
                    return false;
                }

                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Not a valid server status json ({ex.Message}).";
                return false;
            }
        }

        // Copies the picked json into the Servers folder under a safe, collision-free file name
        // derived from the server's name - independent of whatever the source file was originally
        // called, and never overwrites an existing group's file (built-in or user-added).
        private static string CopyIntoServersFolder(string title, string sourcePath)
        {
            Directory.CreateDirectory(AppPaths.ServersFolder);

            string slug = new string(title.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
            if (string.IsNullOrWhiteSpace(slug))
            {
                slug = "server";
            }

            string fileName = $"{slug}_server_status.json";
            string destPath = Path.Combine(AppPaths.ServersFolder, fileName);

            int suffix = 2;
            while (File.Exists(destPath))
            {
                fileName = $"{slug}_server_status_{suffix}.json";
                destPath = Path.Combine(AppPaths.ServersFolder, fileName);
                suffix++;
            }

            File.Copy(sourcePath, destPath);
            return destPath;
        }
    }
}
