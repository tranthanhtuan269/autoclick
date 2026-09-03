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

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out PointApi lpPoint);

    [DllImport("user32.dll")]
    static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    static extern int ShowCursor(bool bShow);

    [StructLayout(LayoutKind.Sequential)]
    struct PointApi
    {
        public int X;
        public int Y;
    }

    /// <summary>Kéo con trỏ từ vị trí hiện tại tới (x,y) rồi click trái.</summary>
    public static async Task MoveSmoothAndClickAsync(int x, int y, CancellationToken ct)
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
