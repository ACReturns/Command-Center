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

        public ServerGroupViewModel(string title, string worldsFilePath)
        {
            Title = title;
            _worldsFilePath = worldsFilePath;

            RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
            LoadWorlds();
        }

        public string Title { get; }
        public ObservableCollection<ServerWorldStatusViewModel> Worlds { get; } = new();
        public AsyncRelayCommand RefreshCommand { get; }

        public bool IsRefreshing
        {
            get => _isRefreshing;
            set => SetProperty(ref _isRefreshing, value);
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
