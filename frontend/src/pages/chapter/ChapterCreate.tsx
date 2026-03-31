import { useState, useEffect } from 'react'
import { Form, Input, Button, Card, message, InputNumber } from 'antd'
import { useNavigate, useParams } from 'react-router-dom'
import { chapterApi } from '@/services/chapterService'
import { getErrorMessage } from '@/types/error'
import type { ChapterCreate } from '@/types/chapter'

const { TextArea } = Input

function ChapterCreatePage() {
  const [loading, setLoading] = useState(false)
  const navigate = useNavigate()
  const { novelId } = useParams<{ novelId: string }>()
  const [form] = Form.useForm<ChapterCreate>()

  useEffect(() => {
    if (!novelId) return
    const nid = parseInt(novelId)
    chapterApi.getNextChapterNumber(nid).then(res => {
      if (res.success) {
        form.setFieldsValue({ chapter_number: res.data.next_chapter_number })
      }
    }).catch(() => {})
  }, [novelId, form])

  const onFinish = async (values: ChapterCreate) => {
    if (!novelId) return

    setLoading(true)
    try {
      const response = await chapterApi.createChapter(parseInt(novelId), values)
      if (response.success) {
        message.success('章节创建成功')
        navigate(`/novels/${novelId}/chapters`)
      }
    } catch (error) {
      message.error(getErrorMessage(error))
    } finally {
      setLoading(false)
    }
  }

  return (
    <Card title="创建章节">
      <Form
        form={form}
        layout="vertical"
        onFinish={onFinish}
        style={{ maxWidth: 800 }}
      >
        <Form.Item
          label="章节编号"
          name="chapter_number"
          rules={[{ required: true, message: '请输入章节编号' }]}
        >
          <InputNumber min={1} style={{ width: '100%' }} placeholder="自动获取下一个章节号" />
        </Form.Item>

        <Form.Item
          label="标题"
          name="title"
          rules={[{ required: true, message: '请输入章节标题' }]}
        >
          <Input placeholder="请输入章节标题" />
        </Form.Item>

        <Form.Item
          label="摘要"
          name="summary"
        >
          <TextArea rows={3} placeholder="请输入章节摘要" />
        </Form.Item>

        <Form.Item
          label="内容"
          name="content"
        >
          <TextArea rows={15} placeholder="请输入章节内容" />
        </Form.Item>

        <Form.Item>
          <Button type="primary" htmlType="submit" loading={loading}>
            创建
          </Button>
          <Button style={{ marginLeft: 8 }} onClick={() => navigate(`/novels/${novelId}/chapters`)}>
            取消
          </Button>
        </Form.Item>
      </Form>
    </Card>
  )
}

export default ChapterCreatePage
