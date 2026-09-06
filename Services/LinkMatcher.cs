using AutoClick.Models;

namespace AutoClick.Services;

/// <summary>
/// So khớp URL kết quả Google với danh sách bạn nhập.
/// Đổi hành vi Contains/Domain/Exact ở đây — ComboBox trên form chỉ chọn mode.
/// </summary>
public static class LinkMatcher
{
    public static bool IsMatch(string resultUrl, string target, MatchMode mode)
    {
        if (string.IsNullOrWhiteSpace(resultUrl) || string.IsNullOrWhiteSpace(target))
            return false;

        resultUrl = UnwrapGoogleHref(resultUrl);
        target = UnwrapGoogleHref(target);

        // http://dienmayxanh.com/ (trang chủ / chỉ domain) → khớp mọi bài trên cùng site.
        if (IsDomainOnlyTarget(target) && SameSite(resultUrl, target))
            return true;

        return mode switch
        {
            MatchMode.Domain => SameSite(resultUrl, target),
            MatchMode.Exact => string.Equals(NormalizeUrl(resultUrl), NormalizeUrl(EnsureScheme(target)), StringComparison.OrdinalIgnoreCase),
            _ => ContainsMatch(resultUrl, target) || SameSite(resultUrl, target)
        };
    }

    public static bool MatchesAny(string resultUrl, IEnumerable<string> targets, MatchMode mode)
        => targets.Any(t => IsMatch(resultUrl, t, mode));

    public static string? FindMatch(IEnumerable<string> resultUrls, IEnumerable<string> targets, MatchMode mode)
        => resultUrls.FirstOrDefault(url => MatchesAny(url, targets, mode));

    /// <summary>
    /// Mỗi URL SERP khớp tối đa 1 lần; mỗi target còn lại chỉ được lấy URL đầu tiên thấy trên trang.
    /// </summary>
    public static List<string> FindMatches(
        IEnumerable<string> resultUrls,
        IEnumerable<string> remainingTargets,
        MatchMode mode)
    {
        var remaining = remainingTargets.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var matches = new List<string>();
        foreach (var url in resultUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
                continue;
            var hits = remaining.Where(t => IsMatch(url, t, mode)).ToList();
            if (hits.Count == 0)
                continue;
            matches.Add(url);
            foreach (var hit in hits)
                remaining.Remove(hit);
        }

        return matches;
    }

    public static void RemoveHitTargets(List<string> remaining, string url, MatchMode mode)
        => remaining.RemoveAll(t => IsMatch(url, t, mode));

