using AutoClick.Models;
using Microsoft.Playwright;

namespace AutoClick.Services;

/// <summary>
/// Pipeline: mở Google → gõ từ khóa → quét kết quả → khớp link → click → lấy dữ liệu.
///
/// Chỗ hay sửa:
///   OpenGoogleSearchAsync     — URL Google, ô search (textarea[name=q])
///   ExtractResultUrlsAsync    — CSS lấy link kết quả (Google hay đổi)
///   GoNextGooglePageAsync     — nút trang sau
///   WaitForCaptchaIfNeededAsync — thời gian đợi giải CAPTCHA (mặc định 2 phút)
///   ProcessKeywordAsync       — cắt text 20000 ký tự, timeout selector
    ///   VisibleMouse / OsMouse  — kéo con trỏ Windows rồi click (thấy kim chuột)
/// </summary>
public static class GoogleCrawlerService
{
    public static async Task<(string RunFolder, IReadOnlyList<CrawlResult> Results)> RunAsync(
        JobConfig config,
        BrowserSession session,
        IProgress<string> log,
        CancellationToken ct)
    {
        var runFolder = ResultWriter.CreateRunFolder(config.OutputDirectory);
        log.Report("Thư mục kết quả: " + runFolder);

        var page = await session.NewWorkPageAsync();
        try { await page.BringToFrontAsync(); } catch { /* ignore */ }
        var results = new List<CrawlResult>();

        foreach (var keyword in config.Keywords)
        {
            ct.ThrowIfCancellationRequested();
            log.Report("-----");
            log.Report("Từ khóa: " + keyword);
            var item = await ProcessKeywordAsync(page, session, keyword, config, log, ct);
            if (config.SaveHtml && item.Found)
                ResultWriter.WriteHtml(runFolder, item);
            item.Html = null; // bỏ HTML khỏi RAM sau khi đã ghi file
            results.Add(item);
            await DelayAsync(config, ct);
        }

        if (config.SaveJson)
            ResultWriter.WriteJson(runFolder, results);
        if (config.SaveCsv)
            ResultWriter.WriteCsv(runFolder, results);

        log.Report("Hoàn tất. Đã lưu vào: " + runFolder);
        return (runFolder, results);
    }

