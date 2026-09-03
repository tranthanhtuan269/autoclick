using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AutoClick.Models;

namespace AutoClick.Services;

/// <summary>
/// Ghi kết quả mỗi lần chạy vào thư mục run_yyyyMMdd_HHmmss:
///   results.json  results.csv  html/*.html
/// Đổi tên file / cột CSV ở các hàm Write*.
/// </summary>
public static class ResultWriter
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // giữ nguyên tiếng Việt trong JSON, không \uXXXX
    };

    public static string CreateRunFolder(string outputDirectory)
    {
        // Đổi format ngày giờ ở đây nếu muốn tên thư mục khác.
        var dir = Path.Combine(outputDirectory, "run_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "html"));
        return dir;
    }

    public static void WriteHtml(string runFolder, CrawlResult result)
    {
        if (string.IsNullOrEmpty(result.Html))
            return;
        var name = Sanitize($"{result.Keyword}_{LinkMatcher.GetHost(result.FinalUrl ?? result.MatchedUrl ?? "page")}") + ".html";
        var path = Path.Combine(runFolder, "html", name);
        File.WriteAllText(path, result.Html, Encoding.UTF8);
        result.HtmlPath = path;
    }

    public static void WriteJson(string runFolder, IReadOnlyList<CrawlResult> results)
    {
        var payload = results.Select(r => new
        {
            r.Keyword,
            r.Found,
            r.MatchedUrl,
            r.FinalUrl,
            r.Title,
            r.Text,
            r.HtmlPath,
            r.Error,
            Fields = r.Fields
        });
        File.WriteAllText(Path.Combine(runFolder, "results.json"), JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
    }

    public static void WriteCsv(string runFolder, IReadOnlyList<CrawlResult> results)
    {
        var extra = results.SelectMany(r => r.Fields.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder();
        var headers = new List<string> { "Keyword", "Found", "MatchedUrl", "FinalUrl", "Title", "Error", "HtmlPath" };
        headers.AddRange(extra);
        sb.AppendLine(string.Join(",", headers.Select(Csv)));

        foreach (var r in results)
        {
            var cells = new List<string>
            {
                Csv(r.Keyword),
                Csv(r.Found ? "yes" : "no"),
                Csv(r.MatchedUrl),
                Csv(r.FinalUrl),
                Csv(r.Title),
                Csv(r.Error),
                Csv(r.HtmlPath)
            };
            foreach (var key in extra)
            {
                r.Fields.TryGetValue(key, out var value);
                cells.Add(Csv(value));
            }
            sb.AppendLine(string.Join(",", cells));
        }

        File.WriteAllText(Path.Combine(runFolder, "results.csv"), sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        // BOM UTF-8 để Excel mở tiếng Việt không lỗi font.
    }

    static string Csv(string? value)
    {
        value ??= "";
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        if (s.Length > 80)
            s = s[..80];
        return string.IsNullOrWhiteSpace(s) ? "page" : s;
    }
}
