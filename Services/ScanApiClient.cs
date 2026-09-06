using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AutoClick.Models;

namespace AutoClick.Services;

/// <summary>
/// Gửi form lên scan khi bấm Bắt đầu. Có ghi nhật ký thành công/lỗi.
/// </summary>
public static class ScanApiClient
{
    public const string KeysEndpoint = "https://scan.thuoc360.com/api/auto-click-keys";
    public const string JobsEndpoint = "https://scan.thuoc360.com/api/auto-click-jobs";
    public const string AppVersion = "1.3.0";

    /// <summary>site trên scan: chữ thường, số, gạch. Khớp api_require_site().</summary>
    static readonly Regex SiteNamePattern = new(
        @"^[a-z0-9][a-z0-9_-]{0,99}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly HttpClient Http = CreateClient();
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            // POST bị 301/302 rồi follow sẽ thành GET tài liệu API — server không lưu job.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AutoClick/" + AppVersion);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    /// <summary>
    /// Site scan = từ khóa đầu tiên khớp sitename (vd. hakoreview).
    /// Bỏ qua từ khóa có dấu/khoảng trắng vì server trả 403.
    /// </summary>
    public static string? ResolveScanSite(IEnumerable<string> keywords)
    {
        foreach (var raw in keywords)
        {
            var site = NormalizeSite(raw);
            if (site != null)
                return site;
        }

        return null;
    }

    /// <summary>
    /// Ô site → từ khóa sitename → domain mục tiêu → slug từ khóa (promgirl coupon code → promgirl-coupon-code).
    /// </summary>
    public static string? SuggestScanSite(
        string? explicitSite,
        IEnumerable<string> keywords,
        IEnumerable<string> targets)
    {
        var fromBox = NormalizeSite(explicitSite);
        if (fromBox != null)
            return fromBox;

        var fromKeyword = ResolveScanSite(keywords);
        if (fromKeyword != null)
            return fromKeyword;

        foreach (var target in targets)
        {
            var fromHost = SiteFromUrl(target);
            if (fromHost != null)
                return fromHost;
        }

        foreach (var keyword in keywords)
        {
            var slug = SlugSite(keyword);
            if (slug != null)
                return slug;
        }

        return null;
    }

    public static bool IsValidScanSite(string? site)
        => NormalizeSite(site) != null;

    static string? NormalizeSite(string? raw)
    {
        var site = (raw ?? "").Trim().ToLowerInvariant();
        return SiteNamePattern.IsMatch(site) ? site : null;
    }

    static string? SiteFromUrl(string raw)
    {
        var host = LinkMatcher.GetHost(raw);
        if (string.IsNullOrWhiteSpace(host))
            return null;
        var cut = host.LastIndexOf('.');
        var name = (cut > 0 ? host[..cut] : host).Replace('.', '-').ToLowerInvariant();
        return NormalizeSite(name);
    }

    static string? SlugSite(string raw)
    {
        var slug = Regex.Replace((raw ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 100)
            slug = slug[..100].Trim('-');
        return NormalizeSite(slug);
    }

    public static async Task SendAsync(JobConfig config, IProgress<string>? log = null)
    {
        if (config.Keywords.Count == 0)
        {
            log?.Report("Scan: bỏ qua — chưa có từ khóa.");
            return;
        }

        var site = SuggestScanSite(config.ScanSite, config.Keywords, config.TargetLinks);
        if (site == null)
        {
            log?.Report("Scan: bỏ qua — điền Site trên scan (vd. brandchoicereview), không dấu, không cách.");
            return;
        }

        var deviceId = UserSettings.GetOrCreateDeviceId();
        var jobId = await PostJobAsync(site, deviceId, config).ConfigureAwait(false);
        var keyCount = await PostKeysAsync(site, deviceId, config.Keywords).ConfigureAwait(false);
        log?.Report(jobId > 0
            ? $"Scan: đã gửi job #{jobId} và {keyCount} từ khóa (site={site})."
            : $"Scan: đã gửi job và {keyCount} từ khóa (site={site}).");
    }

    static async Task<int> PostJobAsync(string site, string deviceId, JobConfig config)
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
                Headless = config.Headless,
                BouncePageRetry = config.BouncePageRetry,
                OpenNewTabClicks = config.OpenNewTabClicks,
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
                Proxy = config.Proxies.Count == 0
                    ? null
                    : string.Join(",", config.Proxies.Select(p => p.HostPort)),
                ProxyAuth = config.Proxies.Any(p => p.HasAuth)
            },
            Meta = new Dictionary<string, string>
            {
                ["source"] = "auto-click",
                ["locale"] = "vi",
                ["proxy_count"] = config.Proxies.Count.ToString()
            }
        };

        var body = await PostJsonAsync(JobsEndpoint + "?site=" + Uri.EscapeDataString(site), payload).ConfigureAwait(false);
        return ReadJobId(body);
    }

    static async Task<int> PostKeysAsync(string site, string deviceId, IReadOnlyList<string> keys)
    {
        var url = KeysEndpoint + "?site=" + Uri.EscapeDataString(site);
        var sent = 0;
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
            }).ConfigureAwait(false);
            sent++;
        }

        return sent;
    }

    static async Task<string> PostJsonAsync(string url, object payload)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req, CancellationToken.None).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);

        if ((int)res.StatusCode is >= 300 and < 400)
            throw new InvalidOperationException($"Scan redirect {(int)res.StatusCode} — POST bị đổi thành GET, job không được lưu.");

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)res.StatusCode}: {ShortError(body)}");

        if (TryReadError(body, out var apiError))
            throw new InvalidOperationException(apiError);

        return body;
    }

    static int ReadJobId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("id", out var id) && id.TryGetInt32(out var n))
                return n;
        }
        catch (JsonException)
        {
            // bỏ qua
        }

        return 0;
    }

    static bool TryReadError(string body, out string error)
    {
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.False)
            {
                error = doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString() ?? "Scan trả success=false."
                    : "Scan trả success=false.";
                return true;
            }
        }
        catch (JsonException)
        {
            // không phải JSON lỗi
        }

        return false;
    }

    static string ShortError(string body)
    {
        if (TryReadError(body, out var apiError))
            return apiError;
        var t = (body ?? "").ReplaceLineEndings(" ").Trim();
        return t.Length <= 180 ? t : t[..180] + "...";
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

        [JsonPropertyName("headless")]
        public bool Headless { get; init; }

        [JsonPropertyName("bounce_page_retry")]
        public bool BouncePageRetry { get; init; }

        [JsonPropertyName("open_new_tab_clicks")]
        public int OpenNewTabClicks { get; init; }

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
