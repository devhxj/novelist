import { useEffect, useState } from 'react'
import { Card, Descriptions, Tag, Button, Space, message, Divider, Row, Col } from 'antd'
import { FileTextOutlined, UserOutlined, CheckCircleOutlined, BarChartOutlined, EditOutlined, RocketOutlined, ClockCircleOutlined } from '@ant-design/icons'
import { useParams, useNavigate } from 'react-router-dom'
import { novelApi } from '@/services/novelService'
import { getErrorMessage } from '@/types/error'
import type { NovelDetail } from '@/types/novel'
import dayjs from 'dayjs'

interface StatusConfig {
  color: string
  text: string
}

function NovelDetailPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const [novel, setNovel] = useState<NovelDetail | null>(null)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    if (id) {
      loadNovel(parseInt(id))
    }
  }, [id])

  const loadNovel = async (novelId: number) => {
    setLoading(true)
    try {
      const response = await novelApi.getNovel(novelId)
      if (response.success) {
        setNovel(response.data)
      }
    } catch (error) {
      message.error(getErrorMessage(error))
    } finally {
      setLoading(false)
    }
  }

  const getStatusTag = (status: string) => {
    const statusMap: Record<string, StatusConfig> = {
      draft: { color: 'default', text: '草稿' },
      writing: { color: 'processing', text: '写作中' },
      completed: { color: 'success', text: '已完成' },
      published: { color: 'blue', text: '已发布' },
    }
    const config = statusMap[status]
    return <Tag color={config.color}>{config.text}</Tag>
  }

  if (!novel) return null

  return (
    <Card
      title={novel.title}
      extra={
        <Space>
          <Button type="primary" icon={<EditOutlined />} onClick={() => navigate(`/novels/${id}/edit`)}>
            编辑
          </Button>
        </Space>
      }
      loading={loading}
    >
      <Descriptions bordered column={2}>
        <Descriptions.Item label="ID">{novel.id}</Descriptions.Item>
        <Descriptions.Item label="类型">{novel.genre}</Descriptions.Item>
        <Descriptions.Item label="状态">{getStatusTag(novel.status)}</Descriptions.Item>
        <Descriptions.Item label="章节数">{novel.chapter_count ?? 0}</Descriptions.Item>
        <Descriptions.Item label="字数">{(novel.word_count ?? 0).toLocaleString()}</Descriptions.Item>
        <Descriptions.Item label="角色数">{novel.character_count ?? 0}</Descriptions.Item>
        <Descriptions.Item label="创建时间">
          {dayjs(novel.created_at).format('YYYY-MM-DD HH:mm:ss')}
        </Descriptions.Item>
        <Descriptions.Item label="更新时间">
          {dayjs(novel.updated_at).format('YYYY-MM-DD HH:mm:ss')}
        </Descriptions.Item>
        <Descriptions.Item label="简介" span={2}>
          {novel.description}
        </Descriptions.Item>
      </Descriptions>

      <Divider>功能入口</Divider>

      <Row gutter={[16, 16]}>
        <Col span={6}>
          <Card hoverable onClick={() => navigate(`/novels/${id}/editor`)} style={{ borderColor: '#007acc' }}>
            <Card.Meta
              avatar={<RocketOutlined style={{ fontSize: 24, color: '#007acc' }} />}
              title="开始创作"
              description="AI IDE 创作工作台"
            />
          </Card>
        </Col>
        <Col span={6}>
          <Card hoverable onClick={() => navigate(`/novels/${id}/chapters`)}>
            <Card.Meta
              avatar={<FileTextOutlined style={{ fontSize: 24, color: '#1890ff' }} />}
              title="章节管理"
              description="管理小说章节内容"
            />
          </Card>
        </Col>
        <Col span={6}>
          <Card hoverable onClick={() => navigate(`/novels/${id}/characters`)}>
            <Card.Meta
              avatar={<UserOutlined style={{ fontSize: 24, color: '#52c41a' }} />}
              title="角色管理"
              description="管理小说角色信息"
            />
          </Card>
        </Col>
        <Col span={6}>
          <Card hoverable onClick={() => navigate(`/novels/${id}/consistency`)}>
            <Card.Meta
              avatar={<CheckCircleOutlined style={{ fontSize: 24, color: '#13c2c2' }} />}
              title="一致性检查"
              description="检查内容一致性"
            />
          </Card>
        </Col>
        <Col span={6}>
          <Card hoverable onClick={() => navigate(`/novels/${id}/tracker`)}>
            <Card.Meta
              avatar={<ClockCircleOutlined style={{ fontSize: 24, color: '#fa8c16' }} />}
              title="故事追踪"
              description="伏笔/规划/指令统一时间线"
            />
          </Card>
        </Col>
        <Col span={6}>
          <Card hoverable onClick={() => navigate(`/novels/${id}/progress`)}>
            <Card.Meta
              avatar={<BarChartOutlined style={{ fontSize: 24, color: '#2f54eb' }} />}
              title="进度追踪"
              description="小说写作进度"
            />
          </Card>
        </Col>
      </Row>
    </Card>
  )
}

export default NovelDetailPage
