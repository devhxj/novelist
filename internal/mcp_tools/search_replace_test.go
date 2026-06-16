package mcp_tools

import (
	"strings"
	"testing"
)

// ── searchReplace 四层匹配 ──────────────────────────────

func TestSearchReplaceExact(t *testing.T) {
	content := "陆沉渊从秦霜那里得到了消息。\n他决定亲自去调查这件事。"
	result, found, hint := searchReplace(content, "他决定亲自去调查这件事。", "他决定派手下去调查。", false)
	if !found {
		t.Fatalf("exact match should succeed, got hint: %s", hint)
	}
	if !strings.Contains(result, "派手下") {
		t.Errorf("replacement not in result: %s", result)
	}
	if strings.Contains(result, "亲自去调查") {
		t.Errorf("old text still present: %s", result)
	}
}

func TestSearchReplaceExactReplaceAll(t *testing.T) {
	content := "苹果 香蕉 苹果 橘子 苹果"
	result, found, _ := searchReplace(content, "苹果", "西瓜", true)
	if !found {
		t.Fatal("replaceAll should succeed")
	}
	if n := strings.Count(result, "西瓜"); n != 3 {
		t.Errorf("expected 3 replacements, got %d", n)
	}
}

func TestSearchReplaceExactOnlyFirst(t *testing.T) {
	content := "苹果 香蕉 苹果"
	result, found, _ := searchReplace(content, "苹果", "西瓜", false)
	if !found {
		t.Fatal("first-only should succeed")
	}
	if strings.Count(result, "西瓜") != 1 {
		t.Errorf("expected 1 replacement, got %d", strings.Count(result, "西瓜"))
	}
}

func TestSearchReplaceExactTrailingNewline(t *testing.T) {
	content := "第一行\n第二行\n"
	result, found, _ := searchReplace(content, "第二行\n", "替换行", false)
	if !found {
		t.Fatal("trailing newline should be trimmed before match")
	}
	if !strings.Contains(result, "替换行") {
		t.Error("replacement not found")
	}
}

// ── TrimSpace 兜底 ────────────────────────────────────────

func TestSearchReplaceTrimmedLeadingSpace(t *testing.T) {
	content := "第一行\n第二行\n第三行"
	result, found, _ := searchReplace(content, "  第二行", "替换", false)
	if !found {
		t.Fatal("leading whitespace should be trimmed")
	}
	if !strings.Contains(result, "替换") {
		t.Error("replacement not found")
	}
}

func TestSearchReplaceTrimmedTrailingSpace(t *testing.T) {
	content := "第一行\n第二行\n第三行"
	result, found, _ := searchReplace(content, "第二行  ", "替换", false)
	if !found {
		t.Fatal("trailing whitespace should be trimmed")
	}
	if !strings.Contains(result, "替换") {
		t.Error("replacement not found")
	}
}

func TestSearchReplaceTrimmedBothSides(t *testing.T) {
	content := "abc"
	result, found, _ := searchReplace(content, " abc ", "x", false)
	if !found {
		t.Fatal("whitespace on both sides should be trimmed")
	}
	if result != "x" {
		t.Errorf("expected 'x', got '%s'", result)
	}
}

// ── 标点归一化 ────────────────────────────────────────────

func TestNormalizePunctuation_CurlyToASCII(t *testing.T) {
	input := "“你好”" // “ = ", ” = "
	got := normalizePunctuation(input)
	if strings.Contains(got, "“") || strings.Contains(got, "”") {
		t.Errorf("curly quotes not normalized: %q", got)
	}
}

func TestNormalizePunctuation_CornerToASCII(t *testing.T) {
	input := "「事」" // 「事」
	got := normalizePunctuation(input)
	if strings.Contains(got, "「") || strings.Contains(got, "」") {
		t.Errorf("corner brackets not normalized: %q", got)
	}
}

func TestNormalizePunctuation_FullwidthToASCII(t *testing.T) {
	input := "＂你好＂" // ＂你好＂
	got := normalizePunctuation(input)
	if strings.Contains(got, "＂") {
		t.Errorf("fullwidth quotes not normalized: %q", got)
	}
}

func TestNormalizePunctuation_SingleQuotes(t *testing.T) {
	input := "‘他’" // ‘ = ', ’ = '
	got := normalizePunctuation(input)
	if strings.Contains(got, "‘") || strings.Contains(got, "’") {
		t.Errorf("single curly quotes not normalized: %q", got)
	}
}

