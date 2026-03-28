import { useState, useEffect } from 'react'
import { Card, Form, Input, Button, Select, message, Spin, Alert, Divider } from 'antd'
import { useParams, useNavigate } from 'react-router-dom'
import { chapterApi } from '@/services/chapterService'
import { generationApi } from '@/services/generationService'
import type { ChapterDetail } from '@/types/chapter'

const { TextArea } = Input
const { Option } = Select

function ChapterGenerate() {
  const [loading, setLoading] = useState(false)
  const [fetchLoading, setFetchLoading] = useState(true)
  const [chapter, setChapter] = useState<ChapterDetail | null>(null)
  const [generatedContent, setGeneratedContent] = useState('')
  const [form] = Form.useForm()
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()

  useEffect(() => {
    if (id) {
      loadChapter(parseInt(id))
    }
  }, [id])

  const loadChapter = async (chapterId: number) => {
    setFetchLoading(true)
    try {
      const response = await chapterApi.getChapter(chapterId)
      if (response.success) {
        setChapter(response.data)
      }
    } catch (error: any) {
      message.error(error.error?.message || '加载章节详情失败')
      navigate('/novels')
    } finally {
      setFetchLoading(false)
    }
  }

  const onGenerate = async (values: any) => {
    if (!id || !chapter) return
    
    setLoading(true)
    try {
      const response = await generationApi.generateChapter(chapter.novel_id, parseInt(id), {
        prompt: values.prompt,
        context: values.context,
        options: values.options,
      })
      
      if (response.success) {
        setGeneratedContent(response.data.content || '生成成功')
        message.success('章节内容生成成功')
      }
    } catch (error: any) {
      message.error(error.error?.message || '生成失败')
    } finally {
      setLoading(false)
    }
  }

  const onSaveContent = async () => {
    if (!id || !generatedContent) return
    
    setLoading(true)
    try {
      const response = await chapterApi.updateChapter(parseInt(id), {
        content: generatedContent,
      })
      if (response.success) {
        message.success('内容保存成功')
        navigate(`/chapters/${id}`)
      }
    } catch (error: any) {
      message.error(error.error?.message || '保存失败')
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

  if (!chapter) {
    return null
  }

  return (
    <Card title={`AI生成章节 - 第${chapter.chapter_number}章 ${chapter.title}`}>
      <Alert
        message="AI生成功能"
        description="使用DeepSeek AI自动生成章节内容。请提供详细的提示词和上下文信息，以获得更好的生成效果。"
        type="info"
        showIcon
        style={{ marginBottom: 16 }}
      />

      <Form
        form={form}
        layout="vertical"
        onFinish={onGenerate}
        initialValues={{
          context: {
            style: 'narrative',
          },
          options: {
            temperature: 0.8,
            max_tokens: 3000,
          },
        }}
      >
        <Form.Item
          label="生成提示词"
          name="prompt"
          rules={[{ required: true, message: '请输入生成提示词' }]}
        >
          <TextArea
            rows={4}
            placeholder="请描述你希望生成的章节内容，例如：主角在森林中遇到了一只神秘的灵兽，展开了一场激烈的战斗..."
          />
        </Form.Item>

        <Form.Item label="写作风格" name={['context', 'style']}>
          <Select placeholder="请选择写作风格">
            <Option value="narrative">叙事风格</Option>
            <Option value="descriptive">描写风格</Option>
            <Option value="dialogue">对话风格</Option>
            <Option value="action">动作风格</Option>
          </Select>
        </Form.Item>

        <Form.Item label="温度参数" name={['options', 'temperature']}>
          <Select placeholder="请选择温度参数">
            <Option value={0.5}>0.5 - 保守</Option>
            <Option value={0.8}>0.8 - 平衡</Option>
            <Option value={1.0}>1.0 - 创意</Option>
          </Select>
        </Form.Item>

        <Form.Item label="最大字数" name={['options', 'max_tokens']}>
          <Select placeholder="请选择最大字数">
            <Option value={2000}>2000字</Option>
            <Option value={3000}>3000字</Option>
            <Option value={5000}>5000字</Option>
          </Select>
        </Form.Item>

        <Form.Item>
          <Button type="primary" htmlType="submit" loading={loading} size="large">
            开始生成
          </Button>
          <Button style={{ marginLeft: 8 }} onClick={() => navigate(`/chapters/${id}`)}>
            取消
          </Button>
        </Form.Item>
      </Form>

      {generatedContent && (
        <>
          <Divider>生成结果</Divider>
          <Card>
            <div style={{ whiteSpace: 'pre-wrap', maxHeight: '500px', overflow: 'auto' }}>
              {generatedContent}
            </div>
            <Divider />
            <Button type="primary" onClick={onSaveContent} loading={loading}>
              保存内容
            </Button>
          </Card>
        </>
      )}
    </Card>
  )
}

export default ChapterGenerate
