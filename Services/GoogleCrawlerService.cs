using AutoClick.Models;
using Microsoft.Playwright;

namespace AutoClick.Services;

/// <summary>
/// Pipeline: mở Google → gõ từ khóa → quét kết quả → khớp link → click → lấy dữ liệu.
///
/// Chỗ hay sửa:
///   OpenGoogleSearchAsync     — URL Google, ô search (textarea[name=q])
///   ExtractResultUrlsAsync    — CSS lấy link organic + quảng cáo (Google hay đổi)
///   GoNextGooglePageAsync     — nút trang sau
///   WaitForCaptchaIfNeededAsync — thời gian đợi giải CAPTCHA (mặc định 2 phút)
///   ProcessKeywordAsync       — mỗi từ khóa vào hết các link mục tiêu thấy trên Google
///   RunAsync                  — hết bộ từ khóa thì đổi proxy (mở lại trình duyệt)
///   VisibleMouse / OsMouse    — kéo con trỏ Windows rồi click (thấy kim chuột)
/// </summary>
public static class GoogleCrawlerService
{
    public static async Task<(string RunFolder, IReadOnlyList<CrawlResult> Results)> RunAsync(
        JobConfig config,
        Func<BrowserProxy?, CancellationToken, Task<BrowserSession>> openSession,
        IProgress<string> log,
        CancellationToken ct)
    {
        var runFolder = ResultWriter.CreateRunFolder(config.OutputDirectory);
        log.Report("Thư mục kết quả: " + runFolder);

        var proxySlots = ProxySlots(config);
        var rotateProxies = proxySlots.Count > 1;
        var results = new List<CrawlResult>();
        var total = config.Keywords.Count;
        var round = 0;
        BrowserSession? session = null;
        IPage? page = null;
        BrowserProxy? currentProxy = null;

        void FlushResults()
        {
            if (config.SaveJson)
                ResultWriter.WriteJson(runFolder, results);
            if (config.SaveCsv)
                ResultWriter.WriteCsv(runFolder, results);
        }

        try
        {
            LogRunPlan(config, proxySlots, log);

            while (true)
            {
                round++;
                ct.ThrowIfCancellationRequested();
                var proxy = proxySlots[(round - 1) % proxySlots.Count];
                if (session == null || page == null || !SameProxy(currentProxy, proxy))
                {
                    if (session != null && rotateProxies)
                        log.Report($"Đóng cửa sổ để đổi proxy ({ProxyOrdinal(round, proxySlots.Count)})...");
                    session = await openSession(proxy, ct);
                    currentProxy = proxy;
                    page = await OpenWorkPageAsync(session, config, log);
                    if (proxy != null)
                        await BrowserLauncher.ReportExitIpAsync(session.Context, log, ct);
                }

                LogRoundStart(config, rotateProxies, round, total, proxy, proxySlots.Count, log);

                var roundStart = results.Count;
                var index = 0;
                foreach (var keyword in config.Keywords)
                {
                    index++;
                    ct.ThrowIfCancellationRequested();
                    log.Report("-----");
                    log.Report(UseRoundLabel(config, rotateProxies)
                        ? $"Lượt {round} — từ khóa {index}/{total}: {keyword}"
                        : $"Từ khóa {index}/{total}: {keyword}");
                    var items = await ProcessKeywordAsync(page, session, keyword, config, log, ct);
                    foreach (var item in items)
                    {
                        if (config.SaveHtml && item.Found)
                            ResultWriter.WriteHtml(runFolder, item);
                        item.Html = null;
                        results.Add(item);
                    }
                    await DelayAsync(config, ct);
                }

                FlushResults();
                if (UseRoundLabel(config, rotateProxies))
                {
                    var foundThisRound = results.Skip(roundStart).Count(r => r.Found);
                    log.Report($"Xong lượt {round}. Khớp {foundThisRound} link / {total} từ khóa.");
                }

                if (!ShouldContinue(config, rotateProxies, round, proxySlots.Count))
                    break;

                await DelayAsync(config, ct);
            }

            log.Report("Hoàn tất. Đã lưu vào: " + runFolder);
            return (runFolder, results);
        }
        catch (OperationCanceledException)
        {
            FlushResults();
            log.Report(round > 1
                ? $"Đã dừng sau {round} lượt. Đã lưu vào: " + runFolder
                : "Đã lưu kết quả vào: " + runFolder);
            return (runFolder, results);
        }
    }

    static List<BrowserProxy?> ProxySlots(JobConfig config)
        => config.Proxies.Count == 0
            ? [null]
            : config.Proxies.Cast<BrowserProxy?>().ToList();

    static bool SameProxy(BrowserProxy? a, BrowserProxy? b)
        => a == null ? b == null : a.SameAs(b);

    static bool UseRoundLabel(JobConfig config, bool rotateProxies)
        => config.AutoRepeat || rotateProxies;

    static bool ShouldContinue(JobConfig config, bool rotateProxies, int round, int proxyCount)
    {
        if (config.AutoRepeat)
            return true;
        return rotateProxies && round < proxyCount;
    }

