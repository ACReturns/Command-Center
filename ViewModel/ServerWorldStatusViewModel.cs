using CommandCenter.Model;

namespace CommandCenter.ViewModel
{
    public class ServerWorldStatusViewModel : ViewModelBase
    {
        private bool? _isUp; // null = not checked yet

        public ServerWorldStatusViewModel(ServerWorld world)
        {
            World = world;
        }

        public ServerWorld World { get; }
        public string Name => World.Name;

        public bool? IsUp
        {
            get => _isUp;
            set
            {
                if (SetProperty(ref _isUp, value))
                {
                    OnPropertyChanged(nameof(IsChecking));
                    OnPropertyChanged(nameof(StatusGifPath));
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public bool IsChecking => IsUp == null;

        public string StatusGifPath => IsUp == true ? AppPaths.ServerUpGif : AppPaths.ServerDownGif;

        public string StatusText => IsUp == null ? "Checking..." : (IsUp == true ? "Online" : "Offline");
    }
}
