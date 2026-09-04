using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoClick.Models;

/// <summary>
/// Proxy cho Chromium. Ô form để trống = không dùng.
///
/// Định dạng nhận:
///   host:port
///   host:port:user:pass
///   user:pass@host:port
///   user:pass:host:port
///   http://host:port
///   socks5://host:port
///   http://user:pass@host:port
/// </summary>
public sealed class BrowserProxy
{
    static readonly Regex SchemePrefix = new(
        @"^(?<scheme>https?|socks5h?|socks)://",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Giá trị Playwright, ví dụ http://1.2.3.4:8080 hoặc socks5://1.2.3.4:1080.</summary>
    public required string Server { get; init; }

    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>host:port — dùng log / API, không kèm mật khẩu.</summary>
    public required string HostPort { get; init; }

    public bool HasAuth => !string.IsNullOrEmpty(Username);

    /// <summary>null nếu ô trống. Throw nếu cú pháp sai.</summary>
    public static BrowserProxy? Parse(string? raw)
    {
        var text = (raw ?? "").Trim().Trim('"', '\'');
        if (text.Length == 0)
            return null;

        if (!TryParse(text, out var proxy, out var error) || proxy == null)
            throw new InvalidOperationException(error ?? "Proxy không hợp lệ.");

        return proxy;
    }

    public static bool TryParse(string text, out BrowserProxy? proxy, out string? error)
    {
        proxy = null;
        error = null;

        var scheme = "http";
        var match = SchemePrefix.Match(text);
        if (match.Success)
        {
            scheme = NormalizeScheme(match.Groups["scheme"].Value);
            text = text[match.Length..];
        }

        string? user = null;
        string? pass = null;
        string host;
        int port;

        var at = text.LastIndexOf('@');
        if (TrySplitHostPortAndOptionalAuth(text, out host, out port, out user, out pass, out var colonError))
        {
            // host:port hoặc host:port:user:pass — kể cả khi mật khẩu có ký tự @
        }
        else if (at >= 0)
        {
            if (!TrySplitUserPass(text[..at], out user, out pass, out error))
                return false;
            if (!TrySplitHostPort(text[(at + 1)..], out host, out port, out error))
                return false;
        }
        else
        {
            error = colonError;
            return false;
        }

        if (string.IsNullOrWhiteSpace(host) || host.Contains('/') || host.Contains(' '))
        {
            error = "Host proxy không hợp lệ.";
            return false;
        }

        user = string.IsNullOrEmpty(user) ? null : Uri.UnescapeDataString(user);
        pass = pass == null ? null : Uri.UnescapeDataString(pass);

        proxy = new BrowserProxy
        {
            Server = $"{scheme}://{host}:{port}",
            Username = user,
            Password = pass,
            HostPort = $"{host}:{port}"
        };
        return true;
    }

    static string NormalizeScheme(string scheme)
    {
        scheme = scheme.ToLowerInvariant();
        return scheme is "socks" or "socks5h" ? "socks5" : scheme;
    }

    static bool TrySplitUserPass(string cred, out string? user, out string? pass, out string? error)
    {
        user = null;
        pass = null;
        error = null;
        cred = cred.Trim();
        if (cred.Length == 0)
        {
            error = "Thiếu user/pass trước ký tự @.";
            return false;
        }

        var colon = cred.IndexOf(':');
        if (colon < 0)
        {
            user = cred;
            pass = "";
            return true;
        }

        user = cred[..colon];
        pass = cred[(colon + 1)..];
        return true;
    }

    static bool TrySplitHostPort(string hostPort, out string host, out int port, out string? error)
    {
        host = "";
        port = 0;
        error = null;
        hostPort = hostPort.Trim();

        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf("]:", StringComparison.Ordinal);
            if (close <= 1)
            {
                error = "Proxy IPv6 phải dạng [địa_chỉ]:cổng.";
                return false;
            }

            host = hostPort[1..close];
            return TryReadPort(hostPort[(close + 2)..], out port, out error);
        }

        var colon = hostPort.LastIndexOf(':');
        if (colon <= 0 || colon == hostPort.Length - 1)
        {
            error = "Proxy phải có dạng host:port.";
            return false;
        }

        host = hostPort[..colon].Trim();
        return TryReadPort(hostPort[(colon + 1)..], out port, out error);
    }

    static bool TrySplitHostPortAndOptionalAuth(
        string text,
        out string host,
        out int port,
        out string? user,
        out string? pass,
        out string? error)
    {
        host = "";
        port = 0;
        user = null;
        pass = null;
        error = null;

        var parts = text.Split(':');
        if (parts.Length == 2)
        {
            host = parts[0].Trim();
            return TryReadPort(parts[1], out port, out error);
        }

        // host:port:user:pass (pass có thể chứa dấu :)
        if (parts.Length >= 4 && LooksLikeHost(parts[0]) && IsPort(parts[1]))
        {
            host = parts[0].Trim();
            if (!TryReadPort(parts[1], out port, out error))
                return false;
            user = parts[2];
            pass = string.Join(':', parts.Skip(3));
            return true;
        }

        // user:pass:host:port
        if (parts.Length == 4 && IsPort(parts[3]))
        {
            user = parts[0];
            pass = parts[1];
            host = parts[2].Trim();
            return TryReadPort(parts[3], out port, out error);
        }

        error = "Proxy không hợp lệ. Dùng host:port hoặc host:port:user:pass.";
        return false;
    }

    static bool TryReadPort(string raw, out int port, out string? error)
    {
        error = null;
        if (!int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port is < 1 or > 65535)
        {
            error = "Cổng proxy phải từ 1 đến 65535.";
            port = 0;
            return false;
        }

        return true;
    }

    static bool IsPort(string raw)
        => int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
           && port is >= 1 and <= 65535;

    static bool LooksLikeHost(string raw)
    {
        raw = raw.Trim();
        return raw.Length > 0 && !raw.Contains('/') && !raw.Contains('@') && !raw.Contains(' ');
    }
}
