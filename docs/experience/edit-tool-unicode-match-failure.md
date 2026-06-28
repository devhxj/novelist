# Edit 工具匹配含中文多行字符串失败的解决方法

## 问题

使用 Edit 工具修改 Go 源文件时，如果 `old_string` 包含中文字符，即使复制的文本与文件内容完全一致，Edit 工具也会报错 "String to replace not found"，并提示 "also tried swapping \uXXXX escapes and their characters; neither form matched"。

## 根因

Edit 工具在处理含 Unicode（中文）的多行字符串时，内部的 Unicode 转义（`\uXXXX`）匹配机制可能失效——文件实际存储的是 UTF-8 字节，而工具层可能做了额外的编解码导致字符串比对不一致。

## 解决方案（按优先级）

### 1. 加长 ASCII 上下文锚点

**最有效的方法。** 在 `old_string` 和 `new_string` 的前后多取几行纯 ASCII 代码作为锚点，让 Edit 工具基于 ASCII 锚点定位，而非依赖中文部分匹配。

```
# 失败：中文是匹配主体
old_string: "// 6. 自动创建 DB 记录..."
new_string: "// 6. 章节/大纲 DB 记录维护..."

# 成功：前后 ASCII 锚点包裹
old_string: "		approvalFeedback = approval.Feedback
	}

	// 6. 自动创建 DB 记录...
	}

	// 7. 写入前重读对比，阻止并发冲突"
new_string: "		approvalFeedback = approval.Feedback
	}

	// 6. 章节/大纲 DB 记录维护...
	}

	// 7. 写入前重读对比，阻止并发冲突"
```

### 2. gofmt 修复缩进一致性

编辑前确保文件通过 `gofmt` 格式化，避免 tab/space 混用导致匹配失败。

```bash
gofmt -w file.go
```

### 3. 用 cat -A 查看实际缩进字符

确认文件中是 tab（`^I`）还是空格，确保 Edit 工具参数中的缩进与文件一致。

```bash
sed -n '141,142p' file.go | cat -A
# 输出：^I表示tab，行尾$表示换行
```

Read 工具显示的缩进可能被视觉化为空格，实际文件可能是 tab。不要信任 Read 输出的视觉外观，用 `cat -A` 确认。
