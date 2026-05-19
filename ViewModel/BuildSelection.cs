using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static System.Net.Mime.MediaTypeNames;

namespace CommandCenter.ViewModel
{
    public class BuildSelection : INotifyPropertyChanged
    {
        #region Properties
        public ICommand SelectBuildCommand { get; set; }
        public ICommand UpdateBuildCommand { get; set; }
        public ICommand PatchUpdateCommand { get; set; }
        public ICommand LaunchBuildCommand { get; set; }
        public ICommand ExtractBuildCommand { get; set; }
        public ICommand ServerStatusCommand { get; set; }

        public BuildSelection()
        {
            // Bind the command to a method
            SelectBuildCommand = new RelayCommand(ExecuteSelectBuildAction, CanExecuteSelectBuildAction);
            PatchUpdateCommand = new RelayCommand(ExecuteUpdateAction, CanExecuteSelectBuildAction);
            LaunchBuildCommand = new RelayCommand(ExecuteLaunchBuildAction, CanExecuteSelectBuildAction);
            ExtractBuildCommand = new RelayCommand(OnExtractZipClick, CanExecuteSelectBuildAction);
            ServerStatusCommand = new RelayCommand(ExecuteServerStatusAction, CanExecuteSelectBuildAction);
        }

        private string _currentBuildPath;
        private string _newBuildPath = "New Build Path...";
        private string _newPatchPath = "New Patch Path...";
        private Button _updateButton = new Button();
        public ProgressBar ExtractionProgressBar { get; set; }
        public TextBlock StatusLabel { get; set; }
        //public Button btnUpdate { get; set; }

        static Dictionary<string, string> ServerPaths = new Dictionary<string, string>
        {
            {"Test 1", "GameLaunching 34.217.160.238 8484"},
            {"Test 2", "GameLaunching 52.43.197.199 8484"},
            {"Test 3", "GameLaunching 54.148.16.230 8484"},
            {"Test 4", "GameLaunching 54.148.59.7 8484"},
            {"Test 6", "GameLaunching 52.89.167.110 8484"},
            {"Staging (EU)", "GameLaunching 3.77.198.24 8484"},
            {"Staging 1 (NA)", "Gamelaunching 44.234.170.29 8484"},
            {"Staging 2 (NA)", "Gamelaunching 44.234.182.79 8484"}
        };

        private List<string> _servers = ServerPaths.Keys.ToList();
        private string _selectedServer = ServerPaths.Keys.First();
        private string tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Temp");

        public Button btnUpdate
        {
            get { return _updateButton; }
            set { _updateButton = value; }
        }

        public List<string> Servers
        {
            get { return _servers; }
            set { _servers = value; }
        }

        public string CurrentBuildPath 
        {
            get { return _currentBuildPath; }
            set
            {
                if (_currentBuildPath != value)
                {
                    _currentBuildPath = value;
                    OnPropertyChanged(CurrentBuildPath);
                }
            }
        }

        public string NewBuildPath 
        {
            get => _newBuildPath;
            set
            {
                if (_newBuildPath != value)
                {
                    _newBuildPath = value;
                }
                OnPropertyChanged(NewBuildPath);
            }
        }

        public string NewPatchPath
        {
            get => _newPatchPath;
            set
            {
                if (_newPatchPath != value)
                {
                    _newPatchPath = value;
                }
                OnPropertyChanged(NewPatchPath);
            }
        }

        public string SelectedServer
        {
            get => _selectedServer;
            set
            {
                if (_selectedServer != value)
                {
                    _selectedServer = value;
                }
                OnPropertyChanged(SelectedServer);
            }
        }

        
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        #endregion