func TestNormalizePunctuation_Idempotent(t *testing.T) {
	input := "“「＂‘"
	first := normalizePunctuation(input)
	second := normalizePunctuation(first)
	if first != second {
		t.Errorf("not idempotent: first=%q second=%q", first, second)
	}
}

func TestNormalizePunctuation_NoPunctuation(t *testing.T) {
	input := "hello world 你好世界 123"
	got := normalizePunctuation(input)
	if got != input {
		t.Errorf("text without punctuation should be unchanged: %q vs %q", input, got)
	}
}

// ── 标点归一化 + searchReplace 集成 ──────────────────────

func TestSearchReplaceCurlyQuoteInFile_AIUsesCornerQuote(t *testing.T) {
	// 文件使用弯引号
	content := "“从秦霜那里得知的。”陆沉渊摆了摆手，“他有他的消息渠道。”"
	// Q: 从秦霜那里得知的。Q 陆沉渊摆了摆手，Q 他有他的消息渠道。Q  (Q = 弯引号)

	// AI 用了直角引号
	searchText := "「从秦霜那里得知的。」陆沉渊摆了摆手，「他有他的消息渠道。」"
	replaceText := "已修改。"

	result, found, _ := searchReplace(content, searchText, replaceText, false)
	if !found {
		t.Fatal("corner quote to curly quote normalization should succeed")
	}
	if !strings.Contains(result, "已修改") {
		t.Errorf("replacement not found in: %s", result)
	}
}

func TestSearchReplaceFullwidthQuoteInFile_AIUsesCurlyQuote(t *testing.T) {
	// 文件使用全角引号 U+FF02
	content := "＂你好＂就走了。"
	// AI 用了弯引号
	searchText := "“你好”就走了。"
	_, found, _ := searchReplace(content, searchText, "再见。", false)
	if !found {
		t.Fatal("curly quote to fullwidth quote normalization should succeed")
	}
}

func TestSearchReplaceMixedPunctuationParagraph(t *testing.T) {
	// 真实小说段落：混合弯引号、直角引号、弯单引号
	content := "张三道：“听说「那件事」已经传开了。”\n李四冷笑：“传到‘他’耳朵里了？”\n张三摇头：“也许『那边』早就知道了。”"
	// 张三道：Q听说C那件事D已经传开了。Q / 李四冷笑：Q传到S他T耳朵里了？Q / 张三摇头：Q也许B那边F早就知道了。Q

	searchText := "张三道：“听说“那件事”已经传开了。”\n李四冷笑：“传到'他'耳朵里了？”"
	// AI 把直角/弯单引号都改成了弯双引号+ASCII单引号

	replaceText := "已修改。"
	result, found, _ := searchReplace(content, searchText, replaceText, false)
	if !found {
		t.Fatal("mixed punctuation normalization should succeed")
	}
	if !strings.Contains(result, "已修改") {
		t.Error("replacement not found")
	}
	if !strings.Contains(result, "张三摇头") {
		t.Error("lines after the match should remain unchanged")
	}
}

// ── 模糊匹配反馈 ──────────────────────────────────────────

func TestFuzzyHint_NearMatch(t *testing.T) {
	content := "第一章 黎明\n\n陆沉渊站在城墙上，望着远方的山脉。\n他已经等了三天，消息还没有来。\n城中百姓尚不知情。"
	searchText := "陆沉渊站在城墙上面，望着远方的山峦。"

	hint := fuzzyHint(searchText, content)
	if hint == "" {
		t.Fatal("hint should not be empty")
	}
	if !strings.Contains(hint, "城墙") {
		t.Errorf("hint should include matched content, got: %s", hint)
	}
	if !strings.Contains(hint, "自行判断") {
		t.Errorf("hint should tell LLM to judge, got: %s", hint)
	}
	if !strings.Contains(hint, "start_line") {
		t.Error("hint should include line_range_replace suggestion with exact line numbers")
	}
}

func TestFuzzyHint_NoMatch(t *testing.T) {
	content := "abcdefg\nhijklmn"
	searchText := "xyz123"

	hint := fuzzyHint(searchText, content)
	if hint == "" {
		t.Fatal("hint should always return a message")
	}
	if !strings.Contains(hint, "未找到任何相似") {
		t.Errorf("should indicate no match, got: %s", hint)
	}
}

