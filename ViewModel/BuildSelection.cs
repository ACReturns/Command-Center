using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        public ICommand SelectBuildCommand { get; set; }
        public ICommand UpdateBuildCommand { get; set; }
        public ICommand PatchUpdateCommand { get; set; }
        public ICommand LaunchBuildCommand { get; set; }
        public ICommand ExtractCommand { get; set; }

        private string _currentBuildPath = "Current Build Path...";
        private string _newBuildPath = "New Build Path...";
        private string _newPatchPath = "New Patch Path...";
        private string _selectedServer = "Test 1";
        private List<string> _servers = [];
        private string tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Temp");
        
        public List<string> Servers
        {
            get 
            {
                string[] serverFiles = Directory.GetFiles(@"C:\Users\dosmith\Desktop\MapleStory", "*.bat");

                // Verify its empty before adding to avoid duplicates, this allows for dynamic adding of servers without having to restart the program to update the dropdown
                if (_servers.Count == 0)
                {
                    foreach (string file in serverFiles)
                    {
                        _servers.Add(Path.GetFileName(file));
                    }
                }

                return new List<string>(_servers); 
            }
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
                }
                OnPropertyChanged(CurrentBuildPath);
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
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public BuildSelection()
        {
            // Bind the command to a method
            SelectBuildCommand = new RelayCommand(ExecuteSelectBuildAction, CanExecuteSelectBuildAction);
            PatchUpdateCommand = new RelayCommand(ExecuteUpdateAction, CanExecuteSelectBuildAction);
            LaunchBuildCommand = new RelayCommand(ExecuteLaunchBuildAction, CanExecuteSelectBuildAction);
            ExtractCommand = new RelayCommand(ExecuteExtractAction, CanExecuteSelectBuildAction);
            //InitializeServers();
        }

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

        private void ExecuteLaunchBuildAction(object obj)
        {
            ServerSelection();

            // Launch Build
            
            Process process = new Process();
            process.StartInfo.WorkingDirectory = Path.GetDirectoryName(SelectedServer);
            process.StartInfo.FileName = SelectedServer;


            //Optional: Hide the window and redirect output
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true; 

            try
            {
                Process.Start(SelectedServer);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Error moving {SelectedServer}: {ex.Message}");
            }
        }

        private void ServerSelection()
        {
            // Get server selection from dropdown
            var serverChoice = SelectedServer.Replace(" ", "");

            // Get batch file to launch server
            string[] serverFiles = Directory.GetFiles(@"C:\Users\dosmith\Desktop\MapleStory", "*.bat");
            List<string> testServers = [];
            List<string> stagingServers = [];

            foreach (string file in serverFiles)
            {
                if (file.Contains("Staging"))
                {
                    stagingServers.Add(file);
                }
                else if (file.Contains("Test"))
                {
                    testServers.Add(file);
                }
            }

            // Set server launch batch path
            if (serverChoice.Contains("Staging"))
            {
                foreach (string server in stagingServers)
                {
                    if (serverChoice.Contains("EU") && server.Contains("EU"))
                    {
                        serverChoice = server;
                        break;
                    }
                    else if (serverChoice.Contains("2") && server.Contains("2"))
                    {
                        serverChoice = server;
                        break;
                    }
                    else if (serverChoice.Contains("NA") && server.Contains("NA") && !server.Contains("2"))
                    {
                        serverChoice = server;
                        break;
                    }
                }
            }
            else if (serverChoice.Contains("Test"))
            {
                foreach (string server in testServers)
                {
                    if (server.Contains(serverChoice))
                    {
                        SelectedServer = server;
                        break;
                    }
                }
            }
        }

        private void ExecuteExtractAction(object obj)
        {
            // TODO Look into adding Async to not lockup the UI while its running extractions
            // TODO Lock other buttons while this is running to not cause issues, seet Variable thats flagged to active/ inactive

            // Extract the patch in a temp directory on the desktop 
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            ZipFile.ExtractToDirectory(NewPatchPath, tempDir);

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

        private bool CanExecuteSelectBuildAction(object obj)
        {
            // Return false to automatically disable the button
            return true;
        }
    }
}
