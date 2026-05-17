# RAG 可行性验证报告

## 测试目标

验证 Go 版 RAG 引擎的三个核心技术风险：ONNX Runtime Go 绑定、BERT Tokenizer 精度、sqlite-vec 向量存储。

## 测试环境

- OS: Ubuntu 24.04.1 LTS, x86_64
- Go: 1.25.0
- ONNX Runtime: 1.25.1（C 共享库来自 Python 版 venv）
- 模型: shibing624/text2vec-base-chinese（标准版 `model.onnx`，389MB）

## 步骤一：ONNX Runtime Go 绑定

### 目的

验证 `github.com/yalue/onnxruntime_go` 能否加载 389MB 的 ONNX 模型并执行推理。

### 方法

1. 使用 `ort.SetSharedLibraryPath()` 指定 `libonnxruntime.so` 路径
2. `ort.InitializeEnvironment()` 初始化环境
3. `ort.NewDynamicAdvancedSession()` 加载模型（输入: `input_ids/attention_mask/token_type_ids`，输出: `last_hidden_state`）
4. 构造假 token IDs（16 个占位 token），执行推理

### 结果

- 环境初始化成功，ONNX Runtime 版本: 1.25.1
- 模型加载成功
- 推理正常返回，输出 shape: `[1, 16, 768]`
- 输出 12288 个 float32 值，无 NaN

### 结论

✅ `yalue/onnxruntime_go` v1.30.1 与 ONNX Runtime 1.25.1 兼容，加载 389MB 模型推理正常。

---

## 步骤二：Tokenizer 一致性

### 目的

验证 Go 版 WordPiece Tokenizer 与 Python `AutoTokenizer` 输出完全一致。

### 方法

1. 读取 `vocab.txt`（21128 词）构建查找表
2. 实现分词算法：
   - 小写化
   - CJK 字符（`一-鿿` 等范围）逐字拆分
   - 标点符号独立拆分
   - 空格跳过
   - 连续字母/数字序列使用 WordPiece 贪婪最长匹配（## 前缀表示续字）
3. 7 条测试文本覆盖：纯中文、中英混合、英文字词拆分、标点

### 对比结果

| 测试文本 | Python tokens | Go tokens | 对比 |
|---------|-------------|----------|------|
| 你好世界 | `[872, 1962, 686, 4518]` | `[872, 1962, 686, 4518]` | PASS |
| 这是一个测试句子。 | `[6821, 3221, 671, 702, 3844, 6407, 1368, 2094, 511]` | 同上 | PASS |
| 主角张三走进房间，看到李四正在等待。 | 18 tokens | 同上 | PASS |
| The story begins with a dark night. | `[8174, 10076, 8815, 10822, 8118, 8663, 143, 12705, 10036, 119]` | 同上 | PASS |
| 系统提示：请用户输入验证码！ | 14 tokens | 同上 | PASS |
| hello world 你好 | `[8701, 8572, 872, 1962]` | 同上 | PASS |
| Hello World | `[8701, 8572]` | 同上（小写化验证） | PASS |

### 结论

✅ Go tokenizer 与 Python `AutoTokenizer` 输出逐位一致。WordPiece 贪婪匹配、CJK 拆分、小写化、## 前缀逻辑全部正确。

---

## 步骤三：Embedding 精度

### 目的

验证 Go ONNX 推理 + mean pooling 后的最终 embedding 向量与 Python 一致。

### 方法

1. 使用相同 tokenizer 输出（已验证一致）构造输入张量
2. ONNX 推理得到 `last_hidden_state [1, seq_len, 768]`
3. 使用 attention_mask 做 mean pooling（python: `(hidden * mask).sum(1) / mask.sum(1)`）
4. 8 条文本（短句到 33 tokens），对比 cosine similarity

### 对比结果

| 文本 | cos_sim | 状态 |
|------|---------|------|
| 你好世界 | 1.00000000 | PASS |
| 这是一个测试句子。 | 1.00000004 | PASS |
| 主角张三走进房间，看到李四正在等待。 | 1.00000004 | PASS |
| The story begins with a dark night. | 0.99999999 | PASS |
| 系统提示：请用户输入验证码！ | 1.00000000 | PASS |
| hello world 你好 | 0.99999999 | PASS |
| 人工智能正在改变小说创作的方式... | 1.00000005 | PASS |
| 在遥远的东方大陆，有一座古老的城池... | 1.00000006 | PASS |

> cos_sim 微小数点波动（10⁻⁸ 级别）为 Go float32 与 Python numpy.float32 的浮点运算顺序差异，完全在可接受范围内。

### 结论

✅ Go 端 embedding 与 Python 端完全一致，cosine similarity > 0.999（阈值），实际均为 0.99999999+。

---

## 步骤四：sqlite-vec 向量存储

### 目的

验证 sqlite-vec 能否在 Go 中正常工作：建表、插入向量、KNN 查询。

### 方法

1. 使用 `github.com/asg017/sqlite-vec-go-bindings/cgo` + `github.com/mattn/go-sqlite3`
2. `sqlite_vec.Auto()` 启用扩展
3. 创建 `vec0` 虚拟表（`embedding float[768]`）
4. 用 `sqlite_vec.SerializeFloat32()` 序列化，插入 100 个 768 维向量
5. 用 KNN 查询检索最近邻

### 结果

```
vec_version: v0.1.6
Vector table created ✓
100 vectors inserted ✓
KNN query (top 5):
  rowid=50 distance=0.000000
  rowid=51 distance=0.036084
  rowid=49 distance=0.036084
  rowid=52 distance=0.072169
  rowid=48 distance=0.072169
Closest match: rowid=50 (expected ~50) ✓
```

### 结论

✅ sqlite-vec CGO 绑定工作正常，建表、插入、KNN 查询全链路通过。查询向量与 #50 最接近，距离为 0，结果正确。

---

## 依赖修正

原设计文档（`go-rewrite-design.md`）第十二节依赖列表需修正：

| 原设计 | 修正 | 说明 |
|--------|------|------|
| `modernc.org/sqlite` | `github.com/mattn/go-sqlite3` | sqlite-vec 仅支持 CGO 路径，`modernc` 是纯 Go，不兼容 |
| `github.com/asg017/sqlite-vec-go` | `github.com/asg017/sqlite-vec-go-bindings/cgo` | 实际包名不同 |

ONNX Runtime 本身需要 CGO，因此全部使用 CGO 路径不会引入额外约束。

## 编译环境要求

- `libsqlite3-dev`（提供 `sqlite3.h`）
- `libonnxruntime.so`（从 ONNX Runtime 发布版或 Python wheel 中获取）
- CGO 编译标志：
  - `CGO_CFLAGS=-I<go-sqlite3 目录>`
  - `CGO_LDFLAGS=-L<onnxruntime 目录> -lonnxruntime`

## 总体结论

RAG 技术方案可行性验证 **全部通过**。Go 可以：
1. 加载 text2vec-base-chinese ONNX 模型并推理
2. Tokenize 中文文本，输出与 Python 完全一致
3. 生成与 Python 精度相同（cos_sim > 0.999）的 embedding
4. 使用 sqlite-vec 进行高效 KNN 向量检索

无阻塞风险，可以进入正式开发阶段。