func TestFuzzyHint_ExactMatchWouldHaveWorked(t *testing.T) {
	content := "这是一段完全相同的文本。"
	searchText := "这是一段完全相同的文本。"

	hint := fuzzyHint(searchText, content)
	if hint == "" {
		t.Fatal("hint should not be empty for exact match")
	}
	if strings.Contains(hint, "未找到任何相似") {
		t.Error("should find high similarity for exact match")
	}
}

func TestFuzzyHint_CrossLine(t *testing.T) {
	content := "第一段\n第二段内容\n第三段\n第四段"
	searchText := "第二段内容\n第三段"
	hint := fuzzyHint(searchText, content)
	if hint == "" || strings.Contains(hint, "未找到任何相似") {
		t.Error("should find cross-line match")
	}
}

func TestFuzzyHint_EmptyInput(t *testing.T) {
	if h := fuzzyHint("", "content"); h == "" {
		t.Error("empty search should return some message")
	}
	if h := fuzzyHint("search", ""); h == "" {
		t.Error("empty content should return some message")
	}
}

// ── partialRatio ──────────────────────────────────────────

func TestPartialRatio_ExactMatch(t *testing.T) {
	score := partialRatio("hello", "hello world")
	if score != 1.0 {
		t.Errorf("exact substring should score 1.0, got %.2f", score)
	}
}

func TestPartialRatio_NoMatch(t *testing.T) {
	score := partialRatio("abc", "xyz123")
	if score != 0.0 {
		t.Errorf("completely different strings should score 0.0, got %.2f", score)
	}
}

func TestPartialRatio_PartialMatch(t *testing.T) {
	score := partialRatio("hello", "hallo world")
	if score < 0.5 || score >= 1.0 {
		t.Errorf("partial match should score between 0.5 and 1.0, got %.2f", score)
	}
}

func TestPartialRatio_ShortInLong(t *testing.T) {
	score := partialRatio("陆沉渊", "陆沉渊站在城墙上望着远方")
	if score != 1.0 {
		t.Errorf("exact substring of Chinese text should score 1.0, got %.2f", score)
	}
}

func TestPartialRatio_ChinesePartialMatch(t *testing.T) {
	score := partialRatio("陆沉渊站在城墙上", "陆沉渊站在城门上望着远方")
	if score < 0.5 {
		t.Errorf("partial Chinese match should score >0.5, got %.2f", score)
	}
}

func TestPartialRatio_Symmetric(t *testing.T) {
	s1 := partialRatio("hello", "hallo hello world")
	s2 := partialRatio("hallo hello world", "hello")
	if s1 != 1.0 || s2 != 1.0 {
		t.Errorf("both should be 1.0 since 'hello' is a substring of longer text")
	}
}

// ── 错误消息格式 ──────────────────────────────────────────

func TestSearchReplaceHint_IsActionable(t *testing.T) {
	content := "第三章 相遇\n\n春雨绵绵，她撑着伞走过石桥。\n桥下的水面泛起层层涟漪。\n远处传来钟声。"
	searchText := "春雨纷纷，她撑着雨伞走过木桥。"

	_, found, hint := searchReplace(content, searchText, "替换", false)
	if found {
		t.Fatal("should not find mismatched text")
	}
	if !strings.Contains(hint, "相似度") {
		t.Error("hint should include similarity score")
	}
	if !strings.Contains(hint, "自行判断") {
		t.Error("hint should tell LLM to judge")
	}
	if !strings.Contains(hint, "start_line") || !strings.Contains(hint, "end_line") {
		t.Error("hint should include line_range_replace with line numbers")
	}
}

func TestSearchReplaceHint_TrailingNewlineNotAffected(t *testing.T) {
	content := "第一段\n第二段\n"
	_, found, _ := searchReplace(content, "第二段\n\n", "替换", false)
	if !found {
		t.Fatal("double trailing newline should still match via TrimSpace")
	}
}

// ── 真实场景回放 ──────────────────────────────────────────