    static void LogRunPlan(JobConfig config, IReadOnlyList<BrowserProxy?> slots, IProgress<string> log)
    {
        if (config.AutoRepeat)
            log.Report("Đã bật tự động lặp lại — quét hết từ khóa sẽ chạy lại từ đầu. Bấm Dừng để thoát.");

        if (config.Proxies.Count > 0)
            log.Report("Có proxy — mở Google tiếng Anh và quét cả quảng cáo (Sponsored).");

        if (slots.Count > 1)
        {
            log.Report(config.AutoRepeat
                ? $"Có {slots.Count} proxy — hết bộ từ khóa sẽ đổi proxy. Hết danh sách sẽ quay lại proxy đầu."
                : $"Có {slots.Count} proxy — chạy {slots.Count} vòng (mỗi vòng 1 proxy) rồi dừng.");
        }
    }

    static void LogRoundStart(
        JobConfig config,
        bool rotateProxies,
        int round,
        int keywordCount,
        BrowserProxy? proxy,
        int proxyCount,
        IProgress<string> log)
    {
        if (UseRoundLabel(config, rotateProxies))
        {
            log.Report(round > 1
                ? $"----- Đã quét hết {keywordCount} từ khóa. Bắt đầu lại từ đầu (lượt {round}) -----"
                : $"----- Lượt {round} -----");
        }

        if (proxyCount > 1)
            log.Report($"Proxy {ProxyOrdinal(round, proxyCount)}: {proxy?.HostPort ?? "(không dùng)"}");
        else if (proxy != null && round == 1)
            log.Report("Proxy: " + proxy.HostPort + (proxy.HasAuth ? " (có user/pass)" : ""));
    }

    static string ProxyOrdinal(int round, int proxyCount)
        => $"{((round - 1) % proxyCount) + 1}/{proxyCount}";

    static async Task<IPage> OpenWorkPageAsync(BrowserSession session, JobConfig config, IProgress<string> log)
    {
        var page = await session.NewWorkPageAsync();
        if (!config.Headless)
        {
            try { await page.BringToFrontAsync(); } catch { /* ignore */ }
            await session.ApplyMiniWindowAsync(page);
            log.Report($"Cửa sổ nửa trái màn hình {BrowserLauncher.MiniWindowWidth}x{BrowserLauncher.MiniWindowHeight}.");
        }
        else
        {
            log.Report("Chế độ chạy nền — không hiện cửa sổ trình duyệt.");
        }

        return page;
    }

    /// <summary>1 từ khóa: search → vào từng link mục tiêu thấy trên Google → crawl → back → tiếp.</summary>
    static async Task<List<CrawlResult>> ProcessKeywordAsync(
        IPage page,
        BrowserSession session,
        string keyword,
        JobConfig config,
        IProgress<string> log,
        CancellationToken ct)
    {
        var items = new List<CrawlResult>();
        try
        {
            await OpenGoogleSearchAsync(page, keyword, config, log, ct);
            if (await WaitForCaptchaIfNeededAsync(page, log, ct))
            {
                items.Add(new CrawlResult
                {
                    Keyword = keyword,
                    Found = false,
                    Error = "Google hiện CAPTCHA và hết thời gian chờ."
                });
                return items;
            }

            var remaining = config.TargetLinks.ToList();
            log.Report($"  Cần tìm {remaining.Count} link mục tiêu.");
            if (config.BouncePageRetry)
                await VisitMatchesWithBounceAsync(page, session, keyword, remaining, items, config, log, ct);
            else
                await VisitMatchesOnPagesAsync(page, session, remaining, items, keyword, config, log, ct);

            if (items.Count == 0)
            {
                log.Report("  Không thấy link mục tiêu nào, chuyển từ khóa tiếp theo.");
                items.Add(new CrawlResult
                {
                    Keyword = keyword,
                    Found = false,
                    Error = "Không tìm thấy link khớp trong kết quả Google."
                });
            }
            else if (remaining.Count > 0)
            {
                log.Report("  Còn link mục tiêu chưa thấy: " + string.Join(", ", remaining));
            }

            return items;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Report("Lỗi: " + ex.Message);
            if (items.Count == 0)
            {
                items.Add(new CrawlResult
                {
                    Keyword = keyword,
                    Found = false,
                    Error = ex.Message
                });
            }

            return items;
        }
    }

    static async Task<CrawlResult> CrawlMatchedAsync(
        IPage searchPage,
        BrowserSession session,
        string keyword,
        string matched,
        JobConfig config,
        IProgress<string> log,
        CancellationToken ct)
    {
        var targetPage = await OpenMatchedAsync(searchPage, session, matched, config, log, ct);
        await DelayAsync(config, ct);

        var title = await targetPage.TitleAsync();
        var text = await SafeInnerTextAsync(targetPage);
        var html = config.SaveHtml ? await targetPage.ContentAsync() : null;
        var result = new CrawlResult
        {
            Keyword = keyword,
            Found = true,
            MatchedUrl = matched,
            FinalUrl = targetPage.Url,
            Title = title,
            Text = Truncate(text, 20000),
            Html = html
        };

        foreach (var field in config.Selectors)
        {
            try
            {
                var loc = targetPage.Locator(field.Selector).First;
                var count = await loc.CountAsync();
                result.Fields[field.Name] = count == 0 ? "" : (await loc.InnerTextAsync(new() { Timeout = 5000 })).Trim();
            }
            catch (Exception ex)
            {
                result.Fields[field.Name] = "";
                log.Report($"  Selector '{field.Name}' lỗi: {ex.Message}");
            }
        }

        log.Report("  Title: " + Truncate(title, 120));
        await ScrollToBottomAndWaitAsync(targetPage, log, ct);
        await GoBackAfterVisitAsync(targetPage, searchPage, session, log, ct);
        return result;
    }

