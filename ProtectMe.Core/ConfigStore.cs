using System.Text.Json;

namespace ProtectMe.Core;

public static class ConfigStore
{
    public static readonly string DataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProtectMe");

    public static readonly string ConfigPath = Path.Combine(DataDirectory, "config.json");

    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static ProtectConfig Load()
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                if (!File.Exists(ConfigPath))
                {
                    var initial = new ProtectConfig();
                    Save(initial);
                    return initial;
                }

                string json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<ProtectConfig>(json, JsonOptions) ?? new ProtectConfig();
            }
            catch
            {
                return new ProtectConfig();
            }
        }
    }

    public static void Save(ProtectConfig config)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(DataDirectory);
            string json = JsonSerializer.Serialize(config, JsonOptions);
            string tempPath = ConfigPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
    }
}