func TestRealWorldQuoteMismatch(t *testing.T) {
	// 还原用户报告的 bug：文件中是弯引号 U+201C/U+201D，AI 的 search_text 用了 ASCII 引号 U+0022
	content := "“我从秦霜那里得知的。”陆沉渊摆了摆手，“他有他的消息渠道，我有我的。现在证据还不够，但已经开始指向同一个方向了。”"
	// "我从秦霜那里得知的。"陆沉渊摆了摆手，"他有他的消息渠道，我有我的。现在证据还不够，但已经开始指向同一个方向了。"

	searchText := "\"我从秦霜那里得知的。\"陆沉渊摆了摆手，\"他有他的消息渠道，我有我的。现在证据还不够，但已经开始指向同一个方向了。\""

	result, found, hint := searchReplace(content, searchText, "已修改", false)
	if !found {
		t.Fatalf("should match after normalization, got hint: %s", hint)
	}
	if !strings.Contains(result, "已修改") {
		t.Error("replacement should be present")
	}
}

func TestRealWorldLineRangeFallback(t *testing.T) {
	content := "白无垢，你来了。\n\n进来坐吧。\n\n我有话对你说。"
	searchText := "白无垢、你来了。"

	_, found, hint := searchReplace(content, searchText, "替换", false)
	if found {
		t.Fatal("punctuation difference should not match exactly")
	}
	if hint == "" {
		t.Fatal("should provide fuzzy hint")
	}
	if !strings.Contains(hint, "start_line=1, end_line=1") {
		t.Errorf("hint should include exact line numbers for line_range_replace, got: %s", hint)
	}
}

func TestRealWorldSingleQuotePunctuation(t *testing.T) {
	// 文件中是弯单引号 U+2018/U+2019，AI search_text 用了 ASCII 单引号
	content := "那是‘他的’东西，不是‘我的’。"
	// 那是'他的'东西，不是'我的'。
	searchText := "那是'他的'东西，不是'我的'。"

	result, found, _ := searchReplace(content, searchText, "这是别人的。", false)
	if !found {
		t.Fatal("single quote normalization should succeed")
	}
	if !strings.Contains(result, "这是别人的。") {
		t.Error("replacement not found")
	}
}

// ── 行号验证 ──────────────────────────────────────────────

func TestFuzzyHintLineNumbers_TopOfFile(t *testing.T) {
	// 匹配在最开头，行号应为 1
	content := "第一行内容\n第二行内容\n第三行内容\n第四行\n第五行"
	searchText := "第一行内容\n第二行"

	hint := fuzzyHint(searchText, content)
	if !strings.Contains(hint, "第 1-2 行") {
		t.Errorf("match at top should report line 1-2, got: %s", hint)
	}
	if !strings.Contains(hint, "start_line=1, end_line=2") {
		t.Errorf("should suggest line_range_replace(1,2), got: %s", hint)
	}
}

func TestFuzzyHintLineNumbers_MiddleOfFile(t *testing.T) {
	content := "第一行\n第二行\n第三行\n第四行\n第五行\n第六行\n第七行"
	searchText := "第三行\n第四行"

	hint := fuzzyHint(searchText, content)
	if !strings.Contains(hint, "第 3-4 行") {
		t.Errorf("match in middle should report line 3-4, got: %s", hint)
	}
	if !strings.Contains(hint, "start_line=3, end_line=4") {
		t.Errorf("should suggest line_range_replace(3,4), got: %s", hint)
	}
}

func TestFuzzyHintLineNumbers_SingleLine(t *testing.T) {
	content := "第一章\n\n第二段\n第三段\n第四段"
	searchText := "第二段"

	hint := fuzzyHint(searchText, content)
	if !strings.Contains(hint, "第 3-3 行") {
		t.Errorf("single-line match should report line 3-3, got: %s", hint)
	}
}

func TestFuzzyHintLineNumbers_BottomOfFile(t *testing.T) {
	content := "第一行\n第二行\n第三行\n第四行\n第五行"
	searchText := "第四行\n第五行"

	hint := fuzzyHint(searchText, content)
	if !strings.Contains(hint, "第 4-5 行") {
		t.Errorf("match at bottom should report line 4-5, got: %s", hint)
	}
}

func TestFuzzyHintLineNumbers_DeltaWindowLarge(t *testing.T) {
	// 搜索 2 行，但最佳匹配可能在 4 行窗口内（+2 delta）
	content := "第一章\n\n第二章开头\n第二章中间\n第二章结尾\n\n第三章"
	searchText := "第二章开头\n第二章中间" // 2 行
	// 如果 2 行匹配得分偏低，+2 窗口匹配 4 行得分更高，bestW 变为 4

	hint := fuzzyHint(searchText, content)
	// 不管用哪个窗口，行号范围必须跟 bestW 一致
	t.Logf("delta window hint: %s", hint)
	if hint == "" {
		t.Fatal("hint should not be empty")
	}
}

