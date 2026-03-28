# Agent间通知消息

## 使用说明
- Agent通过此文件进行通信
- 追加写入，不覆盖已有内容
- 每条消息必须包含时间戳和签名
- 用户负责在各会话间转达通知

---

## 统计信息
- **待处理**: 0个
- **已确认**: 0个
- **已完成**: 0个

---

## 消息格式

```markdown
### MSG-YYYYMMDD-XXX

**发送时间**: YYYY-MM-DDTHH:MM:SSZ
**发送者**: agent_X (角色名称)
**接收者**: agent_Y | ALL
**类型**: INFO | REQUEST | ALERT
**优先级**: HIGH | MEDIUM | LOW

**消息内容**
具体消息内容

**需要的操作**
如需要接收者执行操作，在此说明

**状态**: PENDING | ACKNOWLEDGED | COMPLETED
```

---

## 通知消息列表

暂无通知消息