    /// <summary>Mở google.com, tắt cookie banner nếu có, gõ từ khóa rồi Enter.</summary>
    static async Task OpenGoogleSearchAsync(IPage page, string keyword, JobConfig config, IProgress<string> log, CancellationToken ct)
    {
        log.Report("Mở Google...");
        await page.GotoAsync(GoogleHomeUrl(config), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 45000
        });
        if (!config.Headless)
            await page.BringToFrontAsync();
        await DelayAsync(config, ct);
        await DismissConsentAsync(page, log);

        // Google hiện dùng textarea[name=q]; bản cũ là input[name=q].
        var box = page.Locator("textarea[name='q'], input[name='q']").First;
        if (await box.CountAsync() > 0)
        {
            await ClickElementAsync(page, box, config, log, ct);
            await box.FillAsync("");
            await box.PressSequentiallyAsync(keyword, new() { Delay = 35 });
            await DelayAsync(config, ct);
            await page.Keyboard.PressAsync("Enter");
        }
        else
        {
            log.Report("Không thấy ô search — mở URL tìm kiếm trực tiếp.");
            await page.GotoAsync(GoogleSearchUrl(keyword, config), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 45000
            });
        }

        try
        {
            // Organic + ads (Sponsored / Được tài trợ) + captcha.
            await page.WaitForSelectorAsync(
                "#search h3, #rso h3, a:has(h3), #tads, #tvcap, #tadsb, [data-text-ad], .uEierd h3, #captcha-form, iframe[src*='recaptcha']",
                new() { Timeout = 25000 });
        }
        catch (TimeoutException)
        {
            log.Report("Hết thời gian chờ kết quả Google.");
        }
    }

    /// <summary>
    /// Có proxy (thường US) thì không ép hl=vi — ads coupon US chỉ hiện trên Google tiếng Anh.
    /// </summary>
    static string GoogleHomeUrl(JobConfig config)
        => config.Proxies.Count > 0
            ? "https://www.google.com/?hl=en&pws=0"
            : "https://www.google.com/?hl=vi";

    static string GoogleSearchUrl(string keyword, JobConfig config)
        => config.Proxies.Count > 0
            ? "https://www.google.com/search?hl=en&pws=0&num=10&q=" + Uri.EscapeDataString(keyword)
            : "https://www.google.com/search?hl=vi&num=10&q=" + Uri.EscapeDataString(keyword);

    /// <summary>Bấm nút cookie nếu Google hiện. Thêm nhãn vào mảng labels nếu máy bạn hiện tiếng khác.</summary>
    static async Task DismissConsentAsync(IPage page, IProgress<string> log)
    {
        string[] labels = ["Accept all", "I agree", "Accept", "Đồng ý", "Chấp nhận tất cả", "Tôi đồng ý", "Reject all", "Từ chối"];
        foreach (var label in labels)
        {
            try
            {
                var btn = page.GetByRole(AriaRole.Button, new() { Name = label });
                if (await btn.CountAsync() > 0)
                {
                    await btn.First.ClickAsync(new() { Timeout = 2000 });
                    log.Report("Đã bấm nút cookie: " + label);
                    await page.WaitForTimeoutAsync(500);
                    return;
                }
            }
            catch
            {
                // try next label
            }
        }
    }

    /// <summary>return true = vẫn còn CAPTCHA hết giờ. Đổi AddMinutes(2) nếu muốn đợi lâu hơn.</summary>
    static async Task<bool> WaitForCaptchaIfNeededAsync(IPage page, IProgress<string> log, CancellationToken ct)
    {
        if (!await IsCaptchaAsync(page))
            return false;

        log.Report("Google hiện CAPTCHA. Hãy giải trên cửa sổ trình duyệt — app đợi tối đa 2 phút...");
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(3000, ct);
            if (!await IsCaptchaAsync(page))
            {
                log.Report("CAPTCHA đã xong, tiếp tục.");
                return false;
            }
        }
        return true;
    }

    static async Task<bool> IsCaptchaAsync(IPage page)
    {
        try
        {
            var url = page.Url ?? "";
            if (url.Contains("/sorry/", StringComparison.OrdinalIgnoreCase)
                || url.Contains("unusual_traffic", StringComparison.OrdinalIgnoreCase))
                return true;
            if (await page.Locator("iframe[src*='recaptcha'], #captcha-form, form#captcha").CountAsync() > 0)
                return true;
            var body = (await page.Locator("body").InnerTextAsync(new() { Timeout = 3000 })).ToLowerInvariant();
            return body.Contains("unusual traffic") || body.Contains("không phải là robot") || body.Contains("not a robot");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Lấy URL đích (đã unwrap /url?q= và adurl=). Gồm organic + Sponsored ads.
    /// </summary>
    static async Task<List<string>> ExtractResultUrlsAsync(IPage page)
    {
        var urls = await page.EvaluateAsync<string[]>(@"() => {
            const unwrap = (href, depth) => {
                if (!href) return '';
                depth = depth || 0;
                if (depth > 3) return href;
                try {
                    const u = new URL(href, location.origin);
                    const host = (u.hostname || '').toLowerCase();
                    const path = (u.pathname || '').toLowerCase();
                    const isGoogle = host.includes('google.') || host.includes('googleadservices.com')
                        || host.includes('googlesyndication.com') || host.includes('doubleclick.net');
                    const isRedirect = path === '/url' || path.startsWith('/url')
                        || path.includes('/aclk') || path.includes('/pagead/');
                    if (isGoogle && isRedirect) {
                        const dest = u.searchParams.get('adurl') || u.searchParams.get('q') || u.searchParams.get('url');
                        if (dest && /^https?:/i.test(dest)) return unwrap(dest, depth + 1);
                    }
                    return u.href;
                } catch { return href; }
            };
            const skipHost = (host) => {
                host = (host || '').toLowerCase();
                return host.includes('google.') || host.includes('gstatic.com')
                    || host.includes('googleusercontent.com') || host.includes('googleadservices.com')
                    || host.includes('googlesyndication.com') || host.endsWith('doubleclick.net');
            };
            const seen = new Set();
            const out = [];
            const add = (raw) => {
                const href = unwrap(raw || '');
                if (!href || href.startsWith('#') || href.toLowerCase().startsWith('javascript:')) return;
                try {
                    const u = new URL(href, location.origin);
                    if (u.protocol !== 'http:' && u.protocol !== 'https:') return;
                    if (skipHost(u.hostname)) return;
                    if (seen.has(u.href)) return;
                    seen.add(u.href);
                    out.push(u.href);
                } catch {}
            };
            const groups = [
                document.querySelectorAll('#tads a, #tvcap a, #tadsb a, #bottomads a, [data-text-ad] a, .uEierd a, [aria-label=""Ads""] a, [aria-label=""Sponsored""] a, [aria-label=""Được tài trợ""] a'),
                document.querySelectorAll('#search a:has(h3), #rso a:has(h3), a[jsname=""UWckNb""], .yuRUbf a, a[data-ved]'),
                document.querySelectorAll('#search a[href], #rso a[href], #center_col a[href], a[ping]')
            ];
            for (const nodes of groups) {
                for (const a of nodes) {
                    add(a.href || a.getAttribute('href') || '');
                    const pcu = a.getAttribute('data-pcu') || a.closest('[data-pcu]')?.getAttribute('data-pcu') || '';
                    pcu.split(/\s+/).forEach(add);
                }
            }
            document.querySelectorAll('[data-pcu]').forEach(el => {
                (el.getAttribute('data-pcu') || '').split(/\s+/).forEach(add);
            });
            for (const cite of document.querySelectorAll('cite')) {
                const a = cite.closest('a') || cite.closest('div')?.querySelector('a[href]');
                if (a) add(a.href);
                const text = (cite.innerText || '').trim();
                const host = text.match(/([a-z0-9-]+(?:\.[a-z0-9-]+)+)/i);
                if (host) add('https://' + host[1].replace(/^www\./i, ''));
            }
            return out;
        }");
        return urls.Select(LinkMatcher.UnwrapGoogleHref).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static async Task VisitMatchesOnPagesAsync(
        IPage page,
        BrowserSession session,
        List<string> remaining,
        List<CrawlResult> items,
        string keyword,
        JobConfig config,
        IProgress<string> log,
        CancellationToken ct)
    {
        for (var googlePage = 1; googlePage <= config.MaxGooglePages && remaining.Count > 0; googlePage++)
        {
            ct.ThrowIfCancellationRequested();
            await VisitMatchesOnCurrentPageAsync(
                page, session, keyword, remaining, items, googlePage, config.MaxGooglePages, config, log, ct);
            if (remaining.Count == 0)
                return;
            if (googlePage < config.MaxGooglePages)
            {
                var moved = await GoNextGooglePageAsync(page, config, log, ct);
                if (!moved)
                {
                    log.Report("  Không còn trang sau.");
                    break;
                }
            }
        }
    }

    /// <summary>Trang 1 → trang 2 → lại trang 1. Mỗi trang vào hết link mục tiêu còn lại rồi mới sang trang.</summary>
    static async Task VisitMatchesWithBounceAsync(
        IPage page,
        BrowserSession session,
        string keyword,
        List<string> remaining,
        List<CrawlResult> items,
        JobConfig config,
        IProgress<string> log,
        CancellationToken ct)
    {
        log.Report("Luồng trang 1 → 2 → lại trang 1.");
        await VisitMatchesOnCurrentPageAsync(page, session, keyword, remaining, items, 1, 2, config, log, ct);
        if (remaining.Count == 0)
            return;

        log.Report("  Còn link mục tiêu — click sang trang 2...");
        if (!await GoNextGooglePageAsync(page, config, log, ct))
        {
            log.Report("  Không sang được trang 2.");
            return;
        }

        await VisitMatchesOnCurrentPageAsync(page, session, keyword, remaining, items, 2, 2, config, log, ct);
        if (remaining.Count == 0)
            return;

        log.Report("  Còn link mục tiêu — về trang 1 tìm lại...");
        if (!await GoPrevGooglePageAsync(page, config, log, ct))
            log.Report("  Không click được về trang 1 — mở lại ô tìm kiếm.");

        await OpenGoogleSearchAsync(page, keyword, config, log, ct);
        if (await WaitForCaptchaIfNeededAsync(page, log, ct))
            return;

        await VisitMatchesOnCurrentPageAsync(page, session, keyword, remaining, items, 1, 2, config, log, ct);
    }

    static async Task VisitMatchesOnCurrentPageAsync(
        IPage page,
        BrowserSession session,
        string keyword,
        List<string> remaining,
        List<CrawlResult> items,
        int googlePage,
        int maxPages,
        JobConfig config,
        IProgress<string> log,
        CancellationToken ct)
    {
        var matches = await ListMatchesOnCurrentPageAsync(page, googlePage, maxPages, remaining, config, log, ct);
        var totalTargets = config.TargetLinks.Count;
        foreach (var matched in matches)
        {
            ct.ThrowIfCancellationRequested();
            if (!remaining.Any(t => LinkMatcher.IsMatch(matched, t, config.MatchMode)))
                continue;
            log.Report($"  Vào link mục tiêu ({items.Count + 1}/{totalTargets}): {matched}");
            var item = await CrawlMatchedAsync(page, session, keyword, matched, config, log, ct);
            items.Add(item);
            LinkMatcher.RemoveHitTargets(remaining, matched, config.MatchMode);
            await DelayAsync(config, ct);
        }
    }

    static async Task<List<string>> ListMatchesOnCurrentPageAsync(
        IPage page,
        int googlePage,
        int maxPages,
        IReadOnlyList<string> remaining,
        JobConfig config,
        IProgress<string> log,
        CancellationToken ct)
    {
        log.Report($"Quét trang Google {googlePage}/{maxPages} (còn {remaining.Count} link mục tiêu)...");
        await DelayAsync(config, ct);

        var urls = await ExtractResultUrlsAsync(page);
        log.Report($"  Tìm thấy {urls.Count} link (organic + quảng cáo).");
        foreach (var sample in urls.Take(8))
            log.Report("    • " + sample);

        var matches = LinkMatcher.FindMatches(urls, remaining, config.MatchMode);
        if (matches.Count > 0)
        {
            log.Report($"  Khớp {matches.Count} link mục tiêu trên trang này.");
            foreach (var url in matches)
                log.Report("    → " + url);
            return matches;
        }

        if (urls.Count == 0)
            log.Report("  Chưa lấy được organic link. URL hiện tại: " + page.Url);
        else
        {
            var targetHosts = string.Join(", ", remaining.Select(LinkMatcher.GetHost).Where(h => h.Length > 0).Distinct());
            var serps = string.Join(", ", urls.Select(LinkMatcher.GetHost).Where(h => h.Length > 0).Distinct().Take(12));
            log.Report($"  Chưa khớp. Target còn lại: [{targetHosts}] | Host trên Google: [{serps}]");
        }

        return matches;
    }

    /// <summary>Bấm Next trên Google. Thêm aria-label nếu giao diện máy bạn khác.</summary>
    static async Task<bool> GoNextGooglePageAsync(IPage page, JobConfig config, IProgress<string> log, CancellationToken ct)
    {
        var next = page.Locator("a#pnnext, a[aria-label='Next page'], a[aria-label='Trang sau'], a[aria-label='Tiếp'], a[aria-label='Next']").First;
        if (await next.CountAsync() == 0)
            return false;
        try
        {
            await next.ScrollIntoViewIfNeededAsync();
            await ClickElementAsync(page, next, config, log, ct);
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 20000 });
            return true;
        }
        catch
        {
            return false;
        }
    }

    static async Task<bool> GoPrevGooglePageAsync(IPage page, JobConfig config, IProgress<string> log, CancellationToken ct)
    {
        var prev = page.Locator(
            "a#pnprev, a[aria-label='Previous page'], a[aria-label='Trang trước'], a[aria-label='Trước'], a[aria-label='Previous']").First;
        if (await prev.CountAsync() == 0)
        {
            var page1 = page.Locator("a[aria-label='Page 1'], a[aria-label='Trang 1']").First;
            if (await page1.CountAsync() == 0)
                return false;
            prev = page1;
        }

        try
        {
            await prev.ScrollIntoViewIfNeededAsync();
            await ClickElementAsync(page, prev, config, log, ct);
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 20000 });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Đánh dấu thẻ &lt;a&gt; khớp, click N lần để mở tab mới, rồi làm việc trên tab cuối.
    /// </summary>
    static async Task<IPage> OpenMatchedAsync(IPage page, BrowserSession session, string matched, JobConfig config, IProgress<string> log, CancellationToken ct)
    {
        log.Report("Đang tìm thẻ <a> trên trang Google để click...");

        var clicks = Math.Max(1, config.OpenNewTabClicks);
        var markedDest = await MarkMatchedLinkAsync(page, matched);
        if (string.IsNullOrWhiteSpace(markedDest))
        {
            log.Report("Không thấy thẻ <a> khớp — mở tab trực tiếp.");
            IPage? last = null;
            for (var i = 1; i <= clicks; i++)
                last = await OpenForcedTabAsync(session, matched, log);
            return last ?? page;
        }

        log.Report("Đánh dấu đúng link: " + markedDest);
        log.Report($"Mở {clicks} tab mới bằng click trên thẻ đích (giữ trang Google).");
        await page.WaitForTimeoutAsync(300);

        var opened = new List<IPage>();
        void OnNewPage(object? _, IPage extra)
        {
            if (opened.Contains(extra) || extra.IsClosed)
                return;
            opened.Add(extra);
            if (!session.OwnedPages.Contains(extra))
                session.OwnedPages.Add(extra);
        }
        session.Context.Page += OnNewPage;
        try
        {
            for (var i = 1; i <= clicks; i++)
            {
                ct.ThrowIfCancellationRequested();
                await EnsureSearchPageAsync(page, log, ct);
                await page.BringToFrontAsync();

                markedDest = await MarkMatchedLinkAsync(page, matched);
                var loc = page.Locator("a[data-autoclick-target='1']").First;
                if (string.IsNullOrWhiteSpace(markedDest) || await loc.CountAsync() == 0)
                {
                    log.Report("  Không thấy thẻ đích, mở tab trực tiếp.");
                    var forced = await OpenForcedTabAsync(session, matched, log);
                    if (!opened.Contains(forced))
                        opened.Add(forced);
                    continue;
                }

                var before = opened.Count;
                log.Report($"  Click đích {i}/{clicks}: {markedDest}");
                await ClickOpenNewTabAsync(page, loc, config, log, ct);

                var got = await WaitForNewTabAsync(opened, before, 5000, ct);
                if (!got)
                {
                    log.Report("    Click không ra tab mới — mở tab trực tiếp.");
                    var tab = await OpenForcedTabAsync(session, matched, log);
                    if (!opened.Contains(tab))
                        opened.Add(tab);
                    continue;
                }

                var extra = opened[^1];
                await WaitUntilLeftGoogleAsync(extra);
                if (!TabHitsTarget(extra.Url, matched, config.MatchMode))
                {
                    log.Report("    Tab lệch sang " + extra.Url + " — đóng và click lại đúng thẻ.");
                    try { await extra.CloseAsync(); } catch { /* ignore */ }
                    opened.Remove(extra);
                    session.OwnedPages.Remove(extra);
                    before = opened.Count;
                    markedDest = await MarkMatchedLinkAsync(page, matched);
                    loc = page.Locator("a[data-autoclick-target='1']").First;
                    if (!string.IsNullOrWhiteSpace(markedDest) && await loc.CountAsync() > 0)
                        await ClickTargetWithPlaywrightAsync(loc, log);
                    got = await WaitForNewTabAsync(opened, before, 5000, ct);
                    if (got)
                    {
                        extra = opened[^1];
                        await WaitUntilLeftGoogleAsync(extra);
                    }
                    if (!got || !TabHitsTarget(opened[^1].Url, matched, config.MatchMode))
                    {
                        log.Report("    Vẫn lệch — mở đúng URL đích.");
                        var tab = await OpenForcedTabAsync(session, matched, log);
                        if (!opened.Contains(tab))
                            opened.Add(tab);
                        continue;
                    }
                }

                log.Report("    Đã mở tab: " + opened[^1].Url);

                await page.BringToFrontAsync();
                await DelayAsync(config, ct);
            }
        }
        finally
        {
            session.Context.Page -= OnNewPage;
        }

        var target = opened.LastOrDefault(p => !p.IsClosed);
        if (target == null)
        {
            log.Report("Không mở được tab mới — mở URL trên tab hiện tại.");
            await page.GotoAsync(matched, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            return page;
        }

        try { await target.BringToFrontAsync(); } catch { /* ignore */ }
        try
        {
            await target.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 25000 });
        }
        catch
        {
            // trang đích có thể chưa networkidle
        }

        if (LinkMatcher.IsGoogleResultsUrl(target.Url))
        {
            log.Report("Tab mới vẫn là Google — đợi redirect...");
            try
            {
                await target.WaitForURLAsync(
                    url => !LinkMatcher.IsGoogleResultsUrl(url),
                    new() { Timeout = 15000 });
            }
            catch
            {
                await target.GotoAsync(matched, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            }
        }

        log.Report($"Đã mở {opened.Count(p => !p.IsClosed)} tab. Làm việc trên tab cuối: " + target.Url);
        return target;
    }

    /// <summary>Đánh dấu đúng thẻ tiêu đề của domain đích. Trả về URL đích đã unwrap, rỗng nếu không thấy.</summary>
    static async Task<string> MarkMatchedLinkAsync(IPage page, string matched)
        => await page.EvaluateAsync<string>(@"(matched) => {
            const unwrap = (href, depth) => {
                if (!href) return '';
                depth = depth || 0;
                if (depth > 3) return href;
                try {
                    const u = new URL(href, location.origin);
                    const host = (u.hostname || '').toLowerCase();
                    const path = (u.pathname || '').toLowerCase();
                    const isGoogle = host.includes('google.') || host.includes('googleadservices.com')
                        || host.includes('googlesyndication.com') || host.includes('doubleclick.net');
                    const isRedirect = path === '/url' || path.startsWith('/url')
                        || path.includes('/aclk') || path.includes('/pagead/');
                    if (isGoogle && isRedirect) {
                        const dest = u.searchParams.get('adurl') || u.searchParams.get('q') || u.searchParams.get('url');
                        if (dest && /^https?:/i.test(dest)) return unwrap(dest, depth + 1);
                    }
                    return u.href;
                } catch { return href; }
            };
            const hostOf = (u) => {
                try { return new URL(unwrap(u), location.origin).hostname.replace(/^www\./i,'').toLowerCase(); }
                catch { return ''; }
            };
            const wantHost = hostOf(matched);
            const sameHost = (u) => {
                const h = hostOf(u);
                return !!(wantHost && h && (h === wantHost || h.endsWith('.' + wantHost) || wantHost.endsWith('.' + h)));
            };
            const cardOf = (a) => a.closest('.uEierd, [data-text-ad], .yuRUbf, div.g, .MjjYud, [data-sokoban-container]');
            const destsOf = (a) => {
                const list = [];
                const push = (raw) => {
                    (raw || '').split(/\s+/).forEach(x => { if (x) list.push(x); });
                };
                push(a.href || a.getAttribute('href') || '');
                push(a.getAttribute('data-pcu'));
                const card = cardOf(a);
                if (card) {
                    push(card.getAttribute('data-pcu'));
                    card.querySelectorAll('cite').forEach(cite => {
                        const host = (cite.innerText || '').match(/([a-z0-9-]+(?:\.[a-z0-9-]+)+)/i);
                        if (host) list.push('https://' + host[1]);
                    });
                }
                return list;
            };
            const skip = (a) => {
                const href = a.getAttribute('href') || '';
                if (!href || href.startsWith('#') || href.toLowerCase().startsWith('javascript:')) return true;
                const label = ((a.getAttribute('aria-label') || '') + ' ' + (a.innerText || '')).toLowerCase();
                return label.includes('about this result') || label.includes('thông tin về kết quả');
            };
            const score = (a) => {
                if (skip(a) || !destsOf(a).some(sameHost)) return -1;
                const r = a.getBoundingClientRect();
                if (r.width < 10 || r.height < 8) return -1;
                let s = 0;
                if (a.querySelector('h3') || a.closest('h3')) s += 300;
                if (a.getAttribute('jsname') === 'UWckNb') s += 80;
                if ((a.innerText || '').trim().length > 12) s += 20;
                s += Math.min(50, r.width / 8);
                return s;
            };

            const nodes = document.querySelectorAll(
                '#tads a, #tvcap a, #tadsb a, #bottomads a, [data-text-ad] a, .uEierd a, #search a, #rso a, #center_col a'
            );
            let best = null, bestScore = -1, bestDest = '';
            nodes.forEach(a => {
                const s = score(a);
                if (s <= bestScore) return;
                best = a;
                bestScore = s;
                const hit = destsOf(a).find(sameHost);
                bestDest = unwrap(hit || a.href || '');
            });
            if (!best) return '';

            document.querySelectorAll('[data-autoclick-target]').forEach(el => {
                el.removeAttribute('data-autoclick-target');
                el.style.outline = '';
            });
            best.setAttribute('data-autoclick-target', '1');
            best.setAttribute('target', '_blank');
            best.setAttribute('rel', 'noopener noreferrer');
            best.style.outline = '4px solid #ff6d00';
            best.style.outlineOffset = '3px';
            best.scrollIntoView({ block: 'center', inline: 'nearest' });
            return bestDest || unwrap(best.href || '');
        }", matched) ?? "";

    static async Task ClickOpenNewTabAsync(IPage page, ILocator loc, JobConfig config, IProgress<string> log, CancellationToken ct)
    {
        if (!config.Headless)
        {
            try { await VisibleMouse.MoveToAsync(page, loc, log, ct); }
            catch { /* vẫn click Playwright trên đúng thẻ */ }
        }

        await ClickTargetWithPlaywrightAsync(loc, log);
    }

    static async Task ClickTargetWithPlaywrightAsync(ILocator loc, IProgress<string> log)
    {
        try
        {
            var title = loc.Locator("h3").First;
            var click = await title.CountAsync() > 0 ? title : loc;
            await click.ClickAsync(new()
            {
                Timeout = 8000,
                Modifiers = [KeyboardModifier.Control],
                Force = false
            });
        }
        catch (Exception ex)
        {
            log.Report("    Playwright Ctrl+click lỗi: " + ex.Message);
            try
            {
                await loc.ClickAsync(new() { Timeout = 5000, Button = MouseButton.Middle, Force = true });
            }
            catch
            {
                // fallback mở tab trực tiếp ở vòng lặp ngoài
            }
        }
    }

    static bool TabHitsTarget(string? url, string matched, MatchMode mode)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        var dest = LinkMatcher.UnwrapGoogleHref(url);
        return LinkMatcher.IsMatch(dest, matched, mode)
               || LinkMatcher.IsMatch(url, matched, mode)
               || LinkMatcher.SameSite(dest, matched);
    }

    static async Task WaitUntilLeftGoogleAsync(IPage tab)
    {
        try
        {
            await tab.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 12000 });
        }
        catch
        {
            // trang ads có thể redirect chậm
        }

        try
        {
            await tab.WaitForURLAsync(
                url =>
                {
                    var dest = LinkMatcher.UnwrapGoogleHref(url);
                    return !LinkMatcher.IsGoogleResultsUrl(url) && !LinkMatcher.IsGoogleInternal(dest);
                },
                new() { Timeout = 12000 });
        }
        catch
        {
            // giữ URL hiện tại
        }
    }

    static async Task<bool> WaitForNewTabAsync(List<IPage> opened, int before, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (opened.Count > before && opened.Any(p => !p.IsClosed))
                return true;
            await Task.Delay(120, ct);
        }
        return opened.Count > before;
    }

    static async Task<IPage> OpenForcedTabAsync(BrowserSession session, string url, IProgress<string> log)
    {
        var tab = await session.Context.NewPageAsync();
        try { await tab.SetViewportSizeAsync(BrowserLauncher.MiniContentWidth, BrowserLauncher.MiniContentHeight); } catch { /* ignore */ }
        session.OwnedPages.Add(tab);
        try
        {
            await tab.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
        }
        catch (Exception ex)
        {
            log.Report("    Mở tab trực tiếp lỗi: " + ex.Message);
        }
        return tab;
    }

    static async Task EnsureSearchPageAsync(IPage page, IProgress<string> log, CancellationToken ct)
    {
        if (LinkMatcher.IsGoogleResultsUrl(page.Url))
            return;

        log.Report("  Tab Google đã rời kết quả — quay lại để click tiếp.");
        try
        {
            await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15000 });
        }
        catch
        {
            // lần click sau sẽ đánh dấu lại hoặc mở tab trực tiếp
        }
        await Task.Delay(200, ct);
    }

    static async Task<string> SafeInnerTextAsync(IPage page)
    {
        try
        {
            return await page.Locator("body").InnerTextAsync(new() { Timeout = 10000 });
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Cuộn từng đoạn tới cuối trang (nội dung lazy-load cũng kịp ra), rồi chờ 2 giây.</summary>
    static async Task ScrollToBottomAndWaitAsync(IPage page, IProgress<string> log, CancellationToken ct)
    {
        if (page.IsClosed)
            return;

        log.Report("  Cuộn xuống cuối trang...");
        try
        {
            await page.BringToFrontAsync();
            for (var i = 0; i < 20; i++)
            {
                ct.ThrowIfCancellationRequested();
                var atBottom = await page.EvaluateAsync<bool>(
                    "() => (window.innerHeight + window.scrollY) >= (document.body.scrollHeight - 40)");
                if (atBottom)
                    break;
                await page.Mouse.WheelAsync(0, 850);
                await page.WaitForTimeoutAsync(280);
            }

            await page.EvaluateAsync("() => window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' })");
            await page.WaitForTimeoutAsync(400);
            log.Report("  Đã tới cuối trang, chờ 2 giây...");
            await Task.Delay(2000, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Report("  Cuộn trang: " + ex.Message);
            await Task.Delay(2000, ct);
        }
    }

    /// <summary>Back về Google. Đóng mọi tab đích đã mở; cùng tab thì GoBack.</summary>
    static async Task GoBackAfterVisitAsync(IPage targetPage, IPage searchPage, BrowserSession session, IProgress<string> log, CancellationToken ct)
    {
        log.Report("  Back về Google, tiếp tục link mục tiêu / từ khóa tiếp theo...");
        try
        {
            var extras = session.OwnedPages
                .Where(p => !ReferenceEquals(p, searchPage) && !p.IsClosed)
                .ToList();
            foreach (var extra in extras)
            {
                try { await extra.CloseAsync(); } catch { /* ignore */ }
                session.OwnedPages.Remove(extra);
            }

            if (extras.Count > 0)
            {
                if (!searchPage.IsClosed)
                    await searchPage.BringToFrontAsync();
                return;
            }

            if (targetPage.IsClosed)
                return;

            await targetPage.GoBackAsync(new PageGoBackOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 20000
            });
        }
        catch (Exception ex)
        {
            log.Report("  GoBack lỗi, mở lại Google: " + ex.Message);
            try
            {
                if (!searchPage.IsClosed)
                {
                    await searchPage.GotoAsync("https://www.google.com/", new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 20000
                    });
                }
            }
            catch
            {
                // từ khóa sau sẽ tự mở Google
            }
        }

        await Task.Delay(400, ct);
    }

    static async Task<bool> ClickElementAsync(
        IPage page,
        ILocator loc,
        JobConfig config,
        IProgress<string> log,
        CancellationToken ct)
    {
        if (config.Headless)
        {
            await loc.ClickAsync(new() { Timeout = 8000 });
            return true;
        }

        var osClick = await VisibleMouse.ClickAsync(page, loc, log, ct);
        if (osClick)
            return true;

        log.Report("Chuột Windows không click được, thử Playwright.");
        try
        {
            var box = await loc.BoundingBoxAsync();
            if (box != null && box.Width > 2 && box.Height > 2)
            {
                await page.Mouse.ClickAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
                return true;
            }
            await loc.ClickAsync(new() { Timeout = 8000 });
            return true;
        }
        catch (Exception ex)
        {
            log.Report("Playwright mouse lỗi: " + ex.Message);
            return false;
        }
    }

    static Task DelayAsync(JobConfig config, CancellationToken ct)
        => Task.Delay(Math.Max(200, config.DelayMs), ct); // sàn 200ms, tránh delay = 0 làm spam Google

    static string Truncate(string? value, int max)
    {
        value ??= "";
        return value.Length <= max ? value : value[..max] + "...";
    }
}
