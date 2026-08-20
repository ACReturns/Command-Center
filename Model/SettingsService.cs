using System;
using System.IO;
using System.Text.Json;

namespace CommandCenter.Model
{
    // Persists AppSettings (build paths + version numbers) to a JSON file under
    // %AppData%\CommandCenter so selections survive an app restart.
    public class SettingsService
    {
        private readonly string _settingsPath;

        public SettingsService()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CommandCenter");
            Directory.CreateDirectory(folder);
            _settingsPath = Path.Combine(folder, "settings.json");
        }

        public AppSettings Load()
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            try
            {
                string json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch (Exception)
            {
                return new AppSettings();
            }
        }

        public void Save(AppSettings settings)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(settings, options);
            File.WriteAllText(_settingsPath, json);
        }
    }
}
