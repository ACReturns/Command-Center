using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommandCenter.Model;

namespace CommandCenter.ViewModel
{
    // Backs the permanent OpTool tab: which OpTool endpoint is picked, and a Go command that asks
    // the view to navigate there. Knows nothing about WebView2 itself - OpToolView subscribes to
    // NavigationRequested and drives the actual browser (lazy init, back/reload, status text),
    // since all of that is control state rather than app state.
    //
    // One instance for the app's lifetime (MainViewModel.OpTool), same as ServerStatus/Settings.
    // Nothing here is persisted: no endpoint choice, and definitely no credentials.
    public class OpToolViewModel : ViewModelBase
    {
        private OpToolEndpoint? _selectedEndpoint;

        public OpToolViewModel()
        {
            Endpoints = OpToolCatalog.Endpoints;
            _selectedEndpoint = Endpoints.FirstOrDefault();
            GoCommand = new RelayCommand(_ => Go(), _ => SelectedEndpoint != null);
        }

        public IReadOnlyList<OpToolEndpoint> Endpoints { get; }

        public OpToolEndpoint? SelectedEndpoint
        {
            get => _selectedEndpoint;
            set => SetProperty(ref _selectedEndpoint, value);
        }

        public ICommand GoCommand { get; }

        // Raised with the selected endpoint's URL whenever Go is hit - OpToolView handles it.
        public event EventHandler<Uri>? NavigationRequested;

        private void Go()
        {
            if (SelectedEndpoint is { } endpoint)
            {
                NavigationRequested?.Invoke(this, endpoint.Uri);
            }
        }
    }
}
