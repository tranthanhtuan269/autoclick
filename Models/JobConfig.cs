namespace AutoClick.Models;

/// <summary>
/// Cách so khớp URL kết quả Google với danh sách link bạn nhập trên form.
/// Sửa logic chi tiết trong Services/LinkMatcher.cs.
/// </summary>
public enum MatchMode
{
    /// <summary>URL kết quả chứa chuỗi/domain đã nhập (dùng nhiều nhất).</summary>
    Contains,
    /// <summary>Chỉ so hostname, ví dụ vnexpress.net khớp mọi trang con.</summary>
    Domain,
    /// <summary>Khớp gần như đúng URL (bỏ www, #fragment, dấu / cuối).</summary>
    Exact
}

/// <summary>
/// Một trường crawl thêm bằng CSS selector.
/// Trên form nhập:  ten_truong = selector
/// Ví dụ:  gia = .product-price
/// </summary>
public sealed class CustomSelector
{
    public required string Name { get; init; }
    public required string Selector { get; init; }
}

/// <summary>
/// Toàn bộ cấu hình 1 lần chạy — được MainForm.ReadConfig() đọc từ các ô trên form.
/// Muốn đổi giá trị mặc định trên UI: sửa MainForm (constructor), không sửa file này.
/// </summary>
public sealed class JobConfig
{
    public required InstalledBrowser Browser { get; init; }
    public required BrowserProfileInfo Profile { get; init; }

    /// <summary>site trên API scan — từ khóa đầu khớp sitename (vd. hakoreview).</summary>
    public string ScanSite { get; init; } = "";

    /// <summary>Mỗi phần tử = 1 từ khóa Google — đồng thời gửi lên API làm key.</summary>
    public required IReadOnlyList<string> Keywords { get; init; }

    /// <summary>URL hoặc domain cần tìm trong kết quả search.</summary>
    public required IReadOnlyList<string> TargetLinks { get; init; }

    public MatchMode MatchMode { get; init; } = MatchMode.Contains;

    /// <summary>Số trang Google tối đa (1 trang ≈ 10 kết quả). Tăng nếu site hay nằm trang sau.</summary>
    public int MaxGooglePages { get; init; } = 3;

    /// <summary>
    /// true = trang 1 → trang 2 → tìm lại trang 1; hết thì sang từ khóa khác.
    /// Bỏ qua MaxGooglePages.
    /// </summary>
    public bool BouncePageRetry { get; init; }

    /// <summary>Số lần click link khớp để mở tab mới (mỗi lần 1 tab).</summary>
    public int OpenNewTabClicks { get; init; } = 1;

    /// <summary>Nghỉ giữa các lần click cùng một link mục tiêu (ms).</summary>
    public int ClickIntervalMs { get; init; } = 200;

    /// <summary>Nghỉ giữa các thao tác (ms) cho giống người dùng, giảm bị chặn.</summary>
    public int DelayMs { get; init; } = 1500;

    /// <summary>
    /// true = sau khi quét hết danh sách từ khóa thì chạy lại từ đầu, tới khi bấm Dừng.
    /// false = chỉ chạy 1 lượt rồi kết thúc (luồng mặc định).
    /// </summary>
    public bool AutoRepeat { get; init; }

    /// <summary>true = trình duyệt headless, không hiện cửa sổ.</summary>
    public bool Headless { get; init; }

    /// <summary>Danh sách proxy (mỗi phần tử 1 dòng trên form). Rỗng = không dùng proxy.</summary>
    public IReadOnlyList<BrowserProxy> Proxies { get; init; } = [];

    /// <summary>Proxy đầu danh sách — tương thích chỗ chỉ cần 1 proxy (API scan).</summary>
    public BrowserProxy? Proxy => Proxies.Count > 0 ? Proxies[0] : null;

    public required string OutputDirectory { get; init; }
    public bool SaveHtml { get; init; } = true;
    public bool SaveCsv { get; init; } = true;
    public bool SaveJson { get; init; } = true;
    public IReadOnlyList<CustomSelector> Selectors { get; init; } = [];

    /// <summary>
    /// Cổng Chrome DevTools (CDP). Đổi nếu 9333 bị phần mềm khác chiếm.
    /// Phải khớp với BrowserLauncher.DefaultDebugPort nếu bạn đổi một bên.
    /// </summary>
    public int DebugPort { get; init; } = 9333;
}

/// <summary>Kết quả crawl của 1 từ khóa.</summary>
public sealed class CrawlResult
{
    public required string Keyword { get; init; }
    public bool Found { get; init; }
    public string? MatchedUrl { get; set; }
    /// <summary>URL sau khi trang đích load xong (có thể khác MatchedUrl vì redirect).</summary>
    public string? FinalUrl { get; set; }
    public string? Title { get; set; }
    public string? Text { get; set; }
    public string? Html { get; set; }
    public string? HtmlPath { get; set; }
    public string? Error { get; set; }
    /// <summary>Giá trị lấy từ CSS selector tùy chọn (tên → text).</summary>
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Chrome hoặc Edge đã cài trên máy (detect trong BrowserLauncher.DetectInstalled).</summary>
public sealed class InstalledBrowser
{
    public required string Kind { get; init; }
    /// <summary>Tên channel Playwright: "chrome" hoặc "msedge".</summary>
    public required string Channel { get; init; }
    public required string ExecutablePath { get; init; }
    /// <summary>Thư mục User Data (cookie, profile) — KHÔNG phải thư mục Default bên trong.</summary>
    public required string UserDataDir { get; init; }
    /// <summary>Tên process Windows: "chrome" hoặc "msedge".</summary>
    public required string ProcessName { get; init; }

    public override string ToString() => Kind;
}

/// <summary>Một profile Chrome/Edge (Default, Profile 1, ...).</summary>
public sealed class BrowserProfileInfo
{
    /// <summary>Tên thư mục thật, dùng cho --profile-directory.</summary>
    public required string FolderName { get; init; }
    /// <summary>Tên hiển thị trên ComboBox.</summary>
    public required string DisplayName { get; init; }

    public override string ToString() => DisplayName;
}
