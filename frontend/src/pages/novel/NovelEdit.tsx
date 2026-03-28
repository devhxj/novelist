import { useState, useEffect } from 'react'
import { Form, Input, Select, Button, Card, message, Spin } from 'antd'
import { useNavigate, useParams } from 'react-router-dom'
import { novelApi } from '@/services/novelService'
import { useNovelStore } from '@/stores/novelStore'
import type { NovelDetail } from '@/types/novel'

const { TextArea } = Input
const { Option } = Select

function NovelEdit() {
  const [loading, setLoading] = useState(false)
  const [fetchLoading, setFetchLoading] = useState(true)
  const [novel, setNovel] = useState<NovelDetail | null>(null)
  const [form] = Form.useForm()
  const navigate = useNavigate()
  const { id } = useParams<{ id: string }>()
  const { updateNovel } = useNovelStore()

  useEffect(() => {
    if (id) {
      loadNovel(parseInt(id))
    }
  }, [id])

  const loadNovel = async (novelId: number) => {
    setFetchLoading(true)
    try {
      const response = await novelApi.getNovel(novelId)
      if (response.success) {
        setNovel(response.data)
        form.setFieldsValue({
          title: response.data.title,
          genre: response.data.genre,
          description: response.data.description,
          status: response.data.status,
        })
      }
    } catch (error: any) {
      message.error(error.error?.message || '加载小说详情失败')
      navigate('/novels')
    } finally {
      setFetchLoading(false)
    }
  }

  const onFinish = async (values: any) => {
    if (!id) return
    
    setLoading(true)
    try {
      const response = await novelApi.updateNovel(parseInt(id), values)
      if (response.success) {
        updateNovel(parseInt(id), response.data)
        message.success('小说更新成功')
        navigate(`/novels/${id}`)
      }
    } catch (error: any) {
      message.error(error.error?.message || '更新失败')
    } finally {
      setLoading(false)
    }
  }

  if (fetchLoading) {
    return (
      <Card>
        <div style={{ textAlign: 'center', padding: '50px' }}>
          <Spin size="large" />
        </div>
      </Card>
    )
  }

  if (!novel) {
    return null
  }

  return (
    <Card title={`编辑小说: ${novel.title}`}>
      <Form
        form={form}
        layout="vertical"
        onFinish={onFinish}
        style={{ maxWidth: 600 }}
      >
        <Form.Item
          label="标题"
          name="title"
          rules={[{ required: true, message: '请输入小说标题' }]}
        >
          <Input placeholder="请输入小说标题" />
        </Form.Item>

        <Form.Item
          label="类型"
          name="genre"
          rules={[{ required: true, message: '请选择小说类型' }]}
        >
          <Select placeholder="请选择小说类型">
            <Option value="玄幻">玄幻</Option>
            <Option value="武侠">武侠</Option>
            <Option value="都市">都市</Option>
            <Option value="科幻">科幻</Option>
            <Option value="言情">言情</Option>
            <Option value="历史">历史</Option>
          </Select>
        </Form.Item>

        <Form.Item
          label="简介"
          name="description"
          rules={[{ required: true, message: '请输入小说简介' }]}
        >
          <TextArea rows={4} placeholder="请输入小说简介" />
        </Form.Item>

        <Form.Item
          label="状态"
          name="status"
          rules={[{ required: true, message: '请选择小说状态' }]}
        >
          <Select placeholder="请选择小说状态">
            <Option value="draft">草稿</Option>
            <Option value="writing">写作中</Option>
            <Option value="completed">已完成</Option>
            <Option value="published">已发布</Option>
          </Select>
        </Form.Item>

        <Form.Item>
          <Button type="primary" htmlType="submit" loading={loading}>
            保存
          </Button>
          <Button style={{ marginLeft: 8 }} onClick={() => navigate(`/novels/${id}`)}>
            取消
          </Button>
        </Form.Item>
      </Form>
    </Card>
  )
}

export default NovelEdit
