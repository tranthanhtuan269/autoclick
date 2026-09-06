using System.Runtime.InteropServices;

namespace AutoClick.Services;

/// <summary>
/// Điều khiển con trỏ chuột THẬT của Windows (SetCursorPos + click).
/// Playwright page.Mouse chỉ là chuột ảo trong browser — không thấy kim chuột trên màn hình.
/// </summary>
public static class OsMouse
{
    const uint MouseeventfLeftdown = 0x0002;
    const uint MouseeventfLeftup = 0x0004;
    const byte VkControl = 0x11;
    const uint KeyeventfKeyup = 0x0002;

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out PointApi lpPoint);

    [DllImport("user32.dll")]
    static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    static extern int ShowCursor(bool bShow);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    struct PointApi
    {
        public int X;
        public int Y;
    }

    /// <summary>Kéo con trỏ từ vị trí hiện tại tới (x,y), chưa click.</summary>
    public static async Task MoveSmoothAsync(int x, int y, CancellationToken ct)
    {
        EnsureCursorVisible();

        if (!GetCursorPos(out var start))
            start = new PointApi { X = x, Y = y };

        const int steps = 28;
        for (var i = 1; i <= steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            var nx = start.X + (x - start.X) * i / steps;
            var ny = start.Y + (y - start.Y) * i / steps;
            SetCursorPos(nx, ny);
            await Task.Delay(14, ct);
        }

        SetCursorPos(x, y);
        await Task.Delay(90, ct);
    }

    /// <summary>Kéo con trỏ từ vị trí hiện tại tới (x,y) rồi click trái.</summary>
    public static async Task MoveSmoothAndClickAsync(int x, int y, CancellationToken ct)
    {
        await MoveSmoothAsync(x, y, ct);
        await LeftClickAsync(ct);
    }

    /// <summary>Kéo chuột rồi Ctrl+click trái — Chrome mở tab mới, không rời trang Google.</summary>
    public static async Task MoveSmoothAndCtrlClickAsync(int x, int y, CancellationToken ct)
    {
        await MoveSmoothAsync(x, y, ct);
        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        await Task.Delay(30, ct);
        try
        {
            await LeftClickAsync(ct);
        }
        finally
        {
            keybd_event(VkControl, 0, KeyeventfKeyup, UIntPtr.Zero);
        }
    }

    static async Task LeftClickAsync(CancellationToken ct)
    {
        mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(45, ct);
        mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(80, ct);
    }

    static void EnsureCursorVisible()
    {
        // ShowCursor dùng bộ đếm; gọi đến khi >= 0 thì kim chuột hiện.
        for (var i = 0; i < 8; i++)
        {
            if (ShowCursor(true) >= 0)
                break;
        }
    }
}
