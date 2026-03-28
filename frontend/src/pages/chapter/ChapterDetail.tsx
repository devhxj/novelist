import { useEffect, useState } from 'react'
import { Card, Descriptions, Tag, Button, Space, message } from 'antd'
import { useParams, useNavigate } from 'react-router-dom'
import { chapterApi } from '@/services/chapterService'
import type { ChapterDetail } from '@/types/chapter'
import dayjs from 'dayjs'

function ChapterDetailPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const [chapter, setChapter] = useState<ChapterDetail | null>(null)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    if (id) {
      loadChapter(parseInt(id))
    }
  }, [id])

  const loadChapter = async (chapterId: number) => {
    setLoading(true)
    try {
      const response = await chapterApi.getChapter(chapterId)
      if (response.success) {
        setChapter(response.data)
      }
    } catch (error: any) {
      message.error(error.error?.message || '加载章节详情失败')
    } finally {
      setLoading(false)
    }
  }

  const getStatusTag = (status: string) => {
    const statusMap: any = {
      draft: { color: 'default', text: '草稿' },
      completed: { color: 'success', text: '已完成' },
    }
    const config = statusMap[status]
    return <Tag color={config.color}>{config.text}</Tag>
  }

  if (!chapter) return null

  return (
    <Card
      title={`第${chapter.chapter_number}章 ${chapter.title}`}
      extra={
        <Space>
          <Button onClick={() => navigate(`/novels/${chapter.novel_id}/chapters`)}>
            返回列表
          </Button>
          <Button onClick={() => navigate(`/chapters/${id}/generate`)}>
            AI生成
          </Button>
          <Button type="primary" onClick={() => navigate(`/chapters/${id}/edit`)}>
            编辑
          </Button>
        </Space>
      }
      loading={loading}
    >
      <Descriptions bordered column={2}>
        <Descriptions.Item label="ID">{chapter.id}</Descriptions.Item>
        <Descriptions.Item label="所属小说">
          {chapter.novel?.title || '-'}
        </Descriptions.Item>
        <Descriptions.Item label="章节编号">{chapter.chapter_number}</Descriptions.Item>
        <Descriptions.Item label="状态">{getStatusTag(chapter.status)}</Descriptions.Item>
        <Descriptions.Item label="字数">{chapter.word_count.toLocaleString()}</Descriptions.Item>
        <Descriptions.Item label="创建时间">
          {dayjs(chapter.created_at).format('YYYY-MM-DD HH:mm:ss')}
        </Descriptions.Item>
        <Descriptions.Item label="更新时间">
          {dayjs(chapter.updated_at).format('YYYY-MM-DD HH:mm:ss')}
        </Descriptions.Item>
        <Descriptions.Item label="摘要" span={2}>
          {chapter.summary || '-'}
        </Descriptions.Item>
        <Descriptions.Item label="内容" span={2}>
          <div style={{ whiteSpace: 'pre-wrap', maxHeight: '400px', overflow: 'auto' }}>
            {chapter.content || '-'}
          </div>
        </Descriptions.Item>
      </Descriptions>
    </Card>
  )
}

export default ChapterDetailPage
