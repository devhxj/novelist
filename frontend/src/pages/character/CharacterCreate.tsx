import { useState } from 'react'
import { Form, Input, Button, Card, message, Space } from 'antd'
import { PlusOutlined, MinusCircleOutlined } from '@ant-design/icons'
import { useNavigate, useParams } from 'react-router-dom'
import { characterApi } from '@/services/characterService'

const { TextArea } = Input

function CharacterCreate() {
  const [loading, setLoading] = useState(false)
  const navigate = useNavigate()
  const { novelId } = useParams<{ novelId: string }>()

  const onFinish = async (values: any) => {
    if (!novelId) return
    
    setLoading(true)
    try {
      const response = await characterApi.createCharacter(parseInt(novelId), values)
      if (response.success) {
        message.success('角色创建成功')
        navigate(`/novels/${novelId}/characters`)
      }
    } catch (error: any) {
      message.error(error.error?.message || '创建失败')
    } finally {
      setLoading(false)
    }
  }

  return (
    <Card title="创建角色">
      <Form
        layout="vertical"
        onFinish={onFinish}
        style={{ maxWidth: 600 }}
      >
        <Form.Item
          label="角色名"
          name="name"
          rules={[{ required: true, message: '请输入角色名' }]}
        >
          <Input placeholder="请输入角色名" />
        </Form.Item>

        <Form.Item label="性格特征">
          <Form.List name={['personality', 'traits']}>
            {(fields, { add, remove }) => (
              <>
                {fields.map(({ key, name, ...restField }) => (
                  <Space key={key} style={{ display: 'flex', marginBottom: 8 }} align="baseline">
                    <Form.Item
                      {...restField}
                      name={name}
                      rules={[{ required: true, message: '请输入性格特征' }]}
                    >
                      <Input placeholder="性格特征" style={{ width: 300 }} />
                    </Form.Item>
                    <MinusCircleOutlined onClick={() => remove(name)} />
                  </Space>
                ))}
                <Form.Item>
                  <Button type="dashed" onClick={() => add()} block icon={<PlusOutlined />}>
                    添加性格特征
                  </Button>
                </Form.Item>
              </>
            )}
          </Form.List>
        </Form.Item>

        <Form.Item
          label="背景故事"
          name={['personality', 'background']}
        >
          <TextArea rows={4} placeholder="请输入角色背景故事" />
        </Form.Item>

        <Form.Item label="能力">
          <Form.List name="abilities">
            {(fields, { add, remove }) => (
              <>
                {fields.map(({ key, name, ...restField }) => (
                  <Space key={key} style={{ display: 'flex', marginBottom: 8 }} align="baseline">
                    <Form.Item
                      {...restField}
                      name={name}
                    >
                      <Input placeholder="能力" style={{ width: 300 }} />
                    </Form.Item>
                    <MinusCircleOutlined onClick={() => remove(name)} />
                  </Space>
                ))}
                <Form.Item>
                  <Button type="dashed" onClick={() => add()} block icon={<PlusOutlined />}>
                    添加能力
                  </Button>
                </Form.Item>
              </>
            )}
          </Form.List>
        </Form.Item>

        <Form.Item>
          <Button type="primary" htmlType="submit" loading={loading}>
            创建
          </Button>
          <Button style={{ marginLeft: 8 }} onClick={() => navigate(`/novels/${novelId}/characters`)}>
            取消
          </Button>
        </Form.Item>
      </Form>
    </Card>
  )
}

export default CharacterCreate
