# PNG 转 SVG 方法

## 环境

Python venv：`python-master/backend/.venv/`

依赖：

```bash
.venv/bin/python -m pip install vtracer Pillow
```

## 流程

### 一、原生背景版（位图底色原样保留）

直接描摹，不做预处理：

```bash
.venv/bin/python -c "
from vtracer import convert_image_to_svg_py
convert_image_to_svg_py(
    'input.png',
    'output.svg',
    colormode='color',
    mode='spline',
    filter_speckle=4,
    corner_threshold=60,
    hierarchical='stacked',
)"
```

参数说明：
- `colormode='color'` — 保留原色
- `mode='spline'` — 贝塞尔曲线拟合，线条更平滑
- `filter_speckle=4` — 过滤 4px 以下的噪点
- `corner_threshold=60` — 拐角敏感度，越小越锐利
- `hierarchical='stacked'` — 色块叠加，比 cutout 稳定

### 二、透明背景版（切除底色）

先得到原生版本，再精确切除背景矩形：

```bash
cp output.svg output-tp.svg

python -c "
with open('output-tp.svg') as f:
    lines = f.readlines()

kept = []
for l in lines:
    # 背景矩形特征：全画幅路径 + 底色 fill + translate(0,0)
    if 'fill=\"#FDFDFD\"' in l and 'M0 0 C' in l and '1254 1254' in l and 'translate(0,0)' in l:
        continue
    kept.append(l)

with open('output-tp.svg', 'w') as f:
    f.writelines(kept)
"
```

vtracer 描摹后背景总是一个全画幅矩形 path，fill 色号为原图的四角平均色。这套特征稳定可定位。**只删一个背景矩形，绝不按色值范围批量删除**——按色值批量删会把眼白、高光等接近底色的细节 path 也误删。

### 其他尝试过的方法

| 方法 | 效果 | 问题 |
|------|------|------|
| PNG 预透明化后再描摹（PIL 把底色改 alpha=0） | 可用但路径数暴增 3 倍 | 透明边界会被当作新形状描摹，产生大量边缘碎片 |
| vtracer binary 模式 + potrace 风格 | 不适合多色扁平图 | 只出黑白线条，丢失色彩 |
| SVG 全量色值替换（fill 映射表） | 浅色/深色反转时可用 | 简单替换不考虑渐变邻接关系，破环色彩一致性 |
| vectorizer.ai | 效果极好 | 付费，仅前几次免费 |
