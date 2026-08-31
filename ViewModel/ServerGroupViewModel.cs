using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommandCenter.Model;
using CommandCenter.Services;

namespace CommandCenter.ViewModel
{
    // One environment's worth of worlds (Live, Staging, or Test), loaded from its
    // *_server_status.json file, with live reachability checks against each world's ports.
    public class ServerGroupViewModel : ViewModelBase
    {
        private readonly string _worldsFilePath;
        private bool _isRefreshing;
        private bool _isExpanded;
        private string _title;

        // Non-null only for a group added via Server Status' "Add New Server" - ties this group
        // back to its CustomServerGroupSettings entry so ServerStatusViewModel can find and update
        // (or remove) that entry without matching on Title, which the user can change. Null for the
        // 3 built-in groups (Live/Staging/Test), which aren't in CustomGroups at all and can't be
        // renamed/deleted (see IsCustom).
        public ServerGroupViewModel(string title, string worldsFilePath, bool initiallyExpanded, Guid? customGroupId = null)
        {
            _title = title;
            _worldsFilePath = worldsFilePath;
            _isExpanded = initiallyExpanded;
            CustomGroupId = customGroupId;

            RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
            LoadWorlds();
        }

        // Settable (not just get-only) so ServerStatusViewModel.RenameServer can update it in place
        // and have the Expander header pick the new text up immediately via binding.
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        public Guid? CustomGroupId { get; }

        // Drives the Rename/Delete buttons' visibility in ServerStatusView - only ever true for a
        // group loaded from CustomGroups, never for Live/Staging/Test.
        public bool IsCustom => CustomGroupId.HasValue;
        public ObservableCollection<ServerWorldStatusViewModel> Worlds { get; } = new();
        public AsyncRelayCommand RefreshCommand { get; }

        public bool IsRefreshing
        {
            get => _isRefreshing;
            set => SetProperty(ref _isRefreshing, value);
        }

        // Bound two-way to the Expander in ServerStatusView. Live/Staging start collapsed,
        // Test starts expanded (see ServerStatusSettings) - whatever the user leaves it as gets
        // persisted by ServerStatusViewModel.SaveExpandedState when they leave the tab.
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        private void LoadWorlds()
        {
            Worlds.Clear();

            if (!File.Exists(_worldsFilePath))
            {
                return;
            }

            try
            {
                string json = File.ReadAllText(_worldsFilePath);
                var worlds = JsonSerializer.Deserialize<List<ServerWorld>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new List<ServerWorld>();

                foreach (var world in worlds)
                {
                    Worlds.Add(new ServerWorldStatusViewModel(world));
                }
            }
            catch
            {
                // Malformed status file - leave the group empty rather than crashing the app.
            }
        }

        public async Task RefreshAsync()
        {
            if (Worlds.Count == 0)
            {
                LoadWorlds();
            }

            IsRefreshing = true;

            try
            {
                var timeout = TimeSpan.FromSeconds(2);
                var checks = Worlds.Select(async w => w.IsUp = await PortStatusService.IsWorldOnlineAsync(w.World, timeout));
                await Task.WhenAll(checks);
            }
            finally
            {
                IsRefreshing = false;
            }
        }
    }
}
