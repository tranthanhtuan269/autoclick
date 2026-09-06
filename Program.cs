namespace AutoClick;

/// <summary>
/// Điểm vào của app WinForms.
/// Sửa giao diện / luồng chạy: <see cref="MainForm"/>.
/// Sửa mở Chrome: Services/BrowserLauncher.cs
/// Sửa search + crawl: Services/GoogleCrawlerService.cs
/// </summary>
static class Program
{
    // STAThread bắt buộc với WinForms (kéo-thả, clipboard, WebBrowser...).
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--install-chromium", StringComparison.OrdinalIgnoreCase)))
        {
            var dest = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
            if (string.IsNullOrWhiteSpace(dest))
                dest = Path.Combine(AppContext.BaseDirectory, "ms-playwright");
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", dest);
            Environment.Exit(Microsoft.Playwright.Program.Main(["install", "chromium"]));
            return;
        }

        ConfigurePlaywrightBrowsersPath();
        // High-DPI, visual styles mặc định của .NET 8.
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    /// <summary>
    /// Bộ cài đặt ms-playwright cạnh .exe. Máy dev / thiếu bundle thì dùng thư mục trong AppData.
    /// Phải gọi trước Playwright.CreateAsync / install chromium.
    /// </summary>
    static void ConfigurePlaywrightBrowsersPath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ms-playwright");
        var path = HasBundledChromium(bundled)
            ? bundled
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AutoClick",
                "ms-playwright");
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", path);
    }

    static bool HasBundledChromium(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                && Directory.EnumerateDirectories(dir, "chromium-*").Any();
        }
        catch
        {
            return false;
        }
    }
}