func TestLinePreview_SingleLine(t *testing.T) {
	content := "第一行\n第二行\n第三行"
	preview := linePreview(content, 2, 2)
	if !strings.Contains(preview, "1|") {
		t.Errorf("should show context line 1, got:\n%s", preview)
	}
	if !strings.Contains(preview, "2|") {
		t.Errorf("should show target line 2, got:\n%s", preview)
	}
	if !strings.Contains(preview, "3|") {
		t.Errorf("should show context line 3, got:\n%s", preview)
	}
	if !strings.Contains(preview, "改动区间") || !strings.Contains(preview, "改动结束") {
		t.Errorf("should mark the changed section, got:\n%s", preview)
	}
}

func TestLinePreview_FirstLine(t *testing.T) {
	content := "第一行\n第二行\n第三行"
	preview := linePreview(content, 1, 1)
	// 第一行往前没上下文，但 "改动区间" 标记仍在
	if !strings.Contains(preview, "1|第一行") {
		t.Errorf("should show line 1, got:\n%s", preview)
	}
	if strings.Contains(preview, "0|") {
		t.Errorf("should not show line 0 (before file start), got:\n%s", preview)
	}
}

func TestLinePreview_LastLine(t *testing.T) {
	content := "第一行\n第二行\n第三行"
	preview := linePreview(content, 3, 3)
	if !strings.Contains(preview, "3|第三行") {
		t.Errorf("should show line 3, got:\n%s", preview)
	}
	if strings.Contains(preview, "4|") {
		t.Errorf("should not show line 4 (past file end), got:\n%s", preview)
	}
	if !strings.Contains(preview, "2|第二行") {
		t.Errorf("should show context line 2, got:\n%s", preview)
	}
}

func TestLinePreview_MultiLine(t *testing.T) {
	content := "一行\n二行\n三行\n四行\n五行\n六行"
	preview := linePreview(content, 2, 4)
	// 改动区间 2-4，上下文 1 和 5
	if !strings.Contains(preview, "1|一行") {
		t.Errorf("should show context line 1, got:\n%s", preview)
	}
	if !strings.Contains(preview, "5|五行") {
		t.Errorf("should show context line 5, got:\n%s", preview)
	}
}

func TestLineRangeReplace_LineNumbers(t *testing.T) {
	content := "第一行\n第二行\n第三行\n第四行\n第五行"
	result, err := lineRangeReplace(content, 2, 4, "新内容")
	if err != nil {
		t.Fatal(err)
	}
	expected := "第一行\n新内容\n第五行"
	if result != expected {
		t.Errorf("expected %q, got %q", expected, result)
	}
}

func TestLineRangeReplace_FirstToLast(t *testing.T) {
	content := "a\nb\nc"
	result, err := lineRangeReplace(content, 1, 3, "x\ny")
	if err != nil {
		t.Fatal(err)
	}
	if result != "x\ny" {
		t.Errorf("expected full replace, got %q", result)
	}
}

func TestLineRangeReplace_EmptyNewContent(t *testing.T) {
	content := "第一行\n第二行\n第三行"
	result, err := lineRangeReplace(content, 2, 2, "")
	if err != nil {
		t.Fatal(err)
	}
	expected := "第一行\n第三行"
	if result != expected {
		t.Errorf("expected line removed, got %q", result)
	}
}

func TestLineRangeReplace_TrailingNewline(t *testing.T) {
	content := "第一行\n第二行\n第三行"
	// newContent 带尾部换行 → 结果中替换行后多一个空行
	result, err := lineRangeReplace(content, 2, 2, "替换行\n")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(result, "替换行\n\n第三行") {
		t.Errorf("trailing newline in newContent should keep blank line, got: %q", result)
	}
}

func TestLineRangeReplace_OutOfRange(t *testing.T) {
	_, err := lineRangeReplace("a\nb", 1, 5, "x")
	if err == nil {
		t.Error("should error when end > total lines")
	}
}

func TestLineRangeReplace_StartAfterEnd(t *testing.T) {
	_, err := lineRangeReplace("a\nb", 3, 1, "x")
	if err == nil {
		t.Error("should error when start > end")
	}
}
