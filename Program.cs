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
    static void Main()
    {
        // High-DPI, visual styles mặc định của .NET 8.
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
