using System.Text.Json;

namespace AutoClick.Services;

/// <summary>Lưu device_id nội bộ — người dùng không thấy trên form.</summary>
public sealed class UserSettings
{
    public string DeviceId { get; set; } = "";

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AutoClick");

    static string FilePath => Path.Combine(Dir, "user.json");

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new UserSettings();
            var raw = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<UserSettings>(raw) ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string GetOrCreateDeviceId()
    {
        var s = Load();
        if (string.IsNullOrWhiteSpace(s.DeviceId))
        {
            s.DeviceId = "device-" + Guid.NewGuid().ToString("N")[..12];
            s.Save();
        }
        return s.DeviceId;
    }

}
