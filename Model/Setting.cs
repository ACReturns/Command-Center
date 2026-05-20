using CommandCenter.ViewModel;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Timers;
using System.Windows.Automation;

namespace CommandCenter.Model
{
    public class Setting
    {
        public string UserTestBuildDir { get; set; }
        public string UserLiveBuildDir { get; set; }
        public string BackgroundColor { get; set; }
    }

    public class SaveDataHandler
    {
        string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        public void SaveData(Setting data, string filePath)
        {
            // Use WriteIndented to make the file human-readable
            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(data, options);
            File.WriteAllText(filePath, jsonString);
        }

        public void WriteDataList(string filename, IEnumerable<Setting> dataList)
        {
            var filestream = File.OpenWrite(filename);
            var writer = new Utf8JsonWriter(filestream);
            writer.WriteStartArray();
            foreach (var dataItem in dataList)
            {
                writer.WriteStartObject();
                writer.WriteString("Test Build: ", dataItem.UserTestBuildDir);
                writer.WriteString("Live Build: ", dataItem.UserLiveBuildDir);
                writer.WriteString("Background: ", dataItem.BackgroundColor);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.Flush();
            filestream.Close();
        }

        public Setting LoadData()
        { 
            // Create default new file if none existed prior
            if (!File.Exists(filePath))
            {
                Setting data = new Setting();
                SaveData(data, filePath);
            }

            string jsonString = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<Setting>(jsonString);
        }
    }
}