    /// <summary>Xử lý 1 từ khóa: search → lật trang → khớp → click → crawl.</summary>
    static async Task<CrawlResult> ProcessKeywordAsync(
        IPage page,
        BrowserSession session,
        string keyword,
        JobConfig config,
        IProgress<string> log,
        CancellationToken ct)
    {
        try
        {
            await OpenGoogleSearchAsync(page, keyword, config, log, ct);
            if (await WaitForCaptchaIfNeededAsync(page, log, ct))
            {
                return new CrawlResult
                {
                    Keyword = keyword,
                    Found = false,
                    Error = "Google hiện CAPTCHA và hết thời gian chờ."
                };
            }

            string? matched = null;
            for (var googlePage = 1; googlePage <= config.MaxGooglePages; googlePage++)
            {
                ct.ThrowIfCancellationRequested();
                log.Report($"Quét trang Google {googlePage}/{config.MaxGooglePages}...");
                await DelayAsync(config, ct);

                var urls = await ExtractResultUrlsAsync(page);
                log.Report($"  Tìm thấy {urls.Count} link.");
                foreach (var sample in urls.Take(8))
                    log.Report("    • " + sample);

                matched = LinkMatcher.FindMatch(urls, config.TargetLinks, config.MatchMode);
                if (matched != null)
                {
                    log.Report("  Khớp: " + matched);
                    break;
                }

                if (urls.Count == 0)
                    log.Report("  Chưa lấy được organic link. URL hiện tại: " + page.Url);
                else
                {
                    var targetHosts = string.Join(", ", config.TargetLinks.Select(LinkMatcher.GetHost).Where(h => h.Length > 0).Distinct());
                    var serps = string.Join(", ", urls.Select(LinkMatcher.GetHost).Where(h => h.Length > 0).Distinct().Take(12));
                    log.Report($"  Chưa khớp. Target host: [{targetHosts}] | Host trên Google: [{serps}]");
                }

                if (googlePage < config.MaxGooglePages)
                {
                    var moved = await GoNextGooglePageAsync(page, log, ct);
                    if (!moved)
                    {
                        log.Report("  Không còn trang sau.");
                        break;
                    }
                }
            }

            if (matched == null)
            {
                return new CrawlResult
                {
                    Keyword = keyword,
                    Found = false,
                    Error = "Không tìm thấy link khớp trong kết quả Google."
                };
            }

            var targetPage = await OpenMatchedAsync(page, session, matched, log, ct);
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
                Text = Truncate(text, 20000), // tăng số này nếu cần lưu text dài hơn
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
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Report("Lỗi: " + ex.Message);
            return new CrawlResult
            {
                Keyword = keyword,
                Found = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>Mở google.com, tắt cookie banner nếu có, gõ từ khóa rồi Enter.</summary>
    static async Task OpenGoogleSearchAsync(IPage page, string keyword, JobConfig config, IProgress<string> log, CancellationToken ct)
    {
        log.Report("Mở Google...");
        // Đổi hl=vi thành hl=en nếu muốn Google tiếng Anh.
        await page.GotoAsync("https://www.google.com/?hl=vi", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 45000
        });
        await page.BringToFrontAsync();
        await DelayAsync(config, ct);
        await DismissConsentAsync(page, log);

        // Google hiện dùng textarea[name=q]; bản cũ là input[name=q].
        var box = page.Locator("textarea[name='q'], input[name='q']").First;
        if (await box.CountAsync() > 0)
        {
            var osClick = await VisibleMouse.ClickAsync(page, box, log, ct);
            if (!osClick)
                await box.ClickAsync(new() { Timeout = 8000 });
            await box.FillAsync("");
            await box.PressSequentiallyAsync(keyword, new() { Delay = 35 });
            await DelayAsync(config, ct);
            await page.Keyboard.PressAsync("Enter");
        }
        else
        {
            log.Report("Không thấy ô search — mở URL tìm kiếm trực tiếp.");
            var url = "https://www.google.com/search?hl=vi&num=10&q=" + Uri.EscapeDataString(keyword);
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
        }

        try
        {
            // #search / #rso = khối kết quả; captcha-form = khi bị chặn.
            await page.WaitForSelectorAsync("#search h3, #rso h3, a:has(h3), #captcha-form, iframe[src*='recaptcha']", new() { Timeout = 25000 });
        }
        catch (TimeoutException)
        {
            log.Report("Hết thời gian chờ kết quả Google.");
        }
    }

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
    /// Lấy URL đích (đã unwrap /url?q=). Ưu tiên link có h3 (tiêu đề organic).
    /// </summary>
    static async Task<List<string>> ExtractResultUrlsAsync(IPage page)
    {
        var urls = await page.EvaluateAsync<string[]>(@"() => {
            const unwrap = (href) => {
                if (!href) return '';
                try {
                    const u = new URL(href, location.origin);
                    if (u.pathname === '/url' || u.pathname.startsWith('/url')) {
                        const q = u.searchParams.get('q') || u.searchParams.get('url');
                        if (q && /^https?:/i.test(q)) return q;
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
                document.querySelectorAll('#search a:has(h3), #rso a:has(h3), a[jsname=""UWckNb""], .yuRUbf a, a[data-ved]'),
                document.querySelectorAll('#search a[href], #rso a[href], #center_col a[href], a[ping]')
            ];
            for (const nodes of groups) {
                for (const a of nodes) add(a.href || a.getAttribute('href') || '');
            }
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

    /// <summary>Bấm Next trên Google. Thêm aria-label nếu giao diện máy bạn khác.</summary>
    static async Task<bool> GoNextGooglePageAsync(IPage page, IProgress<string> log, CancellationToken ct)
    {
        var next = page.Locator("a#pnnext, a[aria-label='Next page'], a[aria-label='Trang sau'], a[aria-label='Tiếp'], a[aria-label='Next']").First;
        if (await next.CountAsync() == 0)
            return false;
        try
        {
            await next.ScrollIntoViewIfNeededAsync();
            var osClick = await VisibleMouse.ClickAsync(page, next, log, ct);
            if (!osClick)
                await next.ClickAsync(new() { Timeout = 8000 });
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 20000 });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Đánh dấu thẻ &lt;a&gt; khớp (viền cam), scroll vào giữa, rồi click chuột thật.
    /// Ép target=_self để thấy chuyển trang trên cùng tab.
    /// </summary>
    static async Task<IPage> OpenMatchedAsync(IPage page, BrowserSession session, string matched, IProgress<string> log, CancellationToken ct)
    {
        log.Report("Đang tìm thẻ <a> trên trang Google để click...");

        var marked = await page.EvaluateAsync<bool>(@"(matched) => {
            const unwrap = (href) => {
                if (!href) return '';
                try {
                    const u = new URL(href, location.origin);
                    if (u.pathname === '/url' || u.pathname.startsWith('/url')) {
                        const q = u.searchParams.get('q') || u.searchParams.get('url');
                        if (q && /^https?:/i.test(q)) return q;
                    }
                    return u.href;
                } catch { return href; }
            };
            const key = (u) => unwrap(u).replace(/^https?:\/\//i,'').replace(/^www\./i,'').replace(/[?#].*$/,'').replace(/\/$/,'').toLowerCase();
            const hostOf = (u) => {
                try { return new URL(unwrap(u), location.origin).hostname.replace(/^www\./i,'').toLowerCase(); }
                catch { return ''; }
            };
            const want = key(matched);
            const wantHost = hostOf(matched);
            const isHit = (real) => {
                const k = key(real);
                const h = hostOf(real);
                if (k && want && (k.includes(want) || want.includes(k))) return true;
                if (wantHost && h && (h === wantHost || h.endsWith('.' + wantHost) || wantHost.endsWith('.' + h))) return true;
                return false;
            };
            const groups = [
                document.querySelectorAll('#search a:has(h3), #rso a:has(h3), a[jsname=""UWckNb""]'),
                document.querySelectorAll('#search a[href], #rso a[href]')
            ];
            const seen = new Set();
            for (const nodes of groups) {
                for (const a of nodes) {
                    if (seen.has(a)) continue;
                    seen.add(a);
                    const real = unwrap(a.href || a.getAttribute('href') || '');
                    if (!isHit(real)) continue;
                    document.querySelectorAll('[data-autoclick-target]').forEach(el => {
                        el.removeAttribute('data-autoclick-target');
                        el.style.outline = '';
                    });
                    a.setAttribute('data-autoclick-target', '1');
                    a.setAttribute('target', '_self');
                    a.style.outline = '4px solid #ff6d00';
                    a.style.outlineOffset = '3px';
                    a.scrollIntoView({ block: 'center', inline: 'nearest' });
                    return true;
                }
            }
            return false;
        }", matched);

        if (!marked)
        {
            log.Report("Không thấy thẻ <a> khớp trên DOM — mở URL trực tiếp (không phải click).");
            await page.GotoAsync(matched, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            return page;
        }

        log.Report("Đã khoanh cam link khớp — kéo chuột Windows tới link rồi click...");
        await page.WaitForTimeoutAsync(400);

        var loc = page.Locator("a[data-autoclick-target='1']").First;
        var popupTask = session.Context.WaitForPageAsync(new() { Timeout = 8000 });
        var clicked = await VisibleMouse.ClickAsync(page, loc, log, ct);
        if (!clicked)
        {
            log.Report("Chuột Windows không click được, thử Playwright mouse.");
            try
            {
                var box = await loc.BoundingBoxAsync();
                if (box != null && box.Width > 2 && box.Height > 2)
                {
                    await page.Mouse.ClickAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
                    clicked = true;
                }
            }
            catch (Exception ex)
            {
                log.Report("Playwright mouse lỗi: " + ex.Message);
            }
        }

        IPage target = page;
        if (clicked)
        {
            try
            {
                var popup = await popupTask;
                session.OwnedPages.Add(popup);
                target = popup;
                log.Report("Link mở tab mới — chuyển sang tab đó.");
            }
            catch
            {
                target = page;
            }

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
                log.Report("Vẫn còn trang Google sau click, đợi redirect...");
                try
                {
                    await target.WaitForURLAsync(
                        url => !LinkMatcher.IsGoogleResultsUrl(url),
                        new() { Timeout = 15000 });
                }
                catch
                {
                    log.Report("Chưa rời Google — thử click JS native.");
                    await page.EvaluateAsync(@"() => {
                        const a = document.querySelector('a[data-autoclick-target=""1""]');
                        if (a) a.click();
                    }");
                    try
                    {
                        await target.WaitForURLAsync(
                            url => !LinkMatcher.IsGoogleResultsUrl(url),
                            new() { Timeout = 10000 });
                    }
                    catch
                    {
                        log.Report("Vẫn không vào được bằng click, mở URL trực tiếp.");
                        await page.GotoAsync(matched, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
                        target = page;
                    }
                }
            }
        }
        else
        {
            log.Report("Không click được — mở URL trực tiếp.");
            await page.GotoAsync(matched, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            target = page;
        }

        log.Report("Trang đích: " + target.Url);
        return target;
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

    static Task DelayAsync(JobConfig config, CancellationToken ct)
        => Task.Delay(Math.Max(200, config.DelayMs), ct); // sàn 200ms, tránh delay = 0 làm spam Google

    static string Truncate(string? value, int max)
    {
        value ??= "";
        return value.Length <= max ? value : value[..max] + "...";
    }
}
