using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AutoClick.Models;
using Microsoft.Playwright;
using Microsoft.Win32;

namespace AutoClick.Services;

/// <summary>
/// Phiên điều khiển 1 cửa sổ Chrome/Edge đã mở.
/// Dispose chỉ ngắt Playwright + đóng tab do app tạo — KHÔNG tắt trình duyệt của bạn.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    public required IPlaywright Playwright { get; init; }
    public required IBrowser Browser { get; init; }
    public required IBrowserContext Context { get; init; }
    public Process? LaunchedProcess { get; init; }
    public int DebugPort { get; init; }
    public bool ConnectedToExisting { get; init; }
    /// <summary>true = app tự mở Chromium, Dispose sẽ đóng hẳn browser.</summary>
    public bool OwnsBrowser { get; init; }
    public List<IPage> OwnedPages { get; } = [];

    public async Task<IPage> NewWorkPageAsync()
    {
        var page = await Context.NewPageAsync();
        OwnedPages.Add(page);
        return page;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var page in OwnedPages)
        {
            try
            {
                if (!page.IsClosed)
                    await page.CloseAsync();
            }
            catch
            {
                // ignore tab close errors
            }
        }

        if (OwnsBrowser)
        {
            try { await Browser.CloseAsync(); } catch { /* Chromium do app mở */ }
        }

        try
        {
            Playwright.Dispose();
        }
        catch
        {
            // CDP: chỉ ngắt kết nối, không Browser.CloseAsync() (sẽ tắt cả Chrome user).
        }
    }
}

/// <summary>
/// Tìm Chrome/Edge trên máy, liệt kê profile, mở bằng đúng User Data.
///
/// Cơ chế: start chrome.exe với --remote-debugging-port rồi Playwright ConnectOverCDP.
/// Không dùng Chromium bundled. Phải đóng Chrome thường trước lần chạy đầu
/// vì Windows khóa thư mục profile.
///
/// Chỗ hay sửa:
///   DefaultDebugPort     — cổng CDP
///   DetectInstalled()    — thêm đường dẫn chrome.exe nếu máy cài lệch chỗ
///   ConnectOrLaunchAsync — thêm/bớt argument Chrome
/// </summary>
public static class BrowserLauncher
{
    /// <summary>Đổi cổng nếu 9333 bị chiếm. Nên khớp JobConfig.DebugPort.</summary>
    public const int DefaultDebugPort = 9333;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>Tìm chrome.exe / msedge.exe. Thêm path vào FirstExisting nếu máy bạn cài portable.</summary>
    public static IReadOnlyList<InstalledBrowser> DetectInstalled()
    {
        var list = new List<InstalledBrowser>
        {
            new()
            {
                Kind = "Playwright Chromium",
                Channel = "chromium",
                ExecutablePath = "(bundled)",
                UserDataDir = "",
                ProcessName = ""
            }
        };

        var chromeExe = FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            ReadAppPath("chrome.exe"));

        if (chromeExe != null)
        {
            list.Add(new InstalledBrowser
            {
                Kind = "Google Chrome",
                Channel = "chrome",
                ExecutablePath = chromeExe,
                UserDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data"),
                ProcessName = "chrome"
            });
        }

