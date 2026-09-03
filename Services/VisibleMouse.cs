using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace AutoClick.Services;

/// <summary>
/// Click bằng chuột Windows: đưa cửa sổ Chromium ra trước, tính tọa độ màn hình, kéo kim chuột rồi bấm.
/// </summary>
public static class VisibleMouse
{
    sealed class ScreenPointDto
    {
        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        [JsonPropertyName("dpr")]
        public double Dpr { get; set; }
    }

    public static async Task<bool> ClickAsync(IPage page, ILocator loc, IProgress<string> log, CancellationToken ct)
    {
        try
        {
            await page.BringToFrontAsync();
            await loc.ScrollIntoViewIfNeededAsync(new() { Timeout = 8000 });
            await ShowPagePointerAsync(loc);
            await page.WaitForTimeoutAsync(200);

            var pt = await loc.EvaluateAsync<ScreenPointDto>(@"el => {
                el.scrollIntoView({ block: 'center', inline: 'nearest' });
                const r = el.getBoundingClientRect();
                const borderX = Math.max(0, (window.outerWidth - window.innerWidth) / 2);
                const chromeY = Math.max(0, window.outerHeight - window.innerHeight - borderX);
                return {
                    x: window.screenX + borderX + r.left + r.width / 2,
                    y: window.screenY + chromeY + r.top + r.height / 2,
                    dpr: window.devicePixelRatio || 1
                };
            }");

            if (pt == null)
            {
                log.Report("Không tính được tọa độ chuột.");
                return false;
            }

            var dpr = pt.Dpr <= 0 ? 1 : pt.Dpr;
            // SetCursorPos dùng pixel vật lý khi app DPI-aware (PerMonitorV2).
            var x = (int)Math.Round(pt.X * dpr);
            var y = (int)Math.Round(pt.Y * dpr);
            log.Report($"Kéo chuột Windows tới ({x}, {y}) rồi click...");
            await OsMouse.MoveSmoothAndClickAsync(x, y, ct);
            return true;
        }
        catch (Exception ex)
        {
            log.Report("Click chuột Windows lỗi: " + ex.Message);
            return false;
        }
    }

    /// <summary>Kim ảo trên trang (bổ sung) — con trỏ Windows mới là chuột thật.</summary>
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
                        transition: 'left 0.18s linear, top 0.18s linear',
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
