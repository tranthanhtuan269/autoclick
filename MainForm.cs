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
///   Chạy nền (ẩn cửa sổ trình duyệt)→ _chkHeadless.Checked
///   Luồng trang 1→2→lại 1           → _chkBouncePages.Checked
///   Số lần click mở tab mới         → _numNewTabs.Value
///   Thời gian giữa các lần click    → _numClickInterval.Value
///   Proxy trình duyệt (mỗi dòng 1)  → _txtProxy.Text
///   Thư mục lưu mặc định            → _txtOutput.Text
///   Nhãn tiếng Việt trên form       → BuildBrowserGroup / BuildSearchGroup
///   Validate trước khi chạy         → ReadConfig()
///   Bấm Bắt đầu                     → StartAsync() (gửi scan rồi crawl)
/// </summary>
public sealed class MainForm : Form
{
    readonly ComboBox _cboBrowser = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly TextBox _txtProxy = Multiline();
    readonly TextBox _txtKeywords = Multiline();
    readonly TextBox _txtTargets = Multiline();
    // Đổi Value = số trang Google mặc định / delay (ms) mặc định.
    readonly NumericUpDown _numPages = new() { Minimum = 1, Maximum = 20, Value = 3, Dock = DockStyle.Fill };
    readonly NumericUpDown _numDelay = new() { Minimum = 200, Maximum = 20000, Increment = 100, Value = 1500, Dock = DockStyle.Fill };
    readonly NumericUpDown _numNewTabs = new() { Minimum = 1, Maximum = 10, Value = 1, Dock = DockStyle.Fill };
    readonly NumericUpDown _numClickInterval = new() { Minimum = 50, Maximum = 5000, Increment = 50, Value = 200, Dock = DockStyle.Fill };
    readonly CheckBox _chkAutoRepeat = new()
    {
        Text = "Tự động lặp lại khi quét hết từ khóa",
        Checked = false,
        AutoSize = true
    };
    readonly CheckBox _chkHeadless = new()
    {
        Text = "Chạy nền (không hiện cửa sổ trình duyệt)",
        Checked = false,
        AutoSize = true
    };
    readonly CheckBox _chkBouncePages = new()
    {
        Text = "Không thấy trang 1 thì sang trang 2, rồi tìm lại trang 1",
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

        // Thư mục lưu mặc định. Đổi nếu muốn để Desktop hoặc D:\crawl.
        _txtOutput.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AutoClick", "results");
        // Text mẫu trên form — xóa hoặc đổi thành keyword/link bạn hay dùng.
        _txtKeywords.Text = "hakoreview" + Environment.NewLine + "hako" + Environment.NewLine + "bánh mì";
        _txtTargets.Text = "https://www.example.com";
        _txtSelectors.PlaceholderText = "title = h1" + Environment.NewLine + "mota = meta[name='description']";
        _txtProxy.PlaceholderText = "Để trống = không dùng. Mỗi dòng 1 proxy, ví dụ:" + Environment.NewLine +
            "1.2.3.4:8080" + Environment.NewLine +
            "1.2.3.4:8080:user:pass";
        var proxyTip = new ToolTip { AutoPopDelay = 14000, ShowAlways = true };
        proxyTip.SetToolTip(_txtProxy,
            "Mỗi dòng 1 proxy, không giới hạn số dòng.\n" +
            "HTTP: host:port hoặc host:port:user:pass\n" +
            "Hoặc user:pass@host:port\n" +
            "SOCKS5: socks5://host:port\n" +
            "Nhiều proxy: hết một bộ từ khóa sẽ đóng trình duyệt rồi mở lại với proxy tiếp theo.");
        proxyTip.SetToolTip(_chkAutoRepeat,
            "Bật: hết bộ từ khóa thì chạy lại từ đầu, tới khi bấm Dừng.\n" +
            "Nếu có nhiều proxy: mỗi vòng dùng proxy tiếp theo, hết danh sách thì quay lại đầu.");
        proxyTip.SetToolTip(_chkHeadless,
            "Bật: trình duyệt chạy ẩn, không hiện cửa sổ, click bằng Playwright.\n" +
            "Tắt: mở cửa sổ như bình thường.");
        proxyTip.SetToolTip(_chkBouncePages,
            "Bật: trang 1 không khớp → sang trang 2 → click lại trang 1 và tìm lại.\n" +
            "Vẫn còn link mục tiêu thì sang từ khóa tiếp theo. Không dùng ô số trang tối đa.");
        proxyTip.SetToolTip(_numNewTabs,
            "Khi khớp link mục tiêu: click bấy nhiêu lần, mỗi lần mở 1 tab mới.");
        proxyTip.SetToolTip(_numClickInterval,
            "Nghỉ giữa các lần click cùng một link mục tiêu.\n" +
            "Chỉ dùng khi số lần click > 1. Càng nhỏ thì click càng nhanh.");
        proxyTip.SetToolTip(_txtTargets,
            "Mỗi dòng 1 link/domain. Một từ khóa sẽ vào lần lượt mọi link mục tiêu thấy trên Google.");
        _chkBouncePages.CheckedChanged += (_, _) => _numPages.Enabled = !_chkBouncePages.Checked;

        Load += (_, _) =>
        {
            InitBrowsers();
            RestoreForm();
        };
        FormClosing += (_, _) =>
        {
            PersistForm();
            _cts?.Cancel();
        };
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
        var t = Grid(2);
        t.Controls.Add(Lbl("Trình duyệt"), 0, 0);
        t.Controls.Add(_cboBrowser, 1, 0);
        _txtProxy.Height = 96;
        t.Controls.Add(Lbl("Proxy" + Environment.NewLine + "(mỗi dòng 1, không giới hạn)"), 0, 1);
        t.Controls.Add(_txtProxy, 1, 1);
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
        t.Controls.Add(Lbl("Số trang Google tối đa"), 0, 2);
        t.Controls.Add(_numPages, 1, 2);
        t.Controls.Add(Lbl("Delay giữa thao tác (ms)"), 0, 3);
        t.Controls.Add(_numDelay, 1, 3);
        t.Controls.Add(Lbl("Số lần click mở tab mới"), 0, 4);
        t.Controls.Add(_numNewTabs, 1, 4);
        t.Controls.Add(Lbl("Thời gian giữa các lần click (ms)"), 0, 5);
        t.Controls.Add(_numClickInterval, 1, 5);
        t.Controls.Add(Lbl("Chế độ chạy"), 0, 6);
        var modes = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0)
        };
        modes.Controls.Add(_chkAutoRepeat);
        modes.Controls.Add(_chkHeadless);
        modes.Controls.Add(_chkBouncePages);
        t.Controls.Add(modes, 1, 6);
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
            return;

        var chromeIdx = -1;
        for (var i = 0; i < _cboBrowser.Items.Count; i++)
        {
            if (_cboBrowser.Items[i] is InstalledBrowser b
                && string.Equals(b.Channel, "chrome", StringComparison.OrdinalIgnoreCase))
            {
                chromeIdx = i;
                break;
            }
        }
        _cboBrowser.SelectedIndex = chromeIdx >= 0 ? chromeIdx : 0;
    }

    /// <summary>Đọc form → JobConfig. Thêm rule kiểm tra thì viết thêm throw ở đây.</summary>
    JobConfig ReadConfig()
    {
        if (_cboBrowser.SelectedItem is not InstalledBrowser browser)
            throw new InvalidOperationException("Chưa chọn trình duyệt.");
        var profile = ResolveProfile(browser);

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
            ScanSite = ScanApiClient.SuggestScanSite(null, keywords, targets)
                       ?? throw new InvalidOperationException(
                           "Không tạo được site scan từ từ khóa / domain mục tiêu."),
            Keywords = keywords,
            TargetLinks = targets,
            MatchMode = MatchMode.Contains,
            MaxGooglePages = (int)_numPages.Value,
            DelayMs = (int)_numDelay.Value,
            BouncePageRetry = _chkBouncePages.Checked,
            OpenNewTabClicks = (int)_numNewTabs.Value,
            ClickIntervalMs = (int)_numClickInterval.Value,
            AutoRepeat = _chkAutoRepeat.Checked,
            Headless = _chkHeadless.Checked,
            Proxies = BrowserProxy.ParseMany(_txtProxy.Text),
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
        SetStatus(StatusWhileRunning(config));

        PersistForm();
        var log = new Progress<string>(AppendLog);
        BrowserSession? session = null;
        try
        {
            try
            {
                await ScanApiClient.SendAsync(config);
            }
            catch
            {
                // Lỗi scan không chặn crawl.
            }

            var (folder, results) = await GoogleCrawlerService.RunAsync(
                config,
                async (proxy, token) =>
                {
                    if (session != null)
                    {
                        await CloseSessionAsync(session, log);
                        session = null;
                    }

                    session = await BrowserLauncher.ConnectOrLaunchAsync(
                        config.Browser, config.Profile, config.DebugPort, log, token, proxy, config.Headless);
                    return session;
                },
                log,
                _cts.Token);
            _lastRunFolder = folder;
            if (session != null)
            {
                await CloseSessionAsync(session, log);
                session = null;
            }

            if (_cts.IsCancellationRequested)
            {
                AppendLog("Đã dừng theo yêu cầu.");
                SetStatus("Đã dừng.");
                return;
            }

            var found = results.Count(r => r.Found);
            SetStatus($"Xong. Khớp {found}/{results.Count} từ khóa.");
            if (!config.Headless)
            {
                MessageBox.Show(this, $"Hoàn tất.\nKhớp {found}/{results.Count} từ khóa.\nKết quả: {_lastRunFolder}", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
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

    void PersistForm()
    {
        try
        {
            var s = UserSettings.Load();
            s.FormSaved = true;
            s.BrowserKind = (_cboBrowser.SelectedItem as InstalledBrowser)?.Kind;
            s.Proxy = _txtProxy.Text;
            s.Keywords = _txtKeywords.Text;
            s.Targets = _txtTargets.Text;
            s.MaxGooglePages = (int)_numPages.Value;
            s.DelayMs = (int)_numDelay.Value;
            s.OpenNewTabClicks = (int)_numNewTabs.Value;
            s.ClickIntervalMs = (int)_numClickInterval.Value;
            s.AutoRepeat = _chkAutoRepeat.Checked;
            s.Headless = _chkHeadless.Checked;
            s.BouncePageRetry = _chkBouncePages.Checked;
            s.Save();
        }
        catch
        {
            // không chặn đóng form / chạy job
        }
    }

    void RestoreForm()
    {
        var s = UserSettings.Load();
        if (!s.FormSaved)
            return;

        if (!string.IsNullOrWhiteSpace(s.Proxy))
            _txtProxy.Text = s.Proxy;
        if (!string.IsNullOrWhiteSpace(s.Keywords))
            _txtKeywords.Text = s.Keywords;
        if (!string.IsNullOrWhiteSpace(s.Targets))
            _txtTargets.Text = s.Targets;
        if (s.MaxGooglePages >= _numPages.Minimum && s.MaxGooglePages <= _numPages.Maximum)
            _numPages.Value = s.MaxGooglePages;
        if (s.DelayMs >= _numDelay.Minimum && s.DelayMs <= _numDelay.Maximum)
            _numDelay.Value = s.DelayMs;
        if (s.OpenNewTabClicks >= _numNewTabs.Minimum && s.OpenNewTabClicks <= _numNewTabs.Maximum)
            _numNewTabs.Value = s.OpenNewTabClicks;
        if (s.ClickIntervalMs >= _numClickInterval.Minimum && s.ClickIntervalMs <= _numClickInterval.Maximum)
            _numClickInterval.Value = s.ClickIntervalMs;
        _chkAutoRepeat.Checked = s.AutoRepeat;
        _chkHeadless.Checked = s.Headless;
        _chkBouncePages.Checked = s.BouncePageRetry;
        _numPages.Enabled = !_chkBouncePages.Checked;

        if (!string.IsNullOrWhiteSpace(s.BrowserKind))
        {
            for (var i = 0; i < _cboBrowser.Items.Count; i++)
            {
                if (_cboBrowser.Items[i] is InstalledBrowser b
                    && string.Equals(b.Kind, s.BrowserKind, StringComparison.OrdinalIgnoreCase))
                {
                    _cboBrowser.SelectedIndex = i;
                    break;
                }
            }
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

    static string StatusWhileRunning(JobConfig config)
    {
        var manyProxies = config.Proxies.Count > 1;
        if (config.Headless && config.AutoRepeat)
            return manyProxies ? "Đang chạy nền (lặp từ khóa, xoay proxy)..." : "Đang chạy nền (tự động lặp lại)...";
        if (config.Headless)
            return manyProxies ? "Đang chạy nền (xoay proxy)..." : "Đang chạy nền...";
        if (config.AutoRepeat)
            return manyProxies ? "Đang chạy (lặp từ khóa, xoay proxy)..." : "Đang chạy (tự động lặp lại)...";
        return manyProxies ? "Đang chạy (xoay proxy)..." : "Đang chạy...";
    }

    /// <summary>Profile không còn trên form — lấy Default hoặc profile đầu tiên để mở cửa sổ riêng.</summary>
    static BrowserProfileInfo ResolveProfile(InstalledBrowser browser)
    {
        var list = BrowserLauncher.ListProfiles(browser);
        return list.FirstOrDefault(p => p.FolderName.Equals("Default", StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault()
               ?? new BrowserProfileInfo { FolderName = "Default", DisplayName = "Default" };
    }

    /// <summary>Tách textarea thành list, bỏ dòng trống, không phân biệt hoa thường khi loại trùng.</summary>
    static List<string> Lines(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
