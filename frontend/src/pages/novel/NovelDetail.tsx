import { useEffect, useState } from 'react'
import { Card, Descriptions, Tag, Button, Space, message } from 'antd'
import { useParams, useNavigate } from 'react-router-dom'
import { novelApi } from '@/services/novelService'
import type { NovelDetail } from '@/types/novel'
import dayjs from 'dayjs'

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
    } catch (error: any) {
      message.error(error.error?.message || '加载小说详情失败')
    } finally {
      setLoading(false)
    }
  }

  const getStatusTag = (status: string) => {
    const statusMap: any = {
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
          <Button onClick={() => navigate(`/novels/${id}/characters`)}>
            角色管理
          </Button>
          <Button onClick={() => navigate(`/novels/${id}/chapters`)}>
            章节管理
          </Button>
          <Button type="primary" onClick={() => navigate(`/novels/${id}/edit`)}>
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
        <Descriptions.Item label="章节数">{novel.chapter_count}</Descriptions.Item>
        <Descriptions.Item label="字数">{novel.word_count.toLocaleString()}</Descriptions.Item>
        <Descriptions.Item label="角色数">{novel.character_count}</Descriptions.Item>
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
    </Card>
  )
}

export default NovelDetailPage
