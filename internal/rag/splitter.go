package rag

import (
	"fmt"
	"strings"
	"unicode/utf8"
)

const (
	DefaultChunkSize = 420 // tokens，为 overlap 和边界效应留 92 token 余量
	DefaultOverlap   = 50
	BriefPreviewLen  = 280
)

// SplitText 将文本分割成指定大小的重叠块，优先在段落/句子边界切分。
// chunkSize 为 token 数上限，tokenizer 用于精确 BERT token 计数。
// overlap 字符从上一块的末尾复制到下一块的开头，防止语义在块边界被切断。
func SplitText(text string, chunkSize, overlap int, t *Tokenizer) []string {
	if text == "" {
		return nil
	}

	paragraphs := splitParagraphs(text)
	if len(paragraphs) == 0 {
		return nil
	}

	var chunks []string
	current := ""
	curLen := 0

	for _, para := range paragraphs {
		para = trimSpace(para)
		if para == "" {
			continue
		}

		paraLen := t.TokenCount(para)

		if curLen+paraLen+1 <= chunkSize {
			if current != "" {
				current += "\n" + para
				curLen += 1 + paraLen
			} else {
				current = para
				curLen = paraLen
			}
		} else {
			if current != "" {
				chunks = append(chunks, current)
			}

			if paraLen <= chunkSize {
				current = para
				curLen = paraLen
			} else {
				sentences := splitSentences(para)
				current = ""
				curLen = 0
				for _, sentence := range sentences {
					sentLen := t.TokenCount(sentence)
					if curLen+sentLen <= chunkSize {
						if current != "" {
							current += sentence
							curLen += sentLen
						} else {
							current = sentence
							curLen = sentLen
						}
					} else {
						if current != "" {
							chunks = append(chunks, current)
						}
						current = sentence
						curLen = sentLen
					}
				}
			}
		}
	}

	if current != "" {
		chunks = append(chunks, current)
	}

	// 重叠处理：从上一块尾部复制 overlap 个字符到下一块开头
	if overlap > 0 && len(chunks) > 1 {
		overlapped := make([]string, 0, len(chunks))
		overlapped = append(overlapped, chunks[0])
		for i := 1; i < len(chunks); i++ {
			prefix := tailRunes(chunks[i-1], overlap)
			overlapped = append(overlapped, prefix+chunks[i])
		}
		chunks = overlapped
	}

	return chunks
}

// BuildChapterChunks 为章节构建多层记忆块：summary、chapter_brief、正文分块。
// tokenizer 用于按 token 数精确分块。
func BuildChapterChunks(params ChapterChunkParams, t *Tokenizer) []Chunk {
	content := trimSpace(params.Content)
	summary := trimSpace(params.Summary)
	title := trimSpace(params.ChapterTitle)
	if title == "" {
		title = fmt.Sprintf("第%d章", params.ChapterNumber)
	}

	baseMeta := map[string]any{
		"chapter_number": params.ChapterNumber,
		"chapter_title":  title,
	}

	var chunks []Chunk

	// summary chunk
	if summary != "" {
		chunks = append(chunks, Chunk{
			ID:            fmt.Sprintf("%d_summary", params.ChapterNumber),
			Content:       summary,
			ChapterNumber: params.ChapterNumber,
			ChunkType:     "summary",
			ChunkIndex:    0,
			Metadata:      baseMeta,
		})
	}

	// chapter_brief chunk：标题 + 摘要 + 正文开头
	var briefParts []string
	briefParts = append(briefParts, title)
	if summary != "" {
		briefParts = append(briefParts, summary)
	}
	if content != "" {
		preview := content
		if utf8.RuneCountInString(preview) > BriefPreviewLen {
			preview = string([]rune(preview)[:BriefPreviewLen])
		}
		briefParts = append(briefParts, preview)
	}
	brief := strings.Join(briefParts, "\n")
	if brief != "" {
		chunks = append(chunks, Chunk{
			ID:            fmt.Sprintf("%d_brief", params.ChapterNumber),
			Content:       brief,
			ChapterNumber: params.ChapterNumber,
			ChunkType:     "chapter_brief",
			ChunkIndex:    0,
			Metadata:      baseMeta,
		})
	}

	// content chunks
	for i, chunk := range SplitText(content, DefaultChunkSize, DefaultOverlap, t) {
		chunks = append(chunks, Chunk{
			ID:            fmt.Sprintf("%d_%d", params.ChapterNumber, i),
			Content:       chunk,
			ChapterNumber: params.ChapterNumber,
			ChunkType:     "content",
			ChunkIndex:    i,
			Metadata:      baseMeta,
		})
	}

	return chunks
}

// ── helpers ──────────────────────────────────────────────

func splitParagraphs(text string) []string {
	parts := make([]string, 0)
	start := 0
	for i, r := range text {
		if r == '\n' {
			parts = append(parts, text[start:i])
			start = i + 1
		}
	}
	if start < len(text) {
		parts = append(parts, text[start:])
	}
	return parts
}

var sentenceRunes = map[rune]bool{
	'。': true, '！': true, '？': true, '；': true,
	'.': true, '!': true, '?': true, ';': true,
}

func splitSentences(text string) []string {
	var sentences []string
	var buf strings.Builder
	for _, r := range text {
		buf.WriteRune(r)
		if sentenceRunes[r] {
			if buf.Len() > 0 {
				sentences = append(sentences, buf.String())
				buf.Reset()
			}
		}
	}
	if buf.Len() > 0 {
		sentences = append(sentences, buf.String())
	}
	return sentences
}

func trimSpace(s string) string {
	start, end := 0, len(s)
	for start < end {
		r, size := utf8.DecodeRuneInString(s[start:])
		if !isSpace(r) {
			break
		}
		start += size
	}
	for end > start {
		r, size := utf8.DecodeLastRuneInString(s[:end])
		if !isSpace(r) {
			break
		}
		end -= size
	}
	return s[start:end]
}

func isSpace(r rune) bool {
	return r == ' ' || r == '\t' || r == '\n' || r == '\r' || r == '　'
}

// tailRunes 返回 s 末尾 n 个 rune，不做整串 []rune 转换。
func tailRunes(s string, n int) string {
	pos := len(s)
	for i := 0; i < n && pos > 0; i++ {
		_, size := utf8.DecodeLastRuneInString(s[:pos])
		pos -= size
	}
	return s[pos:]
}
