using System.IO;
using System.Text.Json;

namespace IslandApp
{
    // Настройки острова, сохраняются в %AppData%\IslandApp\settings.json
    public class AppSettings
    {
        public bool EqualizerEnabled { get; set; } = true;
        public bool AnimatedGradient { get; set; } = true;
        public bool ShowBattery { get; set; } = false;
        public bool HideInFullscreen { get; set; } = true;
        public bool MinimalMode { get; set; } = false;

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IslandApp", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}