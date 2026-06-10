# G6 v5 ForceLayout 踩坑记录

## nodeStrength 符号与文档相反

**结论**：`nodeStrength` 正数 = 斥力（推远），负数 = 引力（拉近）。官方文档写反了。

### 源码证据

`@antv/layout` 的 `forceRepulsive` 中：

```js
// repulsive.js
const weightParam = factor / coulombDisScale2; // = 1 / 0.005² = 40000
node.weight = weightParam * nodeStrength;       // 40000 * nodeStrength
```

Barnes-Hut 力计算：

```js
const param = treeNode.weight / len3;  // weight / distance³
node.vx += dx * param;                 // dx * weight / d³
```

当 `nodeStrength < 0` 时，`weight` 为负，`dx * (-|weight|) / d³` 将节点拉向对方——斥力变成引力。

### 表现

- `nodeStrength: -200` → 所有节点互相吸引，加上 gravity 往中心拉，最终坍缩成一团
- 初始 `initNodePosition` 会把节点散开，所以刚开始看到散开的，然后逐渐聚拢
- `nodeStrength: 800` → 节点互相排斥，配合 `edgeStrength` 拉拢相连节点，形成正常力导向布局

### 推荐配置

```ts
layout: {
  type: 'force',
  nodeStrength: 800,   // 正数 = 排斥，保持节点间距
  edgeStrength: 200,   // 边拉力，相连节点聚拢
  linkDistance: 200,   // 边理想长度
  gravity: 5,          // 向心力，数字越大布局越紧凑
}
```

## dimmed 状态不要低于 0.3

`opacity: 0.17` 在白底上几乎不可见。保持在 0.3-0.4 能看清结构又有层次感。

## personality 字段是 JSON 字符串

Go 端 `personality` 字段存的是 JSON 自由格式（LLM 写入），前端需要 `JSON.parse()` 后再展示，不能直接当文本渲染。

## CanvasEvent.CLICK 会冒泡

`CanvasEvent.CLICK` 在点击节点时也会触发（事件冒泡），且 `event.target.id` 在点击 label/icon 等子元素时不等于节点 ID，靠 ID 前缀过滤不可靠。用 flag 变量标记 `NodeEvent.CLICK` 更稳定：

```ts
let nodeClicked = false
graph.on(NodeEvent.CLICK, () => { nodeClicked = true; /* select */ })
graph.on(CanvasEvent.CLICK, () => {
  if (nodeClicked) { nodeClicked = false; return }
  /* deselect */
})
```

## G6 v5 属性名陷阱

| 属性 | 正确名称 | 易错名 |
|------|---------|--------|
| 边线宽 | `lineWidth` | `strokeWidth` |
| 虚线 | `lineDash: [6, 4]` | `strokeDasharray` |
| 边箭头类型 | `endArrowType` | 无 |
| label padding | 数字如 `4` | 数组 `[2, 4]` 会崩溃 |
| font weight | 数字 `500` | 字符串 `'500'` |
| 布局迭代 | `iterations` | `maxIteration`（内部用） |
| 布局动画 | `animation` | `animate`（内部用） |
