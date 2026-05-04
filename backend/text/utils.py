import re
import unicodedata
from dataclasses import dataclass

_CJK_RANGES = (
    (0x4E00, 0x9FFF),
    (0x3400, 0x4DBF),
    (0x20000, 0x2A6DF),
    (0x2A700, 0x2B73F),
    (0x2B740, 0x2B81F),
    (0x2B820, 0x2CEAF),
    (0xF900, 0xFAFF),
    (0x2F800, 0x2FA1F),
    (0x3000, 0x303F),
    (0xFF00, 0xFFEF),
)

_PUNCTUATION_CATEGORIES = frozenset({"Pc", "Pd", "Ps", "Pe", "Pi", "Pf", "Po"})

_ENGLISH_WORD_RE = re.compile(r"[a-zA-Z]+(?:'[a-zA-Z]+)?")


def _is_cjk(ch: str) -> bool:
    cp = ord(ch)
    for start, end in _CJK_RANGES:
        if start <= cp <= end:
            return True
    return False


def _is_punctuation(ch: str) -> bool:
    if unicodedata.category(ch) in _PUNCTUATION_CATEGORIES:
        return True
    cp = ord(ch)
    return (
        0x2000 <= cp <= 0x206F
        or 0x3000 <= cp <= 0x303F
        or 0xFF00 <= cp <= 0xFFEF
        or ch in "，。！？；：""''【】（）、—…《》"
    )


@dataclass
class TextStats:
    chinese_chars: int = 0
    english_words: int = 0
    spaces: int = 0
    punctuation: int = 0
    total_count: int = 0

    def to_dict(self) -> dict:
        return {
            "chinese_chars": self.chinese_chars,
            "english_words": self.english_words,
            "spaces": self.spaces,
            "punctuation": self.punctuation,
            "total_count": self.total_count,
        }


def count_words(text: str) -> int:
    if not text or not text.strip():
        return 0
    return compute_text_stats(text).total_count


def compute_text_stats(text: str) -> TextStats:
    if not text:
        return TextStats()

    chinese_chars = 0
    spaces = 0
    punctuation = 0

    for ch in text:
        if _is_cjk(ch):
            if _is_punctuation(ch):
                punctuation += 1
            else:
                chinese_chars += 1
        elif ch == " " or ch == "\t" or ch == "\n" or ch == "\r":
            spaces += 1
        elif _is_punctuation(ch):
            punctuation += 1

    english_words = len(_ENGLISH_WORD_RE.findall(text))

    total_count = chinese_chars + english_words

    return TextStats(
        chinese_chars=chinese_chars,
        english_words=english_words,
        spaces=spaces,
        punctuation=punctuation,
        total_count=total_count,
    )