    /// <summary>Contains: bỏ http/www rồi xem chuỗi này có nằm trong chuỗi kia.</summary>
    static bool ContainsMatch(string resultUrl, string target)
    {
        var resultKey = ToKey(resultUrl);
        var targetKey = ToKey(target);
        if (resultKey.Length == 0 || targetKey.Length == 0)
            return false;
        return resultKey.Contains(targetKey, StringComparison.OrdinalIgnoreCase)
               || targetKey.Contains(resultKey, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// true nếu target chỉ là domain hoặc trang chủ (path rỗng),
    /// ví dụ http://dienmayxanh.com/ hoặc dienmayxanh.com
    /// </summary>
    public static bool IsDomainOnlyTarget(string target)
    {
        target = (target ?? "").Trim();
        if (target.Length == 0)
            return false;
        try
        {
            var url = EnsureScheme(target);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return !target.Contains('/');
            return string.IsNullOrEmpty(uri.AbsolutePath.Trim('/'));
        }
        catch
        {
            return !target.Contains('/');
        }
    }

    /// <summary>Cùng site: dienmayxanh.com khớp www.dienmayxanh.com và subdomain.</summary>
    public static bool SameSite(string resultUrl, string target)
    {
        var resultHost = GetHost(resultUrl);
        var targetHost = GetHost(target.Contains("://", StringComparison.Ordinal)
            ? target
            : "https://" + target.Trim().TrimStart('/'));
        if (string.IsNullOrEmpty(resultHost) || string.IsNullOrEmpty(targetHost))
            return false;
        return resultHost.Equals(targetHost, StringComparison.OrdinalIgnoreCase)
               || resultHost.EndsWith("." + targetHost, StringComparison.OrdinalIgnoreCase)
               || targetHost.EndsWith("." + resultHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Domain: chỉ so host, bỏ www. example.com khớp www.example.com/abc.</summary>
    static bool HostsEqual(string resultUrl, string target) => SameSite(resultUrl, target);

    /// <summary>Chuẩn hóa để so Exact: lowercase, bỏ www, #fragment, / cuối, port mặc định.</summary>
    public static string NormalizeUrl(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0)
            return "";
        url = EnsureScheme(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return ToKey(url);
        var builder = new UriBuilder(uri)
        {
            Fragment = "",
            Host = StripWww(uri.Host)
        };
        if ((builder.Scheme == "http" && builder.Port == 80) || (builder.Scheme == "https" && builder.Port == 443))
            builder.Port = -1;
        var result = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return result.ToLowerInvariant();
    }

    /// <summary>Key dạng vnexpress.net/path — dùng cho Contains.</summary>
    public static string ToKey(string value)
    {
        var s = (value ?? "").Trim();
        if (s.Length == 0)
            return "";
        s = s.Replace("\\", "/");
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            s = s[7..];
        else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = s[8..];
        s = StripWww(s);
        var hash = s.IndexOf('#');
        if (hash >= 0)
            s = s[..hash];
        var query = s.IndexOf('?');
        if (query >= 0)
            s = s[..query];
        return s.Trim().TrimEnd('/').ToLowerInvariant();
    }

    public static string StripWww(string host)
    {
        host = (host ?? "").Trim().TrimEnd('.');
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    public static string GetHost(string url)
    {
        url = EnsureScheme((url ?? "").Trim());
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? StripWww(uri.Host) : "";
    }

    public static string EnsureScheme(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0)
            return url;
        if (url.StartsWith("//"))
            return "https:" + url;
        if (!url.Contains("://"))
            return "https://" + url.TrimStart('/');
        return url;
    }

    /// <summary>
    /// Google hay bọc link thật trong /url?q=... hoặc adurl= (quảng cáo /aclk).
    /// Lấy URL đích trước khi so khớp / click.
    /// </summary>
    public static string UnwrapGoogleHref(string href)
        => UnwrapGoogleHref(href, 0);

    static string UnwrapGoogleHref(string href, int depth)
    {
        if (string.IsNullOrWhiteSpace(href) || depth > 3)
            return href;

        href = href.Trim();
        try
        {
            var uri = href.StartsWith('/')
                ? new Uri(new Uri("https://www.google.com"), href)
                : new Uri(EnsureScheme(href));
            if (!IsGoogleRedirectHost(uri.Host) || !LooksLikeGoogleRedirect(uri.AbsolutePath))
                return href;

            var dest = FirstHttpQueryValue(uri.Query, "adurl", "q", "url");
            if (string.IsNullOrWhiteSpace(dest))
                return href;
            return UnwrapGoogleHref(dest, depth + 1);
        }
        catch
        {
            return href;
        }
    }

    static bool IsGoogleRedirectHost(string host)
    {
        host = host.ToLowerInvariant();
        return host.Contains("google.")
               || host.Contains("googleadservices.com")
               || host.Contains("googlesyndication.com")
               || host.Contains("doubleclick.net");
    }

    static bool LooksLikeGoogleRedirect(string path)
    {
        path = path.ToLowerInvariant();
        return path.Equals("/url", StringComparison.Ordinal)
               || path.StartsWith("/url", StringComparison.Ordinal)
               || path.Contains("/aclk", StringComparison.Ordinal)
               || path.Contains("/pagead/", StringComparison.Ordinal);
    }

    static string? FirstHttpQueryValue(string query, params string[] keys)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = Uri.UnescapeDataString(part[..eq]);
            if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                continue;
            var dest = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            if (dest.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || dest.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return dest;
        }

        return null;
    }

    /// <summary>true nếu vẫn đang ở trang kết quả Google (chưa sang trang đích).</summary>
    public static bool IsGoogleResultsUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var host = uri.Host.ToLowerInvariant();
        if (!host.Contains("google."))
            return false;
        var path = uri.AbsolutePath.ToLowerInvariant();
        return path is "/" or "/search" || path.StartsWith("/search") || path.StartsWith("/webhp");
    }

    /// <summary>true nếu là link nội bộ Google (ads, cache...) — không đưa vào danh sách khớp.</summary>
    public static bool IsGoogleInternal(string url)
    {
        url = UnwrapGoogleHref(url);
        if (!Uri.TryCreate(EnsureScheme(url), UriKind.Absolute, out var uri))
            return false;
        var host = uri.Host.ToLowerInvariant();
        return host.Contains("google.")
               || host.EndsWith(".google")
               || host.Contains("gstatic.com")
               || host.Contains("googleusercontent.com")
               || host.Contains("googleadservices.com")
               || host.Contains("googlesyndication.com")
               || host.Contains("g.doubleclick.net")
               || host.Contains("youtube.com") && uri.AbsolutePath.StartsWith("/redirect", StringComparison.OrdinalIgnoreCase);
    }
}
