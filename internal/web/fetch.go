package web

import (
	"bytes"
	"fmt"
	"io"
	"math/rand/v2"
	"net"
	"net/http"
	"net/http/cookiejar"
	"net/url"
	"strings"
	"time"
	"unicode"

	md "github.com/JohannesKaufmann/html-to-markdown"
	"github.com/PuerkitoBio/goquery"
	"github.com/abadojack/whatlanggo"
	readability "codeberg.org/readeck/go-readability/v2"
)

const (
	fetchTimeout   = 30 * time.Second
	fetchMaxChars  = 32000
	fetchMaxBytes  = 10 << 20 // 10 MB
	fetchDelayMin  = 500 * time.Millisecond
	fetchDelayMax  = 1500 * time.Millisecond
	fetchUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"

	garbledMinTotal = 50 // 总字符数低于此值不检测（太短无法判断）
)

var cookieJar, _ = cookiejar.New(nil)

// FetchResult 是网页抓取结果。
type FetchResult struct {
	URL   string `json:"url"`
	Title string `json:"title"`
	Text  string `json:"text"`
}

// Fetch 抓取指定 URL 的网页内容，清洗后返回 markdown。
func Fetch(rawURL string) (*FetchResult, error) {
	u, err := parseAndValidate(rawURL)
	if err != nil {
		return nil, err
	}

	time.Sleep(fetchDelayMin + rand.N(fetchDelayMax-fetchDelayMin))

	client := &http.Client{
		Timeout: fetchTimeout,
		Jar:     cookieJar,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			if len(via) >= 5 {
				return fmt.Errorf("重定向次数过多")
			}
			if err := validateHost(req.URL.Host); err != nil {
				return fmt.Errorf("重定向目标不安全: %w", err)
			}
			return nil
		},
	}

	req, err := http.NewRequest("GET", u.String(), nil)
	if err != nil {
		return nil, fmt.Errorf("创建请求失败: %w", err)
	}
	req.Header.Set("User-Agent", fetchUserAgent)
	req.Header.Set("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8")
	req.Header.Set("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8")
	req.Header.Set("Cache-Control", "no-cache")
	req.Header.Set("Sec-Ch-Ua", `"Google Chrome";v="149", "Chromium";v="149", "Not.A/Brand";v="99"`)
	req.Header.Set("Sec-Ch-Ua-Mobile", "?0")
	req.Header.Set("Sec-Ch-Ua-Platform", `"Windows"`)
	req.Header.Set("Sec-Fetch-Dest", "document")
	req.Header.Set("Sec-Fetch-Mode", "navigate")
	req.Header.Set("Sec-Fetch-Site", "none")

	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("请求失败: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode >= 400 {
		return nil, fmt.Errorf("HTTP %d", resp.StatusCode)
	}

	body, err := io.ReadAll(io.LimitReader(resp.Body, fetchMaxBytes+1))
	if err != nil {
		return nil, fmt.Errorf("读取响应失败: %w", err)
	}
	if len(body) > fetchMaxBytes {
		return nil, fmt.Errorf("网页过大，超过 %d MB", fetchMaxBytes>>20)
	}

	// 先用 readability 提取正文区域
	article, err := readability.FromReader(bytes.NewReader(body), u)
	if err != nil {
		return nil, fmt.Errorf("提取正文失败: %w", err)
	}

	title := strings.TrimSpace(article.Title())

	// 将提取的正文 node 渲染为 HTML
	var contentBuf bytes.Buffer
	if article.Node != nil {
		if renderErr := article.RenderHTML(&contentBuf); renderErr != nil {
			return nil, fmt.Errorf("渲染正文失败: %w", renderErr)
		}
	}
	contentHTML := contentBuf.String()

	// readability 没提取到正文时回退用全文
	if strings.TrimSpace(contentHTML) == "" {
		doc, docErr := goquery.NewDocumentFromReader(bytes.NewReader(body))
		if docErr != nil {
			return nil, fmt.Errorf("解析 HTML 失败: %w", docErr)
		}
		contentSel := doc.Find("body")
		if contentSel.Length() == 0 {
			contentSel = doc.Find("html")
		}
		contentHTML, docErr = contentSel.Html()
		if docErr != nil {
			return nil, fmt.Errorf("提取正文失败: %w", docErr)
		}
	}

	converter := md.NewConverter("", true, nil)
	text, err := converter.ConvertString(contentHTML)
	if err != nil {
		return nil, fmt.Errorf("转换 markdown 失败: %w", err)
	}

	text = strings.TrimSpace(text)
	if isGarbled(text) {
		return nil, fmt.Errorf("网页内容为乱码，无法解析")
	}
	if len([]rune(text)) > fetchMaxChars {
		runes := []rune(text)
		text = string(runes[:fetchMaxChars]) + "\n\n...[内容已截断]"
	}

	return &FetchResult{
		URL:   rawURL,
		Title: title,
		Text:  text,
	}, nil
}

// isGarbled 检测文本是否为乱码，综合语言检测和结构分析。
func isGarbled(text string) bool {
	runes := []rune(text)
	if len(runes) < garbledMinTotal {
		return false
	}

	// 含替换字符 → 明确乱码
	if strings.ContainsRune(text, '�') {
		return true
	}

	// CJK 占比高 → 不可能是乱码（编码错误产不出大量有效汉字）
	var cjk int
	for _, r := range runes {
		if unicode.Is(unicode.Han, r) ||
			unicode.Is(unicode.Hiragana, r) ||
			unicode.Is(unicode.Katakana, r) ||
			unicode.Is(unicode.Hangul, r) {
			cjk++
		}
	}
	if cjk > len(runes)/3 {
		return false
	}

	// 语言检测：乱码的可信度通常极低
	info := whatlanggo.Detect(text)
	if info.Confidence < 0.3 {
		return true
	}

	return false
}

func parseAndValidate(rawURL string) (*url.URL, error) {
	u, err := url.Parse(rawURL)
	if err != nil {
		return nil, fmt.Errorf("URL 格式无效: %w", err)
	}
	if u.Scheme != "http" && u.Scheme != "https" {
		return nil, fmt.Errorf("仅支持 http/https")
	}
	if u.Host == "" {
		return nil, fmt.Errorf("URL 缺少主机名")
	}
	if u.User != nil {
		return nil, fmt.Errorf("URL 不允许包含用户信息")
	}
	if err := validateHost(u.Host); err != nil {
		return nil, err
	}
	return u, nil
}

func validateHost(host string) error {
	// 去掉端口
	if h, _, err := net.SplitHostPort(host); err == nil {
		host = h
	}

	// 阻止云 metadata 端点
	blocked := map[string]bool{
		"169.254.169.254":          true,
		"metadata.google.internal": true,
		"metadata.tencentyun.com":  true,
		"100.100.100.200":          true,
	}
	if blocked[host] {
		return fmt.Errorf("禁止访问该地址")
	}

	ips, err := net.LookupIP(host)
	if err != nil {
		return fmt.Errorf("DNS 解析失败: %w", err)
	}
	if len(ips) == 0 {
		return fmt.Errorf("DNS 解析无结果")
	}

	for _, ip := range ips {
		if isPrivate(ip) {
			return fmt.Errorf("禁止访问内网地址: %s", ip)
		}
	}

	return nil
}

func isPrivate(ip net.IP) bool {
	return ip.IsLoopback() || ip.IsLinkLocalUnicast() || ip.IsLinkLocalMulticast() ||
		ip.IsUnspecified() || ip.IsPrivate()
}
