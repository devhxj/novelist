import { useState } from 'react'
import { Card, Button, Checkbox, Alert, Spin, Empty, Tag, Collapse, Descriptions, message, Space, Statistic, Row, Col } from 'antd'
import { WarningOutlined, CloseCircleOutlined, InfoCircleOutlined } from '@ant-design/icons'
import { useParams } from 'react-router-dom'
import { consistencyApi } from '@/services/consistencyService'
import type { ConsistencyCheckResponse } from '@/types/consistency'

const { Panel } = Collapse

function ConsistencyCheck() {
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<ConsistencyCheckResponse | null>(null)
  const [checkTypes, setCheckTypes] = useState<string[]>(['character', 'plot', 'timeline', 'foreshadowing'])
  const { novelId } = useParams<{ novelId: string }>()

  const handleCheck = async () => {
    if (!novelId) return

    setLoading(true)
    try {
      const response = await consistencyApi.checkConsistency(parseInt(novelId), {
        check_types: checkTypes as any,
      })
      if (response.success) {
        setResult(response.data)
        message.success('一致性检查完成')
      }
    } catch (error: any) {
      message.error(error.error?.message || '检查失败')
    } finally {
      setLoading(false)
    }
  }

  const getSeverityIcon = (severity: string) => {
    const icons = {
      error: <CloseCircleOutlined style={{ color: '#ff4d4f' }} />,
      warning: <WarningOutlined style={{ color: '#faad14' }} />,
      info: <InfoCircleOutlined style={{ color: '#1890ff' }} />,
    }
    return icons[severity as keyof typeof icons] || <InfoCircleOutlined />
  }

  const getSeverityColor = (severity: string) => {
    const colors = {
      error: 'error',
      warning: 'warning',
      info: 'processing',
    }
    return colors[severity as keyof typeof colors] || 'default'
  }

  const getTypeText = (type: string) => {
    const texts = {
      character: '角色一致性',
      plot: '情节一致性',
      timeline: '时间线一致性',
      foreshadowing: '伏笔状态',
    }
    return texts[type as keyof typeof texts] || type
  }

  if (loading) {
    return (
      <Card>
        <div style={{ textAlign: 'center', padding: '50px' }}>
          <Spin size="large" />
          <p style={{ marginTop: 16 }}>正在进行一致性检查...</p>
        </div>
      </Card>
    )
  }

  return (
    <Card title="一致性检查">
      <Alert
        message="一致性检查功能"
        description="检查小说的角色、情节、时间线和伏笔的一致性，帮助发现潜在的逻辑问题和矛盾。"
        type="info"
        showIcon
        style={{ marginBottom: 16 }}
      />

      <div style={{ marginBottom: 16 }}>
        <div style={{ marginBottom: 8 }}>选择检查类型：</div>
        <Checkbox.Group
          value={checkTypes}
          onChange={(values) => setCheckTypes(values as string[])}
          options={[
            { label: '角色一致性', value: 'character' },
            { label: '情节一致性', value: 'plot' },
            { label: '时间线一致性', value: 'timeline' },
            { label: '伏笔状态', value: 'foreshadowing' },
          ]}
        />
      </div>

      <Button type="primary" onClick={handleCheck} disabled={checkTypes.length === 0} size="large">
        开始检查
      </Button>

      {result && (
        <div style={{ marginTop: 24 }}>
          <Card title="检查结果摘要" style={{ marginBottom: 16 }}>
            <Row gutter={16}>
              <Col span={6}>
                <Statistic title="检查耗时" value={result.check_time} suffix="秒" />
              </Col>
              <Col span={6}>
                <Statistic
                  title="问题总数"
                  value={result.summary.total_issues}
                  valueStyle={{ color: result.summary.total_issues > 0 ? '#faad14' : '#52c41a' }}
                />
              </Col>
              <Col span={6}>
                <Statistic title="错误" value={result.summary.by_severity.error} valueStyle={{ color: '#ff4d4f' }} />
              </Col>
              <Col span={6}>
                <Statistic title="警告" value={result.summary.by_severity.warning} valueStyle={{ color: '#faad14' }} />
              </Col>
            </Row>

            <div style={{ marginTop: 16 }}>
              <div style={{ marginBottom: 8 }}>按类型统计：</div>
              <Space wrap>
                <Tag color="blue">角色: {result.summary.by_type.character}</Tag>
                <Tag color="green">情节: {result.summary.by_type.plot}</Tag>
                <Tag color="purple">时间线: {result.summary.by_type.timeline}</Tag>
                <Tag color="orange">伏笔: {result.summary.by_type.foreshadowing}</Tag>
              </Space>
            </div>
          </Card>

          {result.issues.length > 0 ? (
            <Card title="发现问题">
              <Collapse accordion>
                {result.issues.map((issue, index) => (
                  <Panel
                    header={
                      <Space>
                        {getSeverityIcon(issue.severity)}
                        <Tag color={getSeverityColor(issue.severity)}>{issue.severity.toUpperCase()}</Tag>
                        <Tag>{getTypeText(issue.issue_type)}</Tag>
                        <span>{issue.description}</span>
                      </Space>
                    }
                    key={index}
                  >
                    <Descriptions bordered column={1}>
                      {issue.chapter_number && (
                        <Descriptions.Item label="章节">第{issue.chapter_number}章</Descriptions.Item>
                      )}
                      <Descriptions.Item label="问题描述">{issue.description}</Descriptions.Item>
                      {issue.details && (
                        <Descriptions.Item label="详细信息">
                          <pre>{JSON.stringify(issue.details, null, 2)}</pre>
                        </Descriptions.Item>
                      )}
                      {issue.suggestion && (
                        <Descriptions.Item label="修改建议">
                          <Alert message={issue.suggestion} type="info" showIcon />
                        </Descriptions.Item>
                      )}
                    </Descriptions>
                  </Panel>
                ))}
              </Collapse>
            </Card>
          ) : (
            <Card>
              <Empty description="未发现一致性问题" image={Empty.PRESENTED_IMAGE_SIMPLE} />
            </Card>
          )}
        </div>
      )}
    </Card>
  )
}

export default ConsistencyCheck
