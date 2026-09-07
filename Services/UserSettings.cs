using System.Text.Json;

namespace AutoClick.Services;

/// <summary>Lưu device_id và snapshot form vào %AppData%\AutoClick\user.json.</summary>
public sealed class UserSettings
{
    public string DeviceId { get; set; } = "";
    public bool FormSaved { get; set; }
    public string? BrowserKind { get; set; }
    public string Proxy { get; set; } = "";
    public string Keywords { get; set; } = "";
    public string Targets { get; set; } = "";
    public string ScanSite { get; set; } = "";
    public int MaxGooglePages { get; set; } = 3;
    public int DelayMs { get; set; } = 1500;
    public int OpenNewTabClicks { get; set; } = 1;
    public int ClickIntervalMs { get; set; } = 200;
    public bool AutoRepeat { get; set; }
    public bool Headless { get; set; }
    public bool BouncePageRetry { get; set; }

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
