using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoClick.Models;
using Microsoft.Playwright;
using Microsoft.Win32;

namespace AutoClick.Services;

/// <summary>
/// Phiên điều khiển cửa sổ trình duyệt do app mở.
/// Dispose đóng hết tab rồi tắt browser.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    public required IPlaywright Playwright { get; init; }
    public required IBrowser Browser { get; init; }
    public required IBrowserContext Context { get; init; }
    public Process? LaunchedProcess { get; init; }
    public int DebugPort { get; init; }
    public bool ConnectedToExisting { get; init; }
    /// <summary>true = app tự mở cửa sổ này, Dispose sẽ đóng đúng instance đó (không đụng Chrome hàng ngày).</summary>
    public bool OwnsBrowser { get; init; }
    /// <summary>PID Chrome/Edge do app mở — dùng để đóng đúng process, không kill hết chrome.exe.</summary>
    public IReadOnlyList<int> OwnedProcessIds { get; init; } = [];
    public string? IsolatedUserDataDir { get; init; }
    public bool Headless { get; init; }
    public List<IPage> OwnedPages { get; } = [];

    int _disposed;

    public async Task<IPage> NewWorkPageAsync()
    {
        var page = Context.Pages.FirstOrDefault(p => !p.IsClosed);
        if (page == null || OwnedPages.Count > 0)
            page = await Context.NewPageAsync();
        OwnedPages.Add(page);
        try { await page.SetViewportSizeAsync(BrowserLauncher.MiniContentWidth, BrowserLauncher.MiniContentHeight); } catch { /* ignore */ }
        if (!Headless)
            await ApplyMiniWindowAsync(page);
        return page;
    }

    /// <summary>Ép cửa sổ nửa trái màn hình. Gọi lại vài lần vì Chrome hay khôi phục kích thước cũ.</summary>
    public async Task ApplyMiniWindowAsync(IPage page)
    {
        for (var i = 0; i < 4; i++)
        {
            BrowserLauncher.ForceMiniNativeWindow(OwnedProcessIds);
            await TryCdpMiniWindowAsync(page);
            try { await page.SetViewportSizeAsync(BrowserLauncher.MiniContentWidth, BrowserLauncher.MiniContentHeight); } catch { /* ignore */ }
            await page.WaitForTimeoutAsync(220);
        }
    }

    async Task TryCdpMiniWindowAsync(IPage page)
    {
        try
        {
            var cdp = await Context.NewCDPSessionAsync(page);
            var response = await cdp.SendAsync("Browser.getWindowForTarget");
            if (response is { } json && json.ValueKind == JsonValueKind.Object)
            {
                var windowId = json.GetProperty("windowId").GetInt32();
                // Phải bỏ fullscreen/maximized trước, rồi mới set size.
                await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
                {
                    ["windowId"] = windowId,
                    ["bounds"] = new Dictionary<string, object> { ["windowState"] = "normal" }
                });
                await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
                {
                    ["windowId"] = windowId,
                    ["bounds"] = new Dictionary<string, object>
                    {
                        ["windowState"] = "normal",
                        ["left"] = BrowserLauncher.LeftHalfBounds().Left,
                        ["top"] = BrowserLauncher.LeftHalfBounds().Top,
                        ["width"] = BrowserLauncher.MiniWindowWidth,
                        ["height"] = BrowserLauncher.MiniWindowHeight
                    }
                });
            }

            await cdp.SendAsync("Emulation.setDeviceMetricsOverride", new Dictionary<string, object>
            {
                ["width"] = BrowserLauncher.MiniContentWidth,
                ["height"] = BrowserLauncher.MiniContentHeight,
                ["deviceScaleFactor"] = 1,
                ["mobile"] = false
            });
            try { await cdp.DetachAsync(); } catch { /* ignore */ }
        }
        catch
        {
            try
            {
                await page.EvaluateAsync(
                    $"() => {{ try {{ document.exitFullscreen?.(); }} catch {{}} window.moveTo({BrowserLauncher.LeftHalfBounds().Left}, {BrowserLauncher.LeftHalfBounds().Top}); window.resizeTo({BrowserLauncher.MiniWindowWidth}, {BrowserLauncher.MiniWindowHeight}); }}");
            }
            catch
            {
                // Win32 vẫn xử lý
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

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

        try { await Context.CloseAsync(); } catch { /* đã đóng */ }
        try { await Browser.CloseAsync(); } catch { /* tắt cửa sổ app */ }

        BrowserLauncher.KillOwnedProcesses(LaunchedProcess, OwnedProcessIds);
        BrowserLauncher.ClearSavedLaunch();
        VisibleMouse.SetOwnerProcessIds(null);

        try
        {
            Playwright.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}

/// <summary>
/// Tìm Chrome/Edge trên máy, liệt kê profile, mở cửa sổ riêng song song.
///
/// Chrome/Edge: dùng --user-data-dir riêng trong %LOCALAPPDATA%\AutoClick\chrome-profiles
/// nên không cần đóng Chrome hàng ngày (Windows khóa cả thư mục User Data, không phải từng profile).
/// Playwright Chromium: bundled, không đụng chrome.exe trên máy.
///
/// Chỗ hay sửa:
///   DefaultDebugPort     — cổng CDP
///   DetectInstalled()    — thêm đường dẫn chrome.exe nếu máy cài lệch chỗ
///   ConnectOrLaunchAsync — thêm/bớt argument Chrome; proxy từ form
/// </summary>
public static class BrowserLauncher
{
    /// <summary>Đổi cổng nếu 9333 bị chiếm. Nên khớp JobConfig.DebugPort.</summary>
    public const int DefaultDebugPort = 9333;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>Nửa trái màn hình làm việc (taskbar không đè lên).</summary>
    public static Rectangle LeftHalfBounds()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        return new Rectangle(wa.Left, wa.Top, Math.Max(480, wa.Width / 2), wa.Height);
    }

    public static int MiniWindowWidth => LeftHalfBounds().Width;
    public static int MiniWindowHeight => LeftHalfBounds().Height;
    public static int MiniContentWidth => LeftHalfBounds().Width;
    public static int MiniContentHeight => Math.Max(400, LeftHalfBounds().Height - 88);

    static ViewportSize MiniViewport => new() { Width = MiniContentWidth, Height = MiniContentHeight };

    static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoClick");

    static string IsolatedProfilesRoot => Path.Combine(AppDataDir, "chrome-profiles");
    static string LaunchSessionPath => Path.Combine(AppDataDir, "launch-session.json");

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
    /// Chỉ đóng cửa sổ do AutoClick mở. Không kill Chrome/Edge hàng ngày.
    /// </summary>
    public static async Task CloseBrowserAsync(InstalledBrowser browser, IProgress<string>? log = null)
    {
        if (string.Equals(browser.Channel, "chromium", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(browser.ProcessName))
        {
            log?.Report("Chromium do Playwright mở sẽ tự đóng khi job xong. Không đụng Chrome trên máy.");
            return;
        }

        var session = ReadLaunchSession();
        var pids = session?.Pids ?? [];
        var closedViaCdp = false;

        if (session?.Port > 0 && await IsCdpAliveAsync(session.Port))
        {
            try
            {
                var existing = await ConnectAsync(
                    session.Port, connectedToExisting: true, launched: null, CancellationToken.None, pids, session.UserDataDir);
                await existing.DisposeAsync();
                closedViaCdp = true;
            }
            catch
            {
                // đóng bằng PID bên dưới
            }
        }

        if (!closedViaCdp)
            KillOwnedProcesses(null, pids);
        ClearLaunchSession();
        await Task.Delay(400);

        if (!closedViaCdp && pids.Count == 0)
        {
            log?.Report("Không thấy cửa sổ AutoClick đang mở. Chrome hàng ngày được giữ nguyên.");
            return;
        }

        var still = pids.Count(PidStillRunning);
        log?.Report(still == 0
            ? "Đã đóng cửa sổ AutoClick. Chrome hàng ngày được giữ nguyên."
            : "Một số process AutoClick vẫn còn — đóng tay cửa sổ Chrome do app mở (đừng đóng Chrome đang dùng).");
    }

    public static IReadOnlyList<int> ReadOwnedPids()
        => ReadLaunchSession()?.Pids ?? [];

    public static string IsolatedUserDataDir(InstalledBrowser browser, BrowserProfileInfo profile)
    {
        var safe = string.Join("_", profile.FolderName.Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(safe))
            safe = "Default";
        return Path.Combine(IsolatedProfilesRoot, browser.Channel, safe);
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
    /// Chromium bundled: cửa sổ riêng. Chrome/Edge: user-data-dir riêng, chạy song song profile hàng ngày.
    /// </summary>
    public static async Task<BrowserSession> ConnectOrLaunchAsync(
        InstalledBrowser browser,
        BrowserProfileInfo profile,
        int preferredPort,
        IProgress<string> log,
        CancellationToken ct,
        BrowserProxy? proxy = null,
        bool headless = false)
    {
        if (string.Equals(browser.Channel, "chromium", StringComparison.OrdinalIgnoreCase))
            return await LaunchPlaywrightChromiumAsync(log, ct, proxy, headless);

        return await LaunchIsolatedSystemChromeAsync(browser, profile, preferredPort, log, ct, proxy, headless);
    }

    /// <summary>Mở cửa sổ Chromium đi kèm Playwright (không dùng Chrome trên máy).</summary>
    static async Task<BrowserSession> LaunchPlaywrightChromiumAsync(
        IProgress<string> log,
        CancellationToken ct,
        BrowserProxy? proxy,
        bool headless)
    {
        ct.ThrowIfCancellationRequested();
        log.Report(headless
            ? "Chạy nền — Playwright Chromium ẩn, không hiện cửa sổ."
            : "Mở Playwright Chromium (không dùng Chrome/Edge trên máy)...");
        LogProxy(log, proxy);

        var playwright = await Playwright.CreateAsync();
        IBrowser browser;
        try
        {
            browser = await playwright.Chromium.LaunchAsync(ChromiumLaunchOptions(proxy, headless));
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            log.Report("Chưa có Chromium — đang tải (lần đầu có thể mất vài phút)...");
            Microsoft.Playwright.Program.Main(["install", "chromium"]);
            browser = await playwright.Chromium.LaunchAsync(ChromiumLaunchOptions(proxy, headless));
        }

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = MiniViewport,
            Locale = UiLocale(proxy),
            ExtraHTTPHeaders = LocaleHeaders(proxy),
            Proxy = ToPlaywrightProxy(proxy)
        });

        var owned = headless
            ? []
            : CapturePidsByExecutableHint("chrome", ["ms-playwright", "chromium-", "chrome-win"]);
        VisibleMouse.SetOwnerProcessIds(owned);
        WriteLaunchSession(0, null, owned, "chrome");

        log.Report(headless ? "Đã chạy Chromium nền." : "Đã mở Chromium.");
        return new BrowserSession
        {
            Playwright = playwright,
            Browser = browser,
            Context = context,
            LaunchedProcess = null,
            DebugPort = 0,
            ConnectedToExisting = false,
            OwnsBrowser = true,
            OwnedProcessIds = owned,
            Headless = headless
        };
    }

    static BrowserTypeLaunchOptions ChromiumLaunchOptions(BrowserProxy? proxy, bool headless)
    {
        var args = new List<string> { "--disable-blink-features=AutomationControlled" };
        AddProxyHardeningArgs(args, proxy);
        if (!headless)
            AddMiniWindowArgs(args);

        return new BrowserTypeLaunchOptions
        {
            Headless = headless,
            SlowMo = headless ? 0 : 80,
            Args = args,
            Proxy = ToPlaywrightProxy(proxy)
        };
    }

    static void AddMiniWindowArgs(List<string> args)
    {
        var box = LeftHalfBounds();
        args.Add($"--window-size={box.Width},{box.Height}");
        args.Add($"--window-position={box.Left},{box.Top}");
        args.Add("--force-device-scale-factor=1");
    }

    const int SwRestore = 9;
    const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>Ép HWND Chrome về cửa sổ mini — chắc hơn CDP khi profile nhớ fullscreen.</summary>
    public static void ForceMiniNativeWindow(IReadOnlyList<int>? ownedPids)
    {
        var hwnd = FindOwnedChromeWindow(ownedPids);
        if (hwnd == IntPtr.Zero)
            return;
        var box = LeftHalfBounds();
        ShowWindow(hwnd, SwRestore);
        SetWindowPos(hwnd, IntPtr.Zero, box.Left, box.Top, box.Width, box.Height, SwpShowWindow);
    }

    static IntPtr FindOwnedChromeWindow(IReadOnlyList<int>? ownedPids)
    {
        var found = IntPtr.Zero;
        var enumTop = new EnumWindowsProc((h, _) =>
        {
            if (!IsWindowVisible(h))
                return true;
            var cls = new StringBuilder(256);
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() != "Chrome_WidgetWin_1")
                return true;
            GetWindowThreadProcessId(h, out var pid);
            if (ownedPids is not { Count: > 0 } || !ownedPids.Contains((int)pid))
                return true;
            found = h;
            return false;
        });
        EnumWindows(enumTop, IntPtr.Zero);
        GC.KeepAlive(enumTop);
        return found;
    }

    /// <summary>
    /// Mở chrome.exe / msedge.exe với user-data-dir riêng — song song với Chrome đang mở.
    /// Mỗi lần chạy mở đúng --profile-directory đã chọn và gắn lại proxy (không tái dùng phiên cũ).
    /// </summary>
    static async Task<BrowserSession> LaunchIsolatedSystemChromeAsync(
        InstalledBrowser browser,
        BrowserProfileInfo profile,
        int preferredPort,
        IProgress<string> log,
        CancellationToken ct,
        BrowserProxy? proxy,
        bool headless)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(browser.ExecutablePath))
            throw new FileNotFoundException("Không tìm thấy file trình duyệt.", browser.ExecutablePath);

        var userDataDir = IsolatedUserDataDir(browser, profile);
        var profileFolder = string.IsNullOrWhiteSpace(profile.FolderName) ? "Default" : profile.FolderName;
        Directory.CreateDirectory(userDataDir);

        CloseLeftoverAutoClickChrome(browser, userDataDir, log);
        ClearStaleLocks(userDataDir);
        PrepareIsolatedProfile(browser, profile, userDataDir, profileFolder, proxy, log);

        var port = GetFreePort(preferredPort > 0 ? preferredPort : DefaultDebugPort);
        log.Report(headless
            ? $"Chạy nền {browser.Kind} — profile {profile.DisplayName} ({profileFolder})."
            : $"Mở {browser.Kind} — profile {profile.DisplayName} ({profileFolder}), cửa sổ riêng...");
        LogProxy(log, proxy);

        var playwright = await Playwright.CreateAsync();
        IBrowserContext? context = null;

        foreach (var attempt in new[] { "channel", "exe", "fresh" })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (attempt == "fresh")
                {
                    log.Report("Profile riêng lỗi — tạo profile trống rồi mở lại (proxy vẫn gắn).");
                    ResetIsolatedProfile(userDataDir, profileFolder);
                    WriteLocalState(userDataDir, profileFolder);
                }

                context = await playwright.Chromium.LaunchPersistentContextAsync(
                    userDataDir,
                    PersistentChromeOptions(browser, proxy, port, headless, profileFolder, useChannel: attempt != "exe"));
                break;
            }
            catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException)
            {
                log.Report($"Mở {browser.Kind} thất bại ({attempt}): {ex.Message}");
                ClearStaleLocks(userDataDir);
            }
        }

        if (context == null)
        {
            log.Report("Chuyển sang Chrome kênh Playwright (không giữ cookie profile) để proxy vẫn chạy.");
            return await LaunchSystemChromeEphemeralAsync(playwright, browser, proxy, headless, log);
        }

        var chrome = context.Browser
                     ?? throw new InvalidOperationException("Playwright không trả về browser sau khi mở Chrome.");

        await Task.Delay(400, ct);
        port = ReadDevToolsPort(userDataDir, port);
        var owned = CapturePidsByCommandLine(browser.ProcessName, userDataDir);
        VisibleMouse.SetOwnerProcessIds(owned);
        WriteLaunchSession(port, userDataDir, owned, browser.ProcessName);

        var running = CountRunning(browser);
        log.Report(headless
            ? $"Đã chạy {browser.Kind} nền — profile {profileFolder}. Process {browser.Kind} trên máy: {running}."
            : $"Đã mở {browser.Kind} profile {profileFolder}. Process {browser.Kind} trên máy: {running} (gồm cửa sổ hàng ngày).");

        return new BrowserSession
        {
            Playwright = playwright,
            Browser = chrome,
            Context = context,
            LaunchedProcess = null,
            DebugPort = port,
            ConnectedToExisting = false,
            OwnsBrowser = true,
            OwnedProcessIds = owned,
            IsolatedUserDataDir = userDataDir,
            Headless = headless
        };
    }

    static async Task<BrowserSession> LaunchSystemChromeEphemeralAsync(
        IPlaywright playwright,
        InstalledBrowser browser,
        BrowserProxy? proxy,
        bool headless,
        IProgress<string> log)
    {
        IBrowser launched;
        try
        {
            launched = await playwright.Chromium.LaunchAsync(SystemChromeLaunchOptions(browser, proxy, headless, useChannel: true));
        }
        catch (PlaywrightException)
        {
            launched = await playwright.Chromium.LaunchAsync(SystemChromeLaunchOptions(browser, proxy, headless, useChannel: false));
        }

        var context = await launched.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = MiniViewport,
            Locale = UiLocale(proxy),
            ExtraHTTPHeaders = LocaleHeaders(proxy),
            Proxy = ToPlaywrightProxy(proxy)
        });

        var owned = headless
            ? []
            : CapturePidsByExecutableHint(browser.ProcessName, [browser.ExecutablePath, @"Google\Chrome\", @"Microsoft\Edge\"]);
        VisibleMouse.SetOwnerProcessIds(owned);
        WriteLaunchSession(0, null, owned, browser.ProcessName);
        log.Report($"Đã mở {browser.Kind} (phiên tạm) — proxy {(proxy == null ? "không dùng" : proxy.HostPort)}.");

        return new BrowserSession
        {
            Playwright = playwright,
            Browser = launched,
            Context = context,
            LaunchedProcess = null,
            DebugPort = 0,
            ConnectedToExisting = false,
            OwnsBrowser = true,
            OwnedProcessIds = owned,
            Headless = headless
        };
    }

    static BrowserTypeLaunchPersistentContextOptions PersistentChromeOptions(
        InstalledBrowser browser,
        BrowserProxy? proxy,
        int port,
        bool headless,
        string profileFolder,
        bool useChannel)
    {
        var args = new List<string>
        {
            $"--profile-directory={profileFolder}",
            "--disable-blink-features=AutomationControlled",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-session-crashed-bubble",
            "--hide-crash-restore-bubble"
        };
        AddProxyHardeningArgs(args, proxy);
        if (!headless)
            AddMiniWindowArgs(args);

        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = headless,
            SlowMo = headless ? 0 : 80,
            ViewportSize = MiniViewport,
            Locale = UiLocale(proxy),
            ExtraHTTPHeaders = LocaleHeaders(proxy),
            IgnoreDefaultArgs = ["--enable-automation"],
            Args = args,
            Proxy = ToPlaywrightProxy(proxy)
        };
        if (useChannel)
            options.Channel = browser.Channel;
        else
            options.ExecutablePath = browser.ExecutablePath;
        _ = port;
        return options;
    }

    static BrowserTypeLaunchOptions SystemChromeLaunchOptions(
        InstalledBrowser browser,
        BrowserProxy? proxy,
        bool headless,
        bool useChannel)
    {
        var args = new List<string>
        {
            "--disable-blink-features=AutomationControlled",
            "--no-first-run",
            "--no-default-browser-check"
        };
        AddProxyHardeningArgs(args, proxy);
        if (!headless)
            AddMiniWindowArgs(args);

        var options = new BrowserTypeLaunchOptions
        {
            Headless = headless,
            SlowMo = headless ? 0 : 80,
            Args = args,
            Proxy = ToPlaywrightProxy(proxy)
        };
        if (useChannel)
            options.Channel = browser.Channel;
        else
            options.ExecutablePath = browser.ExecutablePath;
        return options;
    }

    static string UiLocale(BrowserProxy? proxy)
        => proxy != null ? "en-US" : "vi-VN";

    static Dictionary<string, string> LocaleHeaders(BrowserProxy? proxy)
        => new()
        {
            ["Accept-Language"] = proxy != null
                ? "en-US,en;q=0.9"
                : "vi-VN,vi;q=0.9,en;q=0.8"
        };

    static Proxy? ToPlaywrightProxy(BrowserProxy? proxy)
    {
        if (proxy == null)
            return null;
        return new Proxy
        {
            Server = proxy.Server,
            Bypass = "<-loopback>",
            Username = proxy.Username,
            Password = proxy.Password
        };
    }

    /// <summary>
    /// HTTP proxy không bắt được QUIC/WebRTC — Chrome sẽ ra IP thật (VN) dù đã set proxy.
    /// Tắt các kênh đó để mọi request đi qua --proxy-server.
    /// </summary>
    static void AddProxyHardeningArgs(List<string> args, BrowserProxy? proxy)
    {
        if (proxy == null)
            return;
        args.Add($"--proxy-server={proxy.Server}");
        args.Add("--proxy-bypass-list=<-loopback>");
        args.Add("--disable-quic");
        args.Add("--dns-over-https-mode=off");
        args.Add("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
    }

    static void CloseLeftoverAutoClickChrome(InstalledBrowser browser, string userDataDir, IProgress<string> log)
    {
        var pids = CapturePidsByCommandLine(browser.ProcessName, userDataDir);
        var session = ReadLaunchSession();
        if (session?.Pids is { Count: > 0 })
            pids = pids.Concat(session.Pids).Distinct().ToList();
        if (pids.Count == 0)
            return;

        log.Report("Đóng cửa sổ AutoClick cũ để mở lại đúng profile và gắn proxy...");
        KillOwnedProcesses(null, pids);
        Thread.Sleep(400);
        ClearLaunchSession();
    }

    static void PrepareIsolatedProfile(
        InstalledBrowser browser,
        BrowserProfileInfo profile,
        string isolatedDir,
        string profileFolder,
        BrowserProxy? proxy,
        IProgress<string> log)
    {
        var dest = Path.Combine(isolatedDir, profileFolder);
        var source = Path.Combine(browser.UserDataDir, profile.FolderName);
        var destPrefs = Path.Combine(dest, "Preferences");

        if (!File.Exists(destPrefs) && Directory.Exists(source))
        {
            log.Report($"Sao chép profile {profile.DisplayName} → {profileFolder} (bỏ cache / file đang khóa)...");
            CopyDirectoryBestEffort(source, dest);
            if (!File.Exists(destPrefs))
                log.Report("Không copy được Preferences (profile đang bị Chrome khóa). Mở profile riêng mới — proxy vẫn gắn.");
        }
        else if (!Directory.Exists(dest))
        {
            Directory.CreateDirectory(dest);
        }

        MarkProfileClean(dest);
        ApplyProxyToPreferences(dest, proxy);
        WriteLocalState(isolatedDir, profileFolder);
    }

    /// <summary>Ép proxy vào Preferences — profile copy từ Chrome hàng ngày hay để mode=system và lách proxy.</summary>
    static void ApplyProxyToPreferences(string profileDir, BrowserProxy? proxy)
    {
        Directory.CreateDirectory(profileDir);
        var path = Path.Combine(profileDir, "Preferences");
        JsonObject root;
        try
        {
            root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        if (proxy == null)
        {
            root.Remove("proxy");
        }
        else
        {
            root["proxy"] = new JsonObject
            {
                ["mode"] = "fixed_servers",
                ["server"] = proxy.Server,
                ["bypass_list"] = "<-loopback>"
            };
        }

        var doh = root["dns_over_https"] as JsonObject ?? new JsonObject();
        doh["mode"] = "off";
        root["dns_over_https"] = doh;

        if (proxy != null)
        {
            var intl = root["intl"] as JsonObject ?? new JsonObject();
            intl["accept_languages"] = "en-US,en";
            intl["selected_languages"] = "en-US,en";
            root["intl"] = intl;
        }

        try
        {
            File.WriteAllText(path, root.ToJsonString());
        }
        catch
        {
            // profile đang khóa
        }
    }

    static void ResetIsolatedProfile(string userDataDir, string profileFolder)
    {
        try
        {
            var dest = Path.Combine(userDataDir, profileFolder);
            if (Directory.Exists(dest))
                Directory.Delete(dest, recursive: true);
        }
        catch
        {
            // ignore
        }
        ClearStaleLocks(userDataDir);
        Directory.CreateDirectory(Path.Combine(userDataDir, profileFolder));
    }

    static void WriteLocalState(string userDataDir, string profileFolder)
    {
        var path = Path.Combine(userDataDir, "Local State");
        JsonObject root;
        try
        {
            root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        var profile = root["profile"] as JsonObject ?? new JsonObject();
        var cache = profile["info_cache"] as JsonObject ?? new JsonObject();
        if (cache[profileFolder] is not JsonObject)
            cache[profileFolder] = new JsonObject { ["name"] = profileFolder };
        profile["info_cache"] = cache;
        profile["last_used"] = profileFolder;
        profile["last_active_profiles"] = new JsonArray(profileFolder);
        root["profile"] = profile;
        Directory.CreateDirectory(userDataDir);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    static void MarkProfileClean(string profileDir)
    {
        var path = Path.Combine(profileDir, "Preferences");
        if (!File.Exists(path))
            return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
                return;
            var profile = root["profile"] as JsonObject ?? new JsonObject();
            profile["exit_type"] = "Normal";
            profile["exited_cleanly"] = true;
            root["profile"] = profile;

            var browser = root["browser"] as JsonObject ?? new JsonObject();
            browser.Remove("window_placement");
            browser.Remove("window_placement_popup");
            var box = LeftHalfBounds();
            browser["window_placement"] = new JsonObject
            {
                ["maximized"] = false,
                ["left"] = box.Left,
                ["top"] = box.Top,
                ["right"] = box.Right,
                ["bottom"] = box.Bottom
            };
            root["browser"] = browser;
            File.WriteAllText(path, root.ToJsonString());
        }
        catch
        {
            // Preferences hỏng thì Chrome tự tạo lại
        }
    }

    static readonly HashSet<string> SkipCopyDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache",
        "DawnCache", "GraphiteDawnCache", "BrowserMetrics", "Crashpad",
        "optimization_guide_hint_cache", "JumpListIconsMostVisited",
        "JumpListIconsRecentClosed", "CacheStorage"
    };

    static void CopyDirectoryBestEffort(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            try
            {
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
            }
            catch
            {
                // file đang khóa bởi Chrome hàng ngày
            }
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (SkipCopyDirs.Contains(name))
                continue;
            CopyDirectoryBestEffort(dir, Path.Combine(dest, name));
        }
    }

    static void ClearStaleLocks(string userDataDir)
    {
        foreach (var name in new[] { "SingletonLock", "SingletonSocket", "SingletonCookie" })
        {
            try
            {
                var path = Path.Combine(userDataDir, name);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>Playwright bám vào Chrome đã mở qua CDP.</summary>
    static async Task<BrowserSession> ConnectAsync(
        int port,
        bool connectedToExisting,
        Process? launched,
        CancellationToken ct,
        IReadOnlyList<int>? ownedPids = null,
        string? userDataDir = null)
    {
        _ = ct;
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}", new BrowserTypeConnectOverCDPOptions
        {
            Timeout = 30_000
        });

        var context = browser.Contexts.Count > 0
            ? browser.Contexts[0]
            : await browser.NewContextAsync();

        var pids = ownedPids ?? [];
        return new BrowserSession
        {
            Playwright = playwright,
            Browser = browser,
            Context = context,
            LaunchedProcess = launched,
            DebugPort = port,
            ConnectedToExisting = connectedToExisting,
            OwnsBrowser = !connectedToExisting,
            OwnedProcessIds = pids,
            IsolatedUserDataDir = userDataDir
        };
    }

    static void LogProxy(IProgress<string> log, BrowserProxy? proxy)
    {
        if (proxy == null)
            return;
        log.Report("Proxy: " + proxy.Server + (proxy.HasAuth ? " (có user/pass)" : "")
                   + " — tắt QUIC/WebRTC để Google không lách IP thật.");
    }

    /// <summary>Gọi sau khi mở context: lấy IP công cộng mà trình duyệt thực sự thoát ra.</summary>
    public static async Task ReportExitIpAsync(IBrowserContext context, IProgress<string> log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var body = await TryFetchExitInfoAsync(context.APIRequest);
            if (body == null)
            {
                log.Report("Không lấy được IP thoát — proxy có thể sai loại (thử socks5://) hoặc proxy chết.");
                return;
            }

            if (TryReadIpInfo(body, out var ip, out var where))
            {
                log.Report("IP thoát ra ngoài: " + ip + (where.Length > 0 ? "  (" + where + ")" : ""));
                return;
            }

            log.Report("IP thoát ra ngoài: " + TruncateOneLine(body, 120));
        }
        catch (Exception ex)
        {
            log.Report("Không lấy được IP thoát: " + ex.Message);
        }
    }

    static async Task<string?> TryFetchExitInfoAsync(IAPIRequestContext api)
    {
        foreach (var url in new[]
        {
            "https://ipinfo.io/json",
            "https://api.ipify.org?format=json",
            "https://cloudflare.com/cdn-cgi/trace"
        })
        {
            try
            {
                var res = await api.GetAsync(url, new() { Timeout = 10000, MaxRedirects = 3 });
                if (res.Ok)
                    return await res.TextAsync();
            }
            catch
            {
                // thử URL tiếp
            }
        }

        return null;
    }

    static bool TryReadIpInfo(string body, out string ip, out string where)
    {
        ip = "";
        where = "";
        body = body.Trim();
        if (body.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var r = doc.RootElement;
                ip = r.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() ?? "" : "";
                var parts = new List<string>();
                foreach (var key in new[] { "country", "region", "city", "org" })
                {
                    if (r.TryGetProperty(key, out var el))
                    {
                        var v = el.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                            parts.Add(v);
                    }
                }
                where = string.Join(", ", parts);
                return ip.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("ip=", StringComparison.OrdinalIgnoreCase))
                ip = line[3..].Trim();
            if (line.StartsWith("loc=", StringComparison.OrdinalIgnoreCase))
                where = line[4..].Trim();
        }

        return ip.Length > 0;
    }

    static string TruncateOneLine(string value, int max)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max] + "...";
    }

    internal static void KillOwnedProcesses(Process? launched, IReadOnlyList<int> pids)
    {
        try
        {
            if (launched is { HasExited: false })
                launched.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }

        foreach (var pid in pids.Distinct())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch
            {
                // đã thoát hoặc không phải process của ta
            }
        }
    }

    static List<int> CapturePidsByExecutableHint(string processName, IReadOnlyList<string> hints)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(processName))
            return result;
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                var path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { /* thiếu quyền */ }
                if (hints.Any(h => path.Contains(h, StringComparison.OrdinalIgnoreCase)))
                    result.Add(p.Id);
            }
            catch
            {
                // ignore
            }
            finally
            {
                p.Dispose();
            }
        }
        return result;
    }

    /// <summary>Chỉ lấy process có command line chứa user-data-dir của AutoClick — không đụng Chrome hàng ngày.</summary>
    static List<int> CapturePidsByCommandLine(string processName, string userDataDir)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(userDataDir))
            return result;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = $"process where \"Name='{processName}.exe'\" get ProcessId,CommandLine /FORMAT:LIST",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi);
            if (proc == null)
                return result;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(4000);

            var needle = userDataDir.TrimEnd('\\');
            int? pid = null;
            string? cmd = null;
            void Flush()
            {
                if (pid is > 0 && cmd != null && cmd.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    result.Add(pid.Value);
                pid = null;
                cmd = null;
            }

            foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    Flush();
                    continue;
                }
                if (line.StartsWith("CommandLine=", StringComparison.OrdinalIgnoreCase))
                    cmd = line["CommandLine=".Length..];
                else if (line.StartsWith("ProcessId=", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(line["ProcessId=".Length..], out var parsed))
                    pid = parsed;
            }
            Flush();
        }
        catch
        {
            // wmic không có thì thôi — đóng bằng CDP vẫn được
        }

        return result.Distinct().ToList();
    }

    static int ReadDevToolsPort(string userDataDir, int fallback)
    {
        try
        {
            var file = Path.Combine(userDataDir, "DevToolsActivePort");
            if (!File.Exists(file))
                return fallback;
            var line = File.ReadLines(file).FirstOrDefault();
            if (int.TryParse(line, out var port) && port is > 0 and < 65536)
                return port;
        }
        catch
        {
            // ignore
        }
        return fallback;
    }

    static bool PidStillRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    static void WriteLaunchSession(int port, string? userDataDir, IReadOnlyList<int> pids, string processName)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(new LaunchSession
            {
                Port = port,
                UserDataDir = userDataDir,
                Pids = pids.ToList(),
                ProcessName = processName
            });
            File.WriteAllText(LaunchSessionPath, json);
        }
        catch
        {
            // ignore
        }
    }

    static LaunchSession? ReadLaunchSession()
    {
        try
        {
            if (!File.Exists(LaunchSessionPath))
                return null;
            return JsonSerializer.Deserialize<LaunchSession>(File.ReadAllText(LaunchSessionPath));
        }
        catch
        {
            return null;
        }
    }

    public static void ClearSavedLaunch() => ClearLaunchSession();

    static void ClearLaunchSession()
    {
        try
        {
            if (File.Exists(LaunchSessionPath))
                File.Delete(LaunchSessionPath);
        }
        catch
        {
            // ignore
        }
    }

    sealed class LaunchSession
    {
        public int Port { get; set; }
        public string? UserDataDir { get; set; }
        public List<int> Pids { get; set; } = [];
        public string? ProcessName { get; set; }
    }

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
