using AutoClick.Models;
using AutoClick.Services;

namespace AutoClick;

/// <summary>
/// Form chính — toàn bộ ô nhập liệu nằm ở đây (không dùng file Designer).
///
/// Muốn sửa gì thì vào đâu:
///   Tiêu đề / kích thước cửa sổ     → constructor MainForm()
///   Giá trị mặc định từ khóa, link  → _txtKeywords.Text / _txtTargets.Text
///   Delay, số trang mặc định        → _numDelay.Value / _numPages.Value
///   Tự động lặp lại từ khóa         → _chkAutoRepeat.Checked
///   Proxy trình duyệt               → _txtProxy.Text
///   Thư mục lưu mặc định            → _txtOutput.Text
///   Nhãn tiếng Việt trên form       → BuildBrowserGroup / BuildSearchGroup
///   Validate trước khi chạy         → ReadConfig()
///   Bấm Bắt đầu                     → StartAsync()
/// </summary>
public sealed class MainForm : Form
{
    readonly ComboBox _cboBrowser = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly ComboBox _cboProfile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Label _lblBrowserStatus = new() { AutoSize = true, ForeColor = Color.DarkSlateGray, Text = "Chọn trình duyệt đã cài trên máy." };
    readonly TextBox _txtProxy = new() { Dock = DockStyle.Fill };
    readonly TextBox _txtKeywords = Multiline();
    readonly TextBox _txtTargets = Multiline();
    readonly ComboBox _cboMatch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    // Đổi Value = số trang Google mặc định / delay (ms) mặc định.
    readonly NumericUpDown _numPages = new() { Minimum = 1, Maximum = 20, Value = 3, Dock = DockStyle.Fill };
    readonly NumericUpDown _numDelay = new() { Minimum = 200, Maximum = 20000, Increment = 100, Value = 1500, Dock = DockStyle.Fill };
    readonly CheckBox _chkAutoRepeat = new()
    {
        Text = "Tự động lặp lại khi quét hết từ khóa",
        Checked = false,
        AutoSize = true
    };
    readonly TextBox _txtOutput = new() { Dock = DockStyle.Fill };
    readonly TextBox _txtSelectors = Multiline();
    readonly CheckBox _chkHtml = new() { Text = "Lưu HTML trang", Checked = true, AutoSize = true };
    readonly CheckBox _chkCsv = new() { Text = "Xuất CSV", Checked = true, AutoSize = true };
    readonly CheckBox _chkJson = new() { Text = "Xuất JSON", Checked = true, AutoSize = true };
    readonly Button _btnStart = new() { Text = "Bắt đầu", Width = 120, Height = 36 };
    readonly Button _btnStop = new() { Text = "Dừng", Width = 100, Height = 36, Enabled = false };
    readonly ListBox _lstLog = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    readonly Label _lblStatus = new() { Dock = DockStyle.Fill, Text = "Sẵn sàng.", TextAlign = ContentAlignment.MiddleLeft };
    readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0 };

    // Dùng để hủy job khi bấm Dừng hoặc đóng cửa sổ.
    CancellationTokenSource? _cts;
    string? _lastRunFolder;

    public MainForm()
    {
        Text = "AutoClick — Google Search Crawler";
        Width = 1180;
        Height = 780;
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 640,
            SplitterWidth = 8
        };

        var left = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 12, 12, 12) };

        left.Controls.Add(BuildLeft());
        right.Controls.Add(BuildRight());
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        Controls.Add(split);

        // SelectedIndex: 0=Contains, 1=Domain, 2=Exact (phải khớp ReadConfig).
        _cboMatch.Items.AddRange(["Contains (URL chứa chuỗi)", "Domain (khớp domain)", "Exact (khớp đúng URL)"]);
        _cboMatch.SelectedIndex = 1; // Domain: http://dienmayxanh.com/ khớp mọi bài trên site
        // Thư mục lưu mặc định. Đổi nếu muốn để Desktop hoặc D:\crawl.
        _txtOutput.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AutoClick", "results");
        // Text mẫu trên form — xóa hoặc đổi thành keyword/link bạn hay dùng.
        _txtKeywords.Text = "hakoreview" + Environment.NewLine + "hako" + Environment.NewLine + "bánh mì";
        _txtTargets.Text = "https://www.example.com";
        _txtSelectors.PlaceholderText = "title = h1" + Environment.NewLine + "mota = meta[name='description']";
        _txtProxy.PlaceholderText = "Để trống = không dùng. Ví dụ: 1.2.3.4:8080 hoặc 1.2.3.4:8080:user:pass";
        var proxyTip = new ToolTip { AutoPopDelay = 12000, ShowAlways = true };
        proxyTip.SetToolTip(_txtProxy,
            "HTTP: host:port hoặc host:port:user:pass\n" +
            "Hoặc user:pass@host:port\n" +
            "SOCKS5: socks5://host:port");

        _cboBrowser.SelectedIndexChanged += (_, _) => ReloadProfiles();
        Load += (_, _) => InitBrowsers();
        FormClosing += (_, e) => _cts?.Cancel();
    }

    Control BuildLeft()
    {
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(0, 0, 8, 0)
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        stack.Controls.Add(Group("1. Trình duyệt", BuildBrowserGroup()));
        stack.Controls.Add(Group("2. Tìm kiếm Google", BuildSearchGroup()));
        stack.Controls.Add(BuildButtons());
        return stack;
    }

    Control BuildRight()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        table.Controls.Add(new Label { Text = "Nhật ký chạy", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        table.Controls.Add(_lstLog, 0, 1);
        table.Controls.Add(_progress, 0, 2);
        table.Controls.Add(_lblStatus, 0, 3);
        return table;
    }

    Control BuildBrowserGroup()
    {
        var t = Grid(5);
        t.Controls.Add(Lbl("Trình duyệt"), 0, 0);
        t.Controls.Add(_cboBrowser, 1, 0);
        t.Controls.Add(Lbl("Profile"), 0, 1);
        t.Controls.Add(_cboProfile, 1, 1);
        t.Controls.Add(Lbl("Proxy"), 0, 2);
        t.Controls.Add(_txtProxy, 1, 2);

        var btnCheck = new Button { Text = "Kiểm tra đang mở?", AutoSize = true };
        var btnClose = new Button { Text = "Đóng trình duyệt", AutoSize = true };
        btnCheck.Click += async (_, _) => await CheckBrowserAsync();
        btnClose.Click += async (_, _) => await CloseBrowserClickedAsync();

        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        flow.Controls.Add(btnCheck);
        flow.Controls.Add(btnClose);
        t.Controls.Add(flow, 1, 3);
        t.SetColumnSpan(_lblBrowserStatus, 2);
        t.Controls.Add(_lblBrowserStatus, 0, 4);
        _lblBrowserStatus.Margin = new Padding(0, 8, 0, 0);
        return t;
    }

    Control BuildSearchGroup()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _txtKeywords.Height = 120;
        _txtTargets.Height = 90;
        t.Controls.Add(Lbl("Danh sách từ khóa" + Environment.NewLine + "(mỗi dòng 1, chạy lần lượt)"), 0, 0);
        t.Controls.Add(_txtKeywords, 1, 0);
        t.Controls.Add(Lbl("Link mục tiêu (mỗi dòng 1)"), 0, 1);
        t.Controls.Add(_txtTargets, 1, 1);
        t.Controls.Add(Lbl("Cách khớp"), 0, 2);
        t.Controls.Add(_cboMatch, 1, 2);
        t.Controls.Add(Lbl("Số trang Google tối đa"), 0, 3);
        t.Controls.Add(_numPages, 1, 3);
        t.Controls.Add(Lbl("Delay giữa thao tác (ms)"), 0, 4);
        t.Controls.Add(_numDelay, 1, 4);
        t.Controls.Add(Lbl("Chế độ chạy"), 0, 5);
        t.Controls.Add(_chkAutoRepeat, 1, 5);
        _chkAutoRepeat.Padding = new Padding(0, 6, 0, 0);
        return t;
    }

    Control BuildButtons()
    {
        var flow = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(4, 12, 4, 12) };
        _btnStart.Click += async (_, _) => await StartAsync();
        _btnStop.Click += (_, _) =>
        {
            _cts?.Cancel();
            AppendLog("Đang dừng...");
        };
        var btnOpen = new Button { Text = "Mở thư mục kết quả", Width = 170, Height = 36 };
        btnOpen.Click += (_, _) => OpenLastFolder();
        flow.Controls.Add(_btnStart);
        flow.Controls.Add(_btnStop);
        flow.Controls.Add(btnOpen);
        return flow;
    }

    static GroupBox Group(string title, Control inner)
    {
        var g = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 12) };
        inner.Dock = DockStyle.Fill;
        g.Controls.Add(inner);
        return g;
    }

    static TableLayoutPanel Grid(int rows)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    static Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 6, 0, 0) };

    static TextBox Multiline() => new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        AcceptsReturn = true
    };

    void InitBrowsers()
    {
        _cboBrowser.Items.Clear();
        foreach (var b in BrowserLauncher.DetectInstalled())
            _cboBrowser.Items.Add(b);

        if (_cboBrowser.Items.Count == 0)
        {
            _lblBrowserStatus.Text = "Không tìm thấy Chrome hoặc Edge.";
            _lblBrowserStatus.ForeColor = Color.Firebrick;
            return;
        }

        _cboBrowser.SelectedIndex = 0;
        ReloadProfiles();
    }

    void ReloadProfiles()
    {
        _cboProfile.Items.Clear();
        if (_cboBrowser.SelectedItem is not InstalledBrowser browser)
            return;
        foreach (var p in BrowserLauncher.ListProfiles(browser))
            _cboProfile.Items.Add(p);
        if (_cboProfile.Items.Count > 0)
            _cboProfile.SelectedIndex = 0;
        if (string.Equals(browser.Channel, "chromium", StringComparison.OrdinalIgnoreCase))
        {
            _lblBrowserStatus.Text = "Đang dùng Playwright Chromium — không cần đóng Chrome/Edge trên máy.";
            _lblBrowserStatus.ForeColor = Color.DarkGreen;
        }
        else
        {
            _lblBrowserStatus.Text = $"{browser.Kind} — đóng hết cửa sổ trước lần chạy đầu (app sẽ mở lại đúng profile của bạn).";
            _lblBrowserStatus.ForeColor = Color.DarkSlateGray;
        }
    }

    async Task CheckBrowserAsync()
    {
        if (_cboBrowser.SelectedItem is not InstalledBrowser browser)
            return;
        var n = BrowserLauncher.CountRunning(browser);
        var cdp = await BrowserLauncher.IsCdpAliveAsync(BrowserLauncher.DefaultDebugPort);
        var msg = n == 0
            ? $"{browser.Kind} không đang chạy. Có thể bấm Bắt đầu."
            : cdp
                ? $"{browser.Kind} đang mở và đã bật điều khiển (CDP). Có thể chạy tiếp."
                : $"{browser.Kind} đang mở ({n} process) — hãy đóng hết rồi mới chạy.";
        _lblBrowserStatus.Text = msg;
        _lblBrowserStatus.ForeColor = (n == 0 || cdp) ? Color.DarkGreen : Color.Firebrick;
        AppendLog(msg);
    }

    async Task CloseBrowserClickedAsync()
    {
        if (_cboBrowser.SelectedItem is not InstalledBrowser browser)
            return;
        var n = BrowserLauncher.CountRunning(browser);
        if (n == 0)
        {
            MessageBox.Show(this, browser.Kind + " không đang chạy.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var ok = MessageBox.Show(
            this,
            $"Đóng toàn bộ cửa sổ {browser.Kind} ({n} process)?\nLưu tab/phiên làm việc trước nếu cần.",
            "Xác nhận",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (ok != DialogResult.Yes)
            return;

        var progress = new Progress<string>(AppendLog);
        await BrowserLauncher.CloseBrowserAsync(browser, progress);
        await CheckBrowserAsync();
    }

    /// <summary>Đọc form → JobConfig. Thêm rule kiểm tra thì viết thêm throw ở đây.</summary>
    JobConfig ReadConfig()
    {
        if (_cboBrowser.SelectedItem is not InstalledBrowser browser)
            throw new InvalidOperationException("Chưa chọn trình duyệt.");
        if (_cboProfile.SelectedItem is not BrowserProfileInfo profile)
            throw new InvalidOperationException("Chưa chọn profile.");

        var keywords = Lines(_txtKeywords.Text);
        var targets = Lines(_txtTargets.Text);
        if (keywords.Count == 0)
            throw new InvalidOperationException("Nhập ít nhất 1 từ khóa.");
        if (targets.Count == 0)
            throw new InvalidOperationException("Nhập ít nhất 1 link mục tiêu.");

        var output = _txtOutput.Text.Trim();
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("Chưa chọn thư mục lưu.");
        Directory.CreateDirectory(output);

        var mode = _cboMatch.SelectedIndex switch
        {
            1 => MatchMode.Domain,
            2 => MatchMode.Exact,
            _ => MatchMode.Contains
        };

        // Mỗi dòng: "ten_truong = css-selector". Dòng không có dấu = sẽ bị bỏ.
        var selectors = new List<CustomSelector>();
        foreach (var line in Lines(_txtSelectors.Text))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0)
                continue;
            selectors.Add(new CustomSelector
            {
                Name = line[..idx].Trim(),
                Selector = line[(idx + 1)..].Trim()
            });
        }

        return new JobConfig
        {
            Browser = browser,
            Profile = profile,
            ScanSite = keywords[0],
            Keywords = keywords,
            TargetLinks = targets,
            MatchMode = mode,
            MaxGooglePages = (int)_numPages.Value,
            DelayMs = (int)_numDelay.Value,
            AutoRepeat = _chkAutoRepeat.Checked,
            Proxy = BrowserProxy.Parse(_txtProxy.Text),
            OutputDirectory = output,
            SaveHtml = _chkHtml.Checked,
            SaveCsv = _chkCsv.Checked,
            SaveJson = _chkJson.Checked,
            Selectors = selectors
        };
    }

    /// <summary>Luồng Bắt đầu: validate → mở Chromium → crawl. Gửi key scan chạy ngầm.</summary>
    async Task StartAsync()
    {
        if (_btnStop.Enabled)
            return;

        JobConfig config;
        try
        {
            config = ReadConfig();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cts = new CancellationTokenSource();
        _btnStart.Enabled = false;
        _btnStop.Enabled = true;
        _progress.MarqueeAnimationSpeed = 30;
        SetStatus(config.AutoRepeat ? "Đang chạy (tự động lặp lại)..." : "Đang chạy...");

        var log = new Progress<string>(AppendLog);
        BrowserSession? session = null;
        try
        {
            ScanApiClient.SendInBackground(config);

            session = await BrowserLauncher.ConnectOrLaunchAsync(
                config.Browser, config.Profile, config.DebugPort, log, _cts.Token, config.Proxy);
            var (folder, results) = await GoogleCrawlerService.RunAsync(config, session, log, _cts.Token);
            _lastRunFolder = folder;
            await CloseSessionAsync(session, log);
            session = null;

            if (_cts.IsCancellationRequested)
            {
                AppendLog("Đã dừng theo yêu cầu.");
                SetStatus("Đã dừng.");
                return;
            }

            var found = results.Count(r => r.Found);
            SetStatus($"Xong. Khớp {found}/{results.Count} từ khóa.");
            MessageBox.Show(this, $"Hoàn tất.\nKhớp {found}/{results.Count} từ khóa.\nKết quả: {_lastRunFolder}", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Đã dừng theo yêu cầu.");
            SetStatus("Đã dừng.");
        }
        catch (Exception ex)
        {
            AppendLog("LỖI: " + ex.Message);
            SetStatus("Lỗi.");
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (session != null)
                await CloseSessionAsync(session, log);
            _btnStart.Enabled = true;
            _btnStop.Enabled = false;
            _progress.MarqueeAnimationSpeed = 0;
            _progress.Value = 0;
            _cts.Dispose();
            _cts = null;
        }
    }

    static async Task CloseSessionAsync(BrowserSession session, IProgress<string> log)
    {
        try
        {
            await session.DisposeAsync();
            log.Report("Đã đóng trình duyệt.");
        }
        catch
        {
            // ignore
        }
    }

    void OpenLastFolder()
    {
        var dir = _lastRunFolder ?? _txtOutput.Text;
        if (!Directory.Exists(dir))
        {
            MessageBox.Show(this, "Chưa có thư mục kết quả.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });
    }

    void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }
        _lstLog.Items.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        _lstLog.TopIndex = Math.Max(0, _lstLog.Items.Count - 1);
        _lblStatus.Text = message;
    }

    void SetStatus(string text)
    {
        _lblStatus.Text = text;
    }

    /// <summary>Tách textarea thành list, bỏ dòng trống, không phân biệt hoa thường khi loại trùng.</summary>
    static List<string> Lines(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
