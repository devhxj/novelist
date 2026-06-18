package text

import (
	"strings"
	"testing"
)

func TestComputeStats_Empty(t *testing.T) {
	s := ComputeStats("")
	if s.WordCount != 0 || s.ChineseChars != 0 || s.EnglishWords != 0 {
		t.Errorf("empty text should be all zeros, got %+v", s)
	}
}

func TestComputeStats_PureChinese(t *testing.T) {
	s := ComputeStats("主角张三走进了房间。")
	if s.ChineseChars != 9 {
		t.Errorf("expected 9 Chinese chars, got %d", s.ChineseChars)
	}
	if s.EnglishWords != 0 {
		t.Errorf("expected 0 English words, got %d", s.EnglishWords)
	}
	if s.WordCount != 9 {
		t.Errorf("expected WordCount=9, got %d", s.WordCount)
	}
	if s.LineCount != 1 {
		t.Errorf("expected 1 line, got %d", s.LineCount)
	}
}

func TestComputeStats_PureEnglish(t *testing.T) {
	s := ComputeStats("Hello world, this is a test.")
	if s.EnglishWords != 6 {
		t.Errorf("expected 6 English words, got %d", s.EnglishWords)
	}
	if s.ChineseChars != 0 {
		t.Errorf("expected 0 Chinese chars, got %d", s.ChineseChars)
	}
	if s.WordCount != 6 {
		t.Errorf("expected WordCount=6, got %d", s.WordCount)
	}
}

func TestComputeStats_Mixed(t *testing.T) {
	s := ComputeStats("主角 ZhangSan 走进了 room。")
	if s.ChineseChars != 5 {
		t.Errorf("expected 5 Chinese chars, got %d", s.ChineseChars)
	}
	if s.EnglishWords != 2 {
		t.Errorf("expected 2 English words, got %d", s.EnglishWords)
	}
	if s.WordCount != 7 {
		t.Errorf("expected WordCount=7, got %d", s.WordCount)
	}
}

func TestComputeStats_MultiLine(t *testing.T) {
	text := "第一行内容。\n第二行内容。\n第三行内容。"
	s := ComputeStats(text)
	if s.LineCount != 3 {
		t.Errorf("expected 3 lines, got %d", s.LineCount)
	}
	if s.CharCountSpace != len([]rune(text)) {
		t.Errorf("CharCountSpace mismatch: got %d, expected %d", s.CharCountSpace, len([]rune(text)))
	}
}

func TestComputeStats_EnglishContraction(t *testing.T) {
	s := ComputeStats("don't can't it's")
	if s.EnglishWords != 3 {
		t.Errorf("contractions should count as single words, got %d", s.EnglishWords)
	}
}

func TestComputeStats_ParagraphCount(t *testing.T) {
	text := "第一段。\n\n第二段。\n\n第三段。"
	s := ComputeStats(text)
	if s.ParagraphCount != 3 {
		t.Errorf("expected 3 paragraphs, got %d", s.ParagraphCount)
	}
}

func TestComputeStats_SingleParagraph(t *testing.T) {
	s := ComputeStats("只有一段没有任何换行。")
	if s.ParagraphCount != 1 {
		t.Errorf("expected 1 paragraph, got %d", s.ParagraphCount)
	}
}

func TestComputeStats_TrailingNewline(t *testing.T) {
	text := "一段文字。\n"
	s := ComputeStats(text)
	if s.ParagraphCount != 1 {
		t.Errorf("trailing newline should not create extra paragraph, got %d", s.ParagraphCount)
	}
}

func TestComputeStats_Spaces(t *testing.T) {
	text := "中文  English  混合"
	s := ComputeStats(text)
	noSpace := len([]rune(text)) - strings.Count(text, " ")
	if s.CharCountNoSpace != noSpace {
		t.Errorf("CharCountNoSpace: got %d, expected %d", s.CharCountNoSpace, noSpace)
	}
}

func TestComputeStats_LargeText(t *testing.T) {
	// 构造一个较大文本确保无 panic
	var b strings.Builder
	for i := 0; i < 1000; i++ {
		b.WriteString("主角走进了一个宽阔的房间，墙上挂满了各种兵器。\n")
	}
	s := ComputeStats(b.String())
	if s.WordCount <= 0 {
		t.Errorf("large text should have words")
	}
	if s.LineCount != 1001 { // 1000 个 \n → 1001 行
		t.Errorf("expected 1001 lines, got %d", s.LineCount)
	}
}
