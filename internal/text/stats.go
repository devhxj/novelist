package text

import (
	"regexp"
	"strings"
	"unicode"
)

var enWordRe = regexp.MustCompile(`[a-zA-Z]+(?:'[a-zA-Z]+)?`)

// Stats 是文本的详细统计信息。
type Stats struct {
	ChineseChars    int `json:"chinese_chars"`
	EnglishWords    int `json:"english_words"`
	WordCount       int `json:"word_count"`        // 中文字符 + 英文单词
	LineCount       int `json:"line_count"`
	CharCountSpace  int `json:"char_count_space"`  // 计空格
	CharCountNoSpace int `json:"char_count_nospace"` // 不计空格
	ParagraphCount  int `json:"paragraph_count"`
}

// ComputeStats 返回文本的详细统计。单次遍历处理字符分类。
func ComputeStats(s string) Stats {
	st := Stats{}
	if s == "" {
		return st
	}

	runes := []rune(s)
	st.LineCount = strings.Count(s, "\n") + 1
	st.CharCountSpace = len(runes)

	cjk := 0
	spaces := 0
	inPara := false

	for _, r := range runes {
		if unicode.Is(unicode.Han, r) {
			cjk++
		} else if r == ' ' || r == '\t' || r == '\n' || r == '\r' {
			spaces++
		}

		if r == '\n' {
			if inPara {
				st.ParagraphCount++
				inPara = false
			}
		} else if !unicode.IsSpace(r) {
			inPara = true
		}
	}
	if inPara {
		st.ParagraphCount++
	}

	st.ChineseChars = cjk
	st.EnglishWords = len(enWordRe.FindAllString(s, -1))
	st.WordCount = cjk + st.EnglishWords
	st.CharCountNoSpace = st.CharCountSpace - spaces

	return st
}
