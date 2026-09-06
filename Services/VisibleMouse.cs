using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Playwright;

namespace AutoClick.Services;

/// <summary>
/// Click bằng chuột Windows. Tọa độ lấy từ HWND vùng vẽ trang (Chrome_RenderWidgetHostHWND)
/// + ClientToScreen — không cộng screenX/DPI thủ công (dễ lệch xa link).
/// </summary>
public static class VisibleMouse
{
    static IReadOnlySet<int>? OwnerProcessIds;

    /// <summary>PID cửa sổ do app mở — tránh click nhầm vào Chrome hàng ngày.</summary>
    public static void SetOwnerProcessIds(IReadOnlyList<int>? pids)
        => OwnerProcessIds = pids is { Count: > 0 } ? pids.ToHashSet() : null;

    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr hWndParent, EnumProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr hWnd, ref PointApi lpPoint);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    const int SwRestore = 9;

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PointApi
    {
        public int X;
        public int Y;
    }

    public static async Task<bool> ClickAsync(IPage page, ILocator loc, IProgress<string> log, CancellationToken ct, bool ctrlClick = false)
    {
        try
        {
            if (!await MoveToAsync(page, loc, log, ct))
                return await ClickByJsFallbackAsync(page, loc, log, ct, ctrlClick);

            if (ctrlClick)
                await OsMouse.MoveSmoothAndCtrlClickAsync(_lastScreenX, _lastScreenY, ct);
            else
                await OsMouse.MoveSmoothAndClickAsync(_lastScreenX, _lastScreenY, ct);
            return true;
        }
        catch (Exception ex)
        {
            log.Report("Click chuột Windows lỗi: " + ex.Message);
            return false;
        }
    }

    static int _lastScreenX;
    static int _lastScreenY;

    /// <summary>Kéo kim chuột tới phần tử, chưa click — dùng khi click thật bằng Playwright trên đúng thẻ &lt;a&gt;.</summary>
    public static async Task<bool> MoveToAsync(IPage page, ILocator loc, IProgress<string> log, CancellationToken ct)
    {
        await page.BringToFrontAsync();
        await loc.ScrollIntoViewIfNeededAsync(new() { Timeout = 8000 });
        await ShowPagePointerAsync(loc);
        await page.WaitForTimeoutAsync(150);

        var box = await loc.BoundingBoxAsync();
        if (box == null || box.Width < 1 || box.Height < 1)
        {
            log.Report("Không lấy được bounding box của phần tử.");
            return false;
        }

        var clickEl = loc.Locator("h3").First;
        if (await clickEl.CountAsync() > 0)
        {
            var h3 = await clickEl.BoundingBoxAsync();
            if (h3 is { Width: > 8, Height: > 8 })
                box = h3;
        }

        var cssX = box.X + Math.Min(48, box.Width * 0.35);
        var cssY = box.Y + box.Height / 2;

        var viewport = await page.EvaluateAsync<int[]>("() => [window.innerWidth, window.innerHeight]");
        var innerW = Math.Max(1, viewport[0]);
        var innerH = Math.Max(1, viewport[1]);

        if (!TryFindPlaywrightRenderWidget(out var topHwnd, out var renderHwnd))
        {
            var fg = GetForegroundWindow();
            if (GetClass(fg) == "Chrome_WidgetWin_1")
            {
                topHwnd = fg;
                renderHwnd = FindLargestRenderWidget(fg);
            }
        }

        if (topHwnd == IntPtr.Zero || renderHwnd == IntPtr.Zero)
            return false;

        BringToFront(topHwnd);
        await Task.Delay(120, ct);

        GetClientRect(renderHwnd, out var client);
        if (client.Width < 10 || client.Height < 10)
            return false;

        var scaleX = client.Width / (double)innerW;
        var scaleY = client.Height / (double)innerH;
        var point = new PointApi
        {
            X = (int)Math.Round(cssX * scaleX),
            Y = (int)Math.Round(cssY * scaleY)
        };
        ClientToScreen(renderHwnd, ref point);
        _lastScreenX = point.X;
        _lastScreenY = point.Y;
        log.Report($"Kéo chuột tới tiêu đề đích ({point.X}, {point.Y}) [hwnd scale {scaleX:0.00}x]...");
        await OsMouse.MoveSmoothAsync(point.X, point.Y, ct);
        return true;
    }

    /// <summary>Fallback cũ: screenX + thanh công cụ, KHÔNG nhân devicePixelRatio.</summary>
    static async Task<bool> ClickByJsFallbackAsync(IPage page, ILocator loc, IProgress<string> log, CancellationToken ct, bool ctrlClick = false)
    {
        var pt = await loc.EvaluateAsync<double[]>(@"el => {
            const r = el.getBoundingClientRect();
            const borderX = Math.max(0, (window.outerWidth - window.innerWidth) / 2);
            const chromeY = Math.max(0, window.outerHeight - window.innerHeight - borderX);
            return [
                window.screenX + borderX + r.left + r.width / 2,
                window.screenY + chromeY + r.top + r.height / 2
            ];
        }");
        if (pt == null || pt.Length < 2)
            return false;
        var x = (int)Math.Round(pt[0]);
        var y = (int)Math.Round(pt[1]);
        log.Report($"Fallback JS click ({x}, {y}) — không nhân DPI{(ctrlClick ? ", Ctrl" : "")}.");
        if (ctrlClick)
            await OsMouse.MoveSmoothAndCtrlClickAsync(x, y, ct);
        else
            await OsMouse.MoveSmoothAndClickAsync(x, y, ct);
        return true;
    }

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    static bool TryFindPlaywrightRenderWidget(out IntPtr topHwnd, out IntPtr renderHwnd)
    {
        topHwnd = IntPtr.Zero;
        renderHwnd = IntPtr.Zero;
        IntPtr foundTop = IntPtr.Zero;
        var bestArea = 0;
        var enumTop = new EnumProc((h, _) =>
        {
            if (!IsWindowVisible(h) || GetClass(h) != "Chrome_WidgetWin_1")
                return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!IsOwnedBrowser((int)pid))
                return true;
            GetWindowRect(h, out var wr);
            var area = Math.Max(0, wr.Width) * Math.Max(0, wr.Height);
            if (area > bestArea)
            {
                bestArea = area;
                foundTop = h;
            }
            return true;
        });
        EnumWindows(enumTop, IntPtr.Zero);
        GC.KeepAlive(enumTop);
        if (foundTop == IntPtr.Zero)
            return false;

        var best = FindLargestRenderWidget(foundTop);
        if (best == IntPtr.Zero)
            return false;
        topHwnd = foundTop;
        renderHwnd = best;
        return true;
    }

    static IntPtr FindLargestRenderWidget(IntPtr top)
    {
        IntPtr best = IntPtr.Zero;
        var bestArea = 0;
        var enumChild = new EnumProc((h, _) =>
        {
            if (!IsWindowVisible(h))
                return true;
            var cls = GetClass(h);
            if (!cls.Contains("RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase))
                return true;
            GetClientRect(h, out var rc);
            var area = rc.Width * rc.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = h;
            }
            return true;
        });
        EnumChildWindows(top, enumChild, IntPtr.Zero);
        GC.KeepAlive(enumChild);
        return best;
    }

    static bool IsOwnedBrowser(int pid)
    {
        if (OwnerProcessIds is { Count: > 0 })
            return OwnerProcessIds.Contains(pid);

        try
        {
            using var p = Process.GetProcessById(pid);
            string path;
            try { path = p.MainModule?.FileName ?? ""; }
            catch { path = ""; }
            if (path.Contains("ms-playwright", StringComparison.OrdinalIgnoreCase)
                || path.Contains("chromium-", StringComparison.OrdinalIgnoreCase)
                || path.Contains("chrome-win", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    static string GetClass(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static void BringToFront(IntPtr hwnd)
    {
        ShowWindow(hwnd, SwRestore);
        var foreground = GetForegroundWindow();
        var foreThread = GetWindowThreadProcessId(foreground, out _);
        var appThread = GetCurrentThreadId();
        if (foreThread != appThread)
            AttachThreadInput(foreThread, appThread, true);
        SetForegroundWindow(hwnd);
        if (foreThread != appThread)
            AttachThreadInput(foreThread, appThread, false);
    }

    static async Task ShowPagePointerAsync(ILocator loc)
    {
        try
        {
            await loc.EvaluateAsync(@"el => {
                const r = el.getBoundingClientRect();
                let c = document.getElementById('__ac_cursor');
                if (!c) {
                    c = document.createElement('div');
                    c.id = '__ac_cursor';
                    c.textContent = '➤';
                    Object.assign(c.style, {
                        position: 'fixed',
                        zIndex: '2147483647',
                        pointerEvents: 'none',
                        fontSize: '28px',
                        color: '#ff6d00',
                        textShadow: '0 1px 3px #000',
                        left: '0px',
                        top: '0px'
                    });
                    document.body.appendChild(c);
                }
                c.style.left = (r.left + r.width / 2) + 'px';
                c.style.top = (r.top + r.height / 2 - 8) + 'px';
            }");
        }
        catch
        {
            // overlay không bắt buộc
        }
    }
}