        var edgeExe = FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe"),
            ReadAppPath("msedge.exe"));

        if (edgeExe != null)
        {
            list.Add(new InstalledBrowser
            {
                Kind = "Microsoft Edge",
                Channel = "msedge",
                ExecutablePath = edgeExe,
                UserDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data"),
                ProcessName = "msedge"
            });
        }

        return list;
    }

    /// <summary>Đọc thư mục User Data → Default, Profile 1, ... Tên đẹp lấy từ file Local State.</summary>
    public static IReadOnlyList<BrowserProfileInfo> ListProfiles(InstalledBrowser browser)
    {
        if (string.Equals(browser.Channel, "chromium", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(browser.UserDataDir))
        {
            return
            [
                new BrowserProfileInfo { FolderName = "chromium", DisplayName = "Chromium (Playwright)" }
            ];
        }

        var result = new List<BrowserProfileInfo>();
        if (!Directory.Exists(browser.UserDataDir))
            return result;

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localState = Path.Combine(browser.UserDataDir, "Local State");
        if (File.Exists(localState))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(localState));
                if (doc.RootElement.TryGetProperty("profile", out var profile)
                    && profile.TryGetProperty("info_cache", out var cache))
                {
                    foreach (var item in cache.EnumerateObject())
                    {
                        var label = item.Name;
                        if (item.Value.TryGetProperty("name", out var nameEl))
                            label = nameEl.GetString() ?? item.Name;
                        names[item.Name] = $"{label}  ({item.Name})";
                    }
                }
            }
            catch
            {
                // ignore malformed Local State
            }
        }

        foreach (var dir in Directory.GetDirectories(browser.UserDataDir))
        {
            var folder = Path.GetFileName(dir);
            if (folder.Equals("System Profile", StringComparison.OrdinalIgnoreCase)
                || folder.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!File.Exists(Path.Combine(dir, "Preferences")))
                continue;

            result.Add(new BrowserProfileInfo
            {
                FolderName = folder,
                DisplayName = names.TryGetValue(folder, out var pretty) ? pretty : folder
            });
        }

        return result
            .OrderBy(p => p.FolderName.Equals("Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static int CountRunning(InstalledBrowser browser)
        => string.IsNullOrWhiteSpace(browser.ProcessName)
            ? 0
            : Process.GetProcessesByName(browser.ProcessName).Length;

    /// <summary>
    /// Đóng Chrome/Edge: thử CloseMainWindow trước, 1.5s sau mới Kill.
    /// Chỉ gọi khi user bấm nút xác nhận trên form.
    /// </summary>
    public static async Task CloseBrowserAsync(InstalledBrowser browser, IProgress<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(browser.ProcessName)
            || string.Equals(browser.Channel, "chromium", StringComparison.OrdinalIgnoreCase))
        {
            log?.Report("Chromium do Playwright mở sẽ tự đóng khi job xong. Không đụng Chrome trên máy.");
            return;
        }
        var processes = Process.GetProcessesByName(browser.ProcessName);
        if (processes.Length == 0)
        {
            log?.Report($"{browser.Kind} không đang chạy.");
            return;
        }

        log?.Report($"Đang đóng {processes.Length} process {browser.Kind}...");
        foreach (var p in processes)
        {
            try
            {
                if (!p.HasExited)
                    p.CloseMainWindow();
            }
            catch
            {
                // ignore
            }
        }

        await Task.Delay(1500);

        foreach (var p in Process.GetProcessesByName(browser.ProcessName))
        {
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }
        }

        await Task.Delay(800);
        log?.Report(CountRunning(browser) == 0
            ? $"Đã đóng {browser.Kind}."
            : $"{browser.Kind} vẫn còn process — hãy đóng tay trong Task Manager.");
    }

    /// <summary>true nếu đã có Chrome do app mở (cổng debug đang nghe).</summary>
    public static async Task<bool> IsCdpAliveAsync(int port)
    {
        try
        {
            using var res = await Http.GetAsync($"http://127.0.0.1:{port}/json/version");
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Đang dùng Playwright Chromium. Muốn mở lại Chrome/Edge thật: comment return LaunchPlaywright
    /// và bỏ comment return ConnectOrLaunchSystemChromeAsync.
    /// </summary>
    public static async Task<BrowserSession> ConnectOrLaunchAsync(
        InstalledBrowser browser,
        BrowserProfileInfo profile,
        int preferredPort,
        IProgress<string> log,
        CancellationToken ct)
    {
        _ = (browser, profile, preferredPort); // giữ chữ ký để bật lại Chrome thật cho dễ

        // --- Cách cũ: Chrome/Edge thật + profile hàng ngày (phải đóng Chrome trước) ---
        // return await ConnectOrLaunchSystemChromeAsync(browser, profile, preferredPort, log, ct);

        return await LaunchPlaywrightChromiumAsync(log, ct);
    }

    /// <summary>Mở cửa sổ Chromium đi kèm Playwright (không dùng Chrome trên máy).</summary>
    static async Task<BrowserSession> LaunchPlaywrightChromiumAsync(IProgress<string> log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        log.Report("Mở Playwright Chromium (không dùng Chrome/Edge trên máy)...");

        var playwright = await Playwright.CreateAsync();
        IBrowser browser;
        try
        {
            browser = await playwright.Chromium.LaunchAsync(ChromiumLaunchOptions());
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            log.Report("Chưa có Chromium — đang tải (lần đầu có thể mất vài phút)...");
            Microsoft.Playwright.Program.Main(["install", "chromium"]);
            browser = await playwright.Chromium.LaunchAsync(ChromiumLaunchOptions());
        }

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = null,
            Locale = "vi-VN"
        });

        log.Report("Đã mở Chromium.");
        return new BrowserSession
        {
            Playwright = playwright,
            Browser = browser,
            Context = context,
            LaunchedProcess = null,
            DebugPort = 0,
            ConnectedToExisting = false,
            OwnsBrowser = true
        };
    }

    static BrowserTypeLaunchOptions ChromiumLaunchOptions() => new()
    {
        Headless = false, // true = chạy ẩn, không thấy cửa sổ
        SlowMo = 80, // chậm thao tác Playwright một chút cho dễ nhìn
        Args =
        [
            "--disable-blink-features=AutomationControlled",
            "--start-maximized"
        ]
    };

    /// <summary>Cách cũ — mở chrome.exe / msedge.exe với User Data thật qua CDP.</summary>
    static async Task<BrowserSession> ConnectOrLaunchSystemChromeAsync(
        InstalledBrowser browser,
        BrowserProfileInfo profile,
        int preferredPort,
        IProgress<string> log,
        CancellationToken ct)
    {
        var port = preferredPort > 0 ? preferredPort : DefaultDebugPort;

        if (await IsCdpAliveAsync(port))
        {
            log.Report($"Kết nối lại Chrome/Edge đang mở (CDP port {port})...");
            return await ConnectAsync(port, connectedToExisting: true, launched: null, ct);
        }

        var running = CountRunning(browser);
        if (running > 0)
        {
            throw new InvalidOperationException(
                $"{browser.Kind} đang mở ({running} process) nhưng không bật remote debugging.\n\n" +
                "Hãy đóng hết cửa sổ trình duyệt (nút \"Đóng trình duyệt\") rồi bấm Bắt đầu.\n" +
                "Windows không cho hai Chrome dùng chung một profile.");
        }

        if (!File.Exists(browser.ExecutablePath))
            throw new FileNotFoundException("Không tìm thấy file trình duyệt.", browser.ExecutablePath);

        port = GetFreePort(port);
        log.Report($"Mở {browser.Kind} — profile {profile.DisplayName} (port {port})...");

        // Các cờ Chrome — bớt/thêm ở đây nếu cần.
        // --remote-debugging-port : để Playwright điều khiển
        // --user-data-dir         : đúng profile hàng ngày (cookie, login Google)
        // --profile-directory     : Default / Profile 1 / ...
        var args = string.Join(" ",
            $"--remote-debugging-port={port}",
            "--remote-allow-origins=*",
            Quote($"--user-data-dir={browser.UserDataDir}"),
            Quote($"--profile-directory={profile.FolderName}"),
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-blink-features=AutomationControlled",
            "--new-window");

        var psi = new ProcessStartInfo
        {
            FileName = browser.ExecutablePath,
            Arguments = args,
            UseShellExecute = false
        };

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException("Không khởi động được trình duyệt.");

        var deadline = DateTime.UtcNow.AddSeconds(40); // thời gian chờ Chrome sẵn sàng CDP
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsCdpAliveAsync(port))
                break;
            if (process.HasExited)
                throw new InvalidOperationException($"{browser.Kind} thoát ngay sau khi mở (exit {process.ExitCode}). Profile có thể đang bị khóa.");
            await Task.Delay(400, ct);
        }

        if (!await IsCdpAliveAsync(port))
            throw new TimeoutException("Trình duyệt mở nhưng không nhận kết nối điều khiển (CDP). Hãy thử đóng hết rồi chạy lại.");

        log.Report("Đã kết nối tới trình duyệt thường.");
        return await ConnectAsync(port, connectedToExisting: false, launched: process, ct);
    }

    /// <summary>Playwright bám vào Chrome đã mở qua CDP. Không đóng browser khi xong.</summary>
    static async Task<BrowserSession> ConnectAsync(int port, bool connectedToExisting, Process? launched, CancellationToken ct)
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}", new BrowserTypeConnectOverCDPOptions
        {
            Timeout = 30_000
        });

        var context = browser.Contexts.Count > 0
            ? browser.Contexts[0]
            : await browser.NewContextAsync();

        return new BrowserSession
        {
            Playwright = playwright,
            Browser = browser,
            Context = context,
            LaunchedProcess = launched,
            DebugPort = port,
            ConnectedToExisting = connectedToExisting,
            OwnsBrowser = false
        };
    }

    static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    static int GetFreePort(int preferred)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, preferred);
            listener.Start();
            listener.Stop();
            return preferred;
        }
        catch
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    static string? FirstExisting(params string?[] paths)
    {
        foreach (var p in paths)
        {
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                return p;
        }
        return null;
    }

    static string? ReadAppPath(string exeName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName)
                            ?? Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
            var value = key?.GetValue(null) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim('"');
        }
        catch
        {
            return null;
        }
    }
}