        #region Server Status Actions
        // Ref: https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.tcpclient.connect?view=netframework-4.8.1
        public bool IsServerUp(string host, int port)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    // Connect with a timeout
                    var result = client.BeginConnect(host, port, null, null);
                    bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));

                    if (!success) return false;

                    client.EndConnect(result);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
        public void ExecuteServerStatusAction(object obj)
        {
            string filePath = @"C:\Users\dosmith\Desktop\MapleStory\Server Check Files\MSLIVE_Server Status_AWS\server_status.json";

            if (!File.Exists(filePath))
            {
                return;
            }

            // TODO make a dictionary that seperates each server by Port/ IP and then loop through them to check the status of each server and display it in a user friendly way, maybe a list with green/ red indicators for up/ down status
            string serverIP = "52.41.88.16";
            int port = 8585;

            if (IsServerUp(serverIP, port))
            {
                MessageBox.Show($"Server {serverIP} is up!");
            }
            else
            {
                MessageBox.Show($"Server {serverIP} is down.");
            }
        }
        #endregion

            #region Get Build Path Actions
        private void ExecuteSelectBuildAction(object obj)
        {
            // Display dialog to select current build directory

            var dialog = new OpenFolderDialog();
            dialog.Multiselect = false;
            dialog.Title = "Select a folder";
            dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            bool? result = dialog.ShowDialog();

            if (result == true)
            {
                CurrentBuildPath = dialog.FolderName;
            }
        }
        #endregion

        #region Update Build Actions
        private void ExecuteUpdateAction(object obj)
        {
            // Display dialog to select Updated build/ Patch  directory

            var dialog = new OpenFileDialog();
            dialog.Multiselect = false;
            dialog.Title = "Select a Zip file";
            dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            bool? result = dialog.ShowDialog();

            if (result == true)
            {
                NewPatchPath = dialog.FileName;
            }
        }
        #endregion

        private bool CanExecuteSelectBuildAction(object obj)
        {
            // Return false to automatically disable the button
            return true;
        }

        #region Launch Build Actions
        private void ExecuteLaunchBuildAction(object obj)
        {
            if (!Path.Exists(CurrentBuildPath))
            {
                MessageBox.Show("Please set a valid build directory to launch a build from then try again");
                return;
            }
            //ServerSelection();

            // Launch Build
            var launchInfo = new ProcessStartInfo("MapleStoryA.exe");
            

            Process process = new Process();
            string launchServer = "0.0.0.0";

            // Set the server to utilize
            foreach (KeyValuePair<string, string> entry in ServerPaths)
            {
                if (entry.Key == SelectedServer)
                {
                    launchServer = entry.Value;
                    break;
                }
            }
            
            process.StartInfo.WorkingDirectory = CurrentBuildPath;
            process.StartInfo.FileName = "MapleStoryA.exe";
            process.StartInfo.Arguments = launchServer + " -w";

            process.StartInfo.Verb = "runas";
            process.StartInfo.WindowStyle = ProcessWindowStyle.Normal;

            //Optional: Hide the window and redirect output
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.UseShellExecute = true;
            process.StartInfo.RedirectStandardOutput = false;

            if (!File.Exists(Path.Combine(CurrentBuildPath, "MapleStoryA.exe")))
            {
                MessageBox.Show("The directory does not contain the MapleStoryA.exe, please verify you are using the correct directory and try again.");
                return;
            }

            // File & Directory exist, so we can launch the build now
            try
            {
                Process.Start(process.StartInfo);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Error moving {SelectedServer}: {ex.Message}");
            }
        }
        #endregion

        #region Extraction Actions
        private void ExecuteExtractAction(object obj)
        {
            // TODO Look into adding Async to not lockup the UI while its running extractions
            // TODO Lock other buttons while this is running to not cause issues, seet Variable thats flagged to active/ inactive

            // Extract the patch in a temp directory on the desktop 
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            //OnExtractZipClick();
            //ZipFile.ExtractToDirectory(NewPatchPath, tempDir);

            //Move files into the main directory, excluding the header folders from the Zip file

            foreach (string subDir in Directory.GetDirectories(tempDir))
            {
                string folderName = Path.GetFileName(subDir);

                foreach (string filePath in Directory.GetFiles(subDir))
                {
                    string fileName = Path.GetFileName(filePath);
                    string destFilePath = Path.Combine(CurrentBuildPath, fileName);

                    try
                    {
                        File.Move(filePath, destFilePath, overwrite: true);
                    }
                    catch (IOException ex)
                    {
                        Console.WriteLine($"Error moving {fileName}: {ex.Message}");
                    }
                }
            }

            // Step 3 Clean up temp directory
            Directory.Delete(tempDir, recursive: true);
        }

        private async void OnExtractZipClick(Action<object, RoutedEventArgs> sender, RoutedEventArgs e)
        {
            // Extract the patch in a temp directory on the desktop 
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            // Setup IProgress to update the UI on the main thread
            var progress = new Progress<double>(value =>
            {
                ExtractionProgressBar.Value = value;
                StatusLabel.Text = $"Progress: {value:F0}%";
            });

            var progress2 = new Progress<double>(value =>
            {
                ExtractionProgressBar.Value = value;
                StatusLabel.Text = $"Progress: {value:F0}%";
            });

            await Task.Run(() => ExtractWithProgress(NewPatchPath, tempDir, progress));
            StatusLabel.Text = "Extraction Complete!";

            await Task.Run(() => MoveWithProgress(progress2));
            StatusLabel.Text = "Move Complete!";
        }

        private void ExtractWithProgress(string zipPath, string extractPath, IProgress<double> progress)
        {
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                int totalFiles = archive.Entries.Count;
                int extractedFiles = 0;

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    // Determine full path for the extracted file
                    string fullPath = Path.Combine(extractPath, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

                    // Extract the individual file
                    if (!string.IsNullOrEmpty(entry.Name)) // Skips directories which are entries too
                    {
                        entry.ExtractToFile(fullPath, overwrite: true);
                    }

                    // Report progress
                    extractedFiles++;
                    double percentage = (double)extractedFiles / totalFiles * 100;
                    progress.Report(percentage);
                }
            }
        }

        private void MoveWithProgress(IProgress<double> progress)
        {
            //Move files into the main directory, excluding the header folders from the Zip file
            foreach (string subDir in Directory.GetDirectories(tempDir))
            {
                int totalFiles = tempDir.Count();
                int movedFiles = 0;
                string folderName = Path.GetFileName(subDir);

                foreach (string filePath in Directory.GetFiles(subDir))
                {
                    string fileName = Path.GetFileName(filePath);
                    string destFilePath = Path.Combine(CurrentBuildPath, fileName);

                    try
                    {
                        File.Move(filePath, destFilePath, overwrite: true);
                    }
                    catch (IOException ex)
                    {
                        Console.WriteLine($"Error moving {fileName}: {ex.Message}");
                    }

                    // Report progress
                    movedFiles++;
                    double percentage = (double)movedFiles / totalFiles * 100;
                    progress.Report(percentage);
                }
            }

            // Step 3 Clean up temp directory
            Directory.Delete(tempDir, recursive: true);
        }
        #endregion
    }
}
