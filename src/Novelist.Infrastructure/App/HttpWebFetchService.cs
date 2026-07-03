using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class HttpWebFetchOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxChars { get; init; } = 15_000;

    public int MaxBytes { get; init; } = 10 * 1024 * 1024;

    public int MaxRedirects { get; init; } = 5;

    public TimeSpan MinDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMilliseconds(1500);
}

public sealed class DnsWebHostAddressResolver : IWebHostAddressResolver
{
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken)
    {
        return await Dns.GetHostAddressesAsync(host, cancellationToken);
    }
}

public sealed class HttpWebFetchService : IWebFetchService, IDisposable
{
    private const int GarbledMinChars = 50;
    private const int CompressionMinBytes = 1000;
    private const double CompressionThreshold = 0.75;
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly Regex CommentsRegex = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex UnwantedElementRegex = new("<(script|style|noscript|svg|canvas|iframe|nav|footer|aside|form|button|select|textarea)\\b[^>]*>.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex InputElementRegex = new("<(input|option)\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BodyRegex = new("<body\\b[^>]*>(?<content>.*?)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TitleRegex = new("<title\\b[^>]*>(?<content>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex OgTitleRegex = new("<meta\\b(?=[^>]*(?:property|name)\\s*=\\s*['\"](?:og:title|twitter:title|title)['\"])[^>]*content\\s*=\\s*['\"](?<content>.*?)['\"][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CandidateRegex = new("<(?<tag>article|main|section|div)\\b(?<attrs>[^>]*)>(?<content>.*?)</\\k<tag>>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new("<a\\b(?<attrs>[^>]*)>(?<content>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HrefRegex = new("href\\s*=\\s*['\"](?<href>.*?)['\"]", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CharsetRegex = new("charset\\s*=\\s*['\"]?\\s*(?<charset>[a-zA-Z0-9._-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpaceRegex = new("[ \\t\\f\\v]+", RegexOptions.Compiled);
    private static readonly Regex BlankLinesRegex = new("\\n{3,}", RegexOptions.Compiled);

    private static readonly string[] ContentAttributeTokens =
    [
        "article",
        "content",
        "post",
        "entry",
        "main",
        "body",
        "text",
        "story",
        "news"
    ];

    private static readonly string[] NavigationAttributeTokens =
    [
        "comment",
        "sidebar",
        "related",
        "recommend",
        "toolbar",
        "share",
        "ad-",
        "advert",
        "menu",
        "breadcrumb"
    ];

    private static readonly string[] AntiCrawlTitleTokens =
    [
        "just a moment",
        "attention required",
        "please verify",
        "are you a robot",
        "captcha",
        "安全检查",
        "请完成验证",
        "请验证您是",
        "正在检查您的浏览器",
        "请稍候",
        "访问被拒绝",
        "access denied",
        "403 forbidden",
        "请启用javascript",
        "please enable javascript",
        "您的浏览器需要",
        "系统检测到"
    ];

    private static readonly string[] AntiCrawlHeaderNames =
    [
        "cf-chl-bypass",
        "cf-mitigated",
        "x-sucuri-id",
        "x-sucuri-cache",
        "x-iinfo",
        "x-datadome",
        "x-fw-version",
        "x-edgeconnect-mid",
        "x-akamai-transformed"
    ];

    private static readonly string[] AntiCrawlServerTokens =
    [
        "cloudflare-nginx",
        "sucuri",
        "incapsula",
        "imperva"
    ];

    private static readonly string[] AntiCrawlCookieNames =
    [
        "cf_ob_info",
        "cf_use_ob",
        "incap_ses",
        "visid_incap",
        "ak_bmsc"
    ];

    private static readonly HashSet<string> BlockedHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "metadata.google.internal",
        "metadata.tencentyun.com"
    };

    private static readonly HashSet<string> BlockedLiteralHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "169.254.169.254",
        "100.100.100.200"
    };

    private readonly IWebHostAddressResolver _resolver;
    private readonly HttpClient _httpClient;
    private readonly HttpWebFetchOptions _options;
    private readonly bool _ownsHttpClient;

    public HttpWebFetchService(
        HttpClient? httpClient = null,
        IWebHostAddressResolver? resolver = null,
        HttpWebFetchOptions? options = null)
    {
        _resolver = resolver ?? new DnsWebHostAddressResolver();
        _options = options ?? new HttpWebFetchOptions();
        _httpClient = httpClient ?? CreateDefaultHttpClient(_resolver);
        _ownsHttpClient = httpClient is null;
    }

    public async ValueTask<WebFetchResultPayload> FetchAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var initialUri = await NormalizeAndValidateUriAsync(url, cancellationToken);
        await DelayBeforeRequestAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        using var response = await SendWithRedirectsAsync(initialUri, timeout.Token);
        if ((int)response.StatusCode >= 400)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
        }

        if (LooksLikeAntiCrawlResponse(response))
        {
            throw new InvalidOperationException("检测到反爬/CDN 防护响应头，无法抓取");
        }

        EnsureSupportedContentType(response.Content.Headers.ContentType);
        if (response.Content.Headers.ContentLength is { } length && length > _options.MaxBytes)
        {
            throw new InvalidOperationException($"网页过大，超过 {_options.MaxBytes >> 20} MB");
        }

        var body = await ReadContentLimitedAsync(response.Content, _options.MaxBytes, timeout.Token);
        var contentType = response.Content.Headers.ContentType;
        if (IsPlainText(contentType))
        {
            var plain = DecodeBody(body, contentType);
            plain = NormalizeMarkdown(WebUtility.HtmlDecode(plain));
            EnsureReadableText(string.Empty, plain, body.Length);
            return new WebFetchResultPayload(response.RequestMessage?.RequestUri?.ToString() ?? initialUri.ToString(), string.Empty, TruncateText(plain));
        }

        var html = DecodeBody(body, contentType);
        var title = ExtractTitle(html);
        var readableHtml = ExtractReadableHtml(html);
        var text = HtmlToMarkdown(readableHtml, response.RequestMessage?.RequestUri ?? initialUri);
        EnsureReadableText(title, text, body.Length);

        return new WebFetchResultPayload(
            response.RequestMessage?.RequestUri?.ToString() ?? initialUri.ToString(),
            title,
            TruncateText(text));
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async ValueTask<HttpResponseMessage> SendWithRedirectsAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var current = initialUri;
        for (var redirectCount = 0; redirectCount <= _options.MaxRedirects; redirectCount++)
        {
            using var request = CreateRequest(current);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException($"请求失败: {ex.Message}", ex);
            }

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            if (response.Headers.Location is null)
            {
                response.Dispose();
                throw new InvalidOperationException("重定向响应缺少 Location");
            }

            if (redirectCount >= _options.MaxRedirects)
            {
                response.Dispose();
                throw new InvalidOperationException("重定向次数过多");
            }

            var next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(current, response.Headers.Location);
            response.Dispose();
            current = await NormalizeAndValidateUriAsync(next.ToString(), cancellationToken);
        }

        throw new InvalidOperationException("重定向次数过多");
    }

    private HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,*/*;q=0.5");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.TryAddWithoutValidation("Sec-Ch-Ua", "\"Google Chrome\";v=\"149\", \"Chromium\";v=\"149\", \"Not.A/Brand\";v=\"99\"");
        request.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", "?0");
        request.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Platform", "\"Windows\"");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        return request;
    }

    private async ValueTask<Uri> NormalizeAndValidateUriAsync(
        string rawUrl,
        CancellationToken cancellationToken)
    {
        var normalized = (rawUrl ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("URL 不能为空。", nameof(rawUrl));
        }

        if (normalized.Length > 2048)
        {
            throw new ArgumentOutOfRangeException(nameof(rawUrl), normalized.Length, "URL 长度不能超过 2048。");
        }

        if (normalized.Any(ch => char.IsControl(ch)))
        {
            throw new ArgumentException("URL 不能包含控制字符。", nameof(rawUrl));
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("URL 格式无效。", nameof(rawUrl));
        }

        if (!IsHttpOrHttps(uri))
        {
            throw new ArgumentException("仅支持 http/https。", nameof(rawUrl));
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("URL 缺少主机名。", nameof(rawUrl));
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new ArgumentException("URL 不允许包含用户信息。", nameof(rawUrl));
        }

        await ValidateHostAsync(uri.Host, cancellationToken);
        return uri;
    }

    private async ValueTask ValidateHostAsync(string host, CancellationToken cancellationToken)
    {
        var normalizedHost = NormalizeHostForValidation(host);
        if (BlockedHostNames.Contains(normalizedHost) ||
            normalizedHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            BlockedLiteralHosts.Contains(normalizedHost) ||
            normalizedHost.Contains('%', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("禁止访问该地址");
        }

        IReadOnlyList<IPAddress> addresses;
        if (IPAddress.TryParse(normalizedHost, out var literalIp))
        {
            addresses = [literalIp];
        }
        else
        {
            addresses = await _resolver.ResolveAsync(normalizedHost, cancellationToken);
        }

        ValidateResolvedAddresses(normalizedHost, addresses);
    }

    private static void ValidateResolvedAddresses(string host, IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0)
        {
            throw new InvalidOperationException("DNS 解析无结果");
        }

        foreach (var address in addresses)
        {
            if (IsBlockedAddress(address))
            {
                throw new InvalidOperationException($"禁止访问内网地址: {address}");
            }
        }
    }

    private async ValueTask DelayBeforeRequestAsync(CancellationToken cancellationToken)
    {
        if (_options.MaxDelay <= TimeSpan.Zero || _options.MaxDelay < _options.MinDelay)
        {
            return;
        }

        var minMs = Math.Max(0, (int)_options.MinDelay.TotalMilliseconds);
        var maxMs = Math.Max(minMs, (int)_options.MaxDelay.TotalMilliseconds);
        if (maxMs == 0)
        {
            return;
        }

        var delay = Random.Shared.Next(minMs, maxMs + 1);
        await Task.Delay(delay, cancellationToken);
    }

    private static string ExtractTitle(string html)
    {
        var match = TitleRegex.Match(html);
        if (!match.Success)
        {
            match = OgTitleRegex.Match(html);
        }

        return match.Success
            ? NormalizeInlineText(StripTags(match.Groups["content"].Value))
            : string.Empty;
    }

    private static string ExtractReadableHtml(string html)
    {
        var cleaned = RemoveUnwantedHtml(html);
        var bodyMatch = BodyRegex.Match(cleaned);
        var body = bodyMatch.Success ? bodyMatch.Groups["content"].Value : cleaned;
        var candidates = new List<CandidateBlock> { new(body, string.Empty, "body") };

        foreach (Match match in CandidateRegex.Matches(body))
        {
            var attrs = match.Groups["attrs"].Value;
            var tag = match.Groups["tag"].Value;
            if (tag.Equals("article", StringComparison.OrdinalIgnoreCase) ||
                tag.Equals("main", StringComparison.OrdinalIgnoreCase) ||
                HasAnyToken(attrs, ContentAttributeTokens))
            {
                candidates.Add(new CandidateBlock(match.Groups["content"].Value, attrs, tag));
            }
        }

        return candidates
            .Select(candidate => new { Candidate = candidate, Score = ScoreCandidate(candidate) })
            .OrderByDescending(item => item.Score)
            .First()
            .Candidate
            .Html;
    }

    private static double ScoreCandidate(CandidateBlock candidate)
    {
        var text = PlainText(candidate.Html);
        var textLength = text.EnumerateRunes().Count();
        if (textLength == 0)
        {
            return 0;
        }

        var linkTextLength = LinkRegex.Matches(candidate.Html)
            .Select(match => PlainText(match.Groups["content"].Value).EnumerateRunes().Count())
            .Sum();
        var linkDensity = Math.Min(0.95, (double)linkTextLength / textLength);
        var score = textLength * (1 - linkDensity);
        score += Regex.Matches(candidate.Html, "<p\\b", RegexOptions.IgnoreCase).Count * 30;
        score += Regex.Matches(candidate.Html, "<h[1-6]\\b", RegexOptions.IgnoreCase).Count * 50;
        if (candidate.Tag.Equals("article", StringComparison.OrdinalIgnoreCase)) score += 250;
        if (candidate.Tag.Equals("main", StringComparison.OrdinalIgnoreCase)) score += 150;
        if (HasAnyToken(candidate.Attributes, ContentAttributeTokens)) score += 120;
        if (HasAnyToken(candidate.Attributes, NavigationAttributeTokens)) score -= 300;
        return score;
    }

    private static string HtmlToMarkdown(string html, Uri baseUri)
    {
        var work = RemoveUnwantedHtml(html);
        work = Regex.Replace(work, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        work = Regex.Replace(work, "<hr\\s*/?>", "\n\n---\n\n", RegexOptions.IgnoreCase);
        work = Regex.Replace(
            work,
            "<h([1-6])\\b[^>]*>(.*?)</h\\1>",
            match => "\n\n" + new string('#', int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)) + " " + NormalizeInlineText(match.Groups[2].Value) + "\n\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        work = Regex.Replace(
            work,
            "<li\\b[^>]*>(.*?)</li>",
            match => "\n- " + NormalizeInlineText(ConvertLinks(match.Groups[1].Value, baseUri)) + "\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        work = Regex.Replace(
            work,
            "<p\\b[^>]*>(.*?)</p>",
            match => "\n\n" + NormalizeInlineText(ConvertLinks(match.Groups[1].Value, baseUri)) + "\n\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        work = Regex.Replace(
            work,
            "<(blockquote|pre)\\b[^>]*>(.*?)</\\1>",
            match => "\n\n" + NormalizeInlineText(ConvertLinks(match.Groups[2].Value, baseUri)) + "\n\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        work = ConvertLinks(work, baseUri);
        work = Regex.Replace(work, "</?(article|main|section|div|ul|ol|table|tr|td|th|thead|tbody|header)\\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        work = StripTags(work);
        return NormalizeMarkdown(work);
    }

    private static string ConvertLinks(string html, Uri baseUri)
    {
        return LinkRegex.Replace(html, match =>
        {
            var label = NormalizeInlineText(match.Groups["content"].Value);
            if (string.IsNullOrWhiteSpace(label))
            {
                return string.Empty;
            }

            var hrefMatch = HrefRegex.Match(match.Groups["attrs"].Value);
            if (!hrefMatch.Success)
            {
                return label;
            }

            var href = WebUtility.HtmlDecode(hrefMatch.Groups["href"].Value).Trim();
            if (!Uri.TryCreate(baseUri, href, out var target) || !IsHttpOrHttps(target))
            {
                return label;
            }

            return $"[{label}]({target})";
        });
    }

    private static string PlainText(string html)
    {
        return NormalizeInlineText(StripTags(html));
    }

    private static string StripTags(string html)
    {
        var text = TagRegex.Replace(html, " ");
        return WebUtility.HtmlDecode(text);
    }

    private static string RemoveUnwantedHtml(string html)
    {
        var cleaned = CommentsRegex.Replace(html, " ");
        cleaned = UnwantedElementRegex.Replace(cleaned, " ");
        cleaned = InputElementRegex.Replace(cleaned, " ");
        return cleaned;
    }

    private static string NormalizeInlineText(string text)
    {
        text = StripTags(text);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = SpaceRegex.Replace(text, " ");
        text = Regex.Replace(text, " *\\n+ *", " ");
        return text.Trim();
    }

    private static string NormalizeMarkdown(string text)
    {
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = text.Split('\n').Select(line => SpaceRegex.Replace(line, " ").Trim()).ToArray();
        text = string.Join("\n", lines);
        text = BlankLinesRegex.Replace(text, "\n\n");
        return text.Trim();
    }

    private static void EnsureReadableText(string title, string text, int bodyLength)
    {
        var textLength = text.EnumerateRunes().Count();
        if (textLength == 0)
        {
            throw new InvalidOperationException("未提取到有效正文");
        }

        if (IsEncodingGarbled(text))
        {
            throw new InvalidOperationException("网页编码异常，无法解析");
        }

        if (IsAntiCrawl(title, textLength, bodyLength))
        {
            throw new InvalidOperationException("网页可能为反爬验证页面，无法抓取有效内容");
        }
    }

    private string TruncateText(string text)
    {
        var runes = text.EnumerateRunes().ToArray();
        if (runes.Length <= _options.MaxChars)
        {
            return text;
        }

        return string.Concat(runes.Take(_options.MaxChars)) + "\n\n...[内容已截断]";
    }

    private static bool IsAntiCrawl(string title, int extractedLength, int bodyLength)
    {
        var lowerTitle = title.ToLowerInvariant();
        if (AntiCrawlTitleTokens.Any(token => lowerTitle.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return bodyLength > 5000 && extractedLength < 200;
    }

    private static bool LooksLikeAntiCrawlResponse(HttpResponseMessage response)
    {
        if (AntiCrawlHeaderNames.Any(name => response.Headers.Contains(name) || response.Content.Headers.Contains(name)))
        {
            return true;
        }

        var server = string.Join(" ", response.Headers.Server.Select(item => item.ToString())).ToLowerInvariant();
        if (AntiCrawlServerTokens.Any(token => server.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                if (AntiCrawlCookieNames.Any(name => cookie.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsEncodingGarbled(string text)
    {
        var runeCount = text.EnumerateRunes().Count();
        if (runeCount < GarbledMinChars)
        {
            return false;
        }

        if (text.Contains('\uFFFD', StringComparison.Ordinal))
        {
            return true;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        return bytes.Length >= CompressionMinBytes && CompressionRatio(bytes) > CompressionThreshold;
    }

    private static double CompressionRatio(byte[] data)
    {
        using var target = new MemoryStream();
        using (var gzip = new GZipStream(target, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(data, 0, data.Length);
        }

        return (double)target.Length / data.Length;
    }

    private static async ValueTask<byte[]> ReadContentLimitedAsync(
        HttpContent content,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        await using var target = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (target.Length + read > limit)
            {
                throw new InvalidOperationException($"网页过大，超过 {limit >> 20} MB");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return target.ToArray();
    }

    private static void EnsureSupportedContentType(MediaTypeHeaderValue? contentType)
    {
        var mediaType = contentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return;
        }

        mediaType = mediaType.ToLowerInvariant();
        if (mediaType.StartsWith("text/", StringComparison.Ordinal) ||
            mediaType is "application/xhtml+xml" or "application/xml" or "text/xml" ||
            mediaType.EndsWith("+xml", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException($"不支持的内容类型: {mediaType}");
    }

    private static bool IsPlainText(MediaTypeHeaderValue? contentType)
    {
        return string.Equals(contentType?.MediaType, "text/plain", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeBody(byte[] body, MediaTypeHeaderValue? contentType)
    {
        var charset = contentType?.CharSet?.Trim('"', '\'') ?? DetectCharset(body);
        var encoding = EncodingFromCharset(charset);
        return encoding.GetString(body);
    }

    private static string DetectCharset(byte[] body)
    {
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
        {
            return "utf-8";
        }

        var prefix = Encoding.Latin1.GetString(body.AsSpan(0, Math.Min(body.Length, 4096)));
        var match = CharsetRegex.Match(prefix);
        return match.Success ? match.Groups["charset"].Value : "utf-8";
    }

    private static Encoding EncodingFromCharset(string? charset)
    {
        var normalized = (charset ?? string.Empty).Trim().Trim('"', '\'').ToLowerInvariant();
        return normalized switch
        {
            "" or "utf-8" or "utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            "utf-16" or "utf-16le" or "unicode" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            "us-ascii" or "ascii" => Encoding.ASCII,
            "iso-8859-1" or "latin1" or "latin-1" or "windows-1252" => Encoding.Latin1,
            _ => TryGetEncoding(normalized)
        };
    }

    private static Encoding TryGetEncoding(string charset)
    {
        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        }
        catch (NotSupportedException)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is >= 300 and < 400;
    }

    private static bool IsHttpOrHttps(Uri uri)
    {
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAnyToken(string text, IReadOnlyList<string> tokens)
    {
        return tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeHostForValidation(string host)
    {
        var normalized = host.Trim().Trim('[', ']').TrimEnd('.');
        return normalized.ToLowerInvariant();
    }

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                0 => true,
                10 => true,
                100 when bytes[1] is >= 64 and <= 127 => true,
                127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] is >= 16 and <= 31 => true,
                192 when bytes[1] == 168 => true,
                192 when bytes[1] == 0 && bytes[2] == 0 => true,
                192 when bytes[1] == 0 && bytes[2] == 2 => true,
                198 when bytes[1] is 18 or 19 => true,
                198 when bytes[1] == 51 && bytes[2] == 100 => true,
                203 when bytes[1] == 0 && bytes[2] == 113 => true,
                >= 224 => true,
                _ => false
            };
        }

        return address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast ||
            address.IsIPv6SiteLocal ||
            address.Equals(IPAddress.IPv6None) ||
            address.Equals(IPAddress.IPv6Any) ||
            IsUniqueLocalIpv6(address) ||
            IsDocumentationIpv6(address);
    }

    private static bool IsUniqueLocalIpv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC;
    }

    private static bool IsDocumentationIpv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 && bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8;
    }

    private static HttpClient CreateDefaultHttpClient(IWebHostAddressResolver resolver)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseProxy = false
        };
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var host = NormalizeHostForValidation(context.DnsEndPoint.Host);
            var addresses = IPAddress.TryParse(host, out var literalIp)
                ? [literalIp]
                : await resolver.ResolveAsync(host, cancellationToken);
            ValidateResolvedAddresses(host, addresses);

            Exception? lastError = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    lastError = ex;
                    socket.Dispose();
                    if (ex is OperationCanceledException)
                    {
                        throw;
                    }
                }
            }

            throw new HttpRequestException($"无法连接到 {host}", lastError);
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private sealed record CandidateBlock(string Html, string Attributes, string Tag);
}
