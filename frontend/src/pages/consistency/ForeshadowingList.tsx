import { useState, useEffect } from 'react'
import { Table, Button, Space, Tag, Select, message, Popconfirm, Modal, Form, InputNumber, Input } from 'antd'
import { PlusOutlined, EyeOutlined, CheckOutlined, CloseOutlined } from '@ant-design/icons'
import { useNavigate, useParams } from 'react-router-dom'
import { consistencyApi } from '@/services/consistencyService'
import type { Foreshadowing, ForeshadowingStatus, ForeshadowingType } from '@/types/consistency'
import dayjs from 'dayjs'

const { Option } = Select
const { TextArea } = Input

function ForeshadowingList() {
  const navigate = useNavigate()
  const { novelId } = useParams<{ novelId: string }>()
  const [foreshadowings, setForeshadowings] = useState<Foreshadowing[]>([])
  const [loading, setLoading] = useState(false)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [total, setTotal] = useState(0)
  const [statusFilter, setStatusFilter] = useState<ForeshadowingStatus | undefined>()
  const [typeFilter, setTypeFilter] = useState<ForeshadowingType | undefined>()
  const [createModalVisible, setCreateModalVisible] = useState(false)
  const [resolveModalVisible, setResolveModalVisible] = useState(false)
  const [selectedForeshadowing, setSelectedForeshadowing] = useState<Foreshadowing | null>(null)
  const [form] = Form.useForm()
  const [resolveForm] = Form.useForm()

  useEffect(() => {
    if (novelId) {
      loadForeshadowings()
    }
  }, [page, pageSize, novelId, statusFilter, typeFilter])

  const loadForeshadowings = async () => {
    if (!novelId) return

    setLoading(true)
    try {
      const response = await consistencyApi.getForeshadowings(parseInt(novelId), {
        page,
        page_size: pageSize,
        status: statusFilter,
        foreshadowing_type: typeFilter,
      })
      if (response.success) {
        setForeshadowings(response.data.items)
        setTotal(response.data.total)
      }
    } catch (error: any) {
      message.error(error.error?.message || '加载伏笔列表失败')
    } finally {
      setLoading(false)
    }
  }

  const handleCreate = async (values: any) => {
    if (!novelId) return

    try {
      const response = await consistencyApi.createForeshadowing(parseInt(novelId), values)
      if (response.success) {
        message.success('伏笔创建成功')
        setCreateModalVisible(false)
        form.resetFields()
        loadForeshadowings()
      }
    } catch (error: any) {
      message.error(error.error?.message || '创建失败')
    }
  }

  const handleResolve = async (values: any) => {
    if (!novelId || !selectedForeshadowing) return

    try {
      const response = await consistencyApi.resolveForeshadowing(parseInt(novelId), selectedForeshadowing.id, values)
      if (response.success) {
        message.success('伏笔已解决')
        setResolveModalVisible(false)
        resolveForm.resetFields()
        setSelectedForeshadowing(null)
        loadForeshadowings()
      }
    } catch (error: any) {
      message.error(error.error?.message || '操作失败')
    }
  }

  const handleAbandon = async (id: number, reason?: string) => {
    if (!novelId) return

    try {
      const response = await consistencyApi.abandonForeshadowing(parseInt(novelId), id, reason)
      if (response.success) {
        message.success('伏笔已放弃')
        loadForeshadowings()
      }
    } catch (error: any) {
      message.error(error.error?.message || '操作失败')
    }
  }

  const getStatusTag = (status: ForeshadowingStatus) => {
    const statusMap = {
      unresolved: { color: 'warning', text: '未解决' },
      resolved: { color: 'success', text: '已解决' },
      abandoned: { color: 'default', text: '已放弃' },
    }
    const config = statusMap[status]
    return <Tag color={config.color}>{config.text}</Tag>
  }

  const getTypeTag = (type: ForeshadowingType) => {
    const typeMap = {
      plot: { color: 'blue', text: '情节' },
      character: { color: 'green', text: '角色' },
      item: { color: 'purple', text: '物品' },
      mystery: { color: 'orange', text: '悬疑' },
      other: { color: 'default', text: '其他' },
    }
    const config = typeMap[type]
    return <Tag color={config.color}>{config.text}</Tag>
  }

  const columns = [
    {
      title: 'ID',
      dataIndex: 'id',
      key: 'id',
      width: 80,
    },
    {
      title: '标题',
      dataIndex: 'title',
      key: 'title',
    },
    {
      title: '类型',
      dataIndex: 'foreshadowing_type',
      key: 'foreshadowing_type',
      width: 100,
      render: (type: ForeshadowingType) => getTypeTag(type),
    },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 100,
      render: (status: ForeshadowingStatus) => getStatusTag(status),
    },
    {
      title: '重要程度',
      dataIndex: 'importance',
      key: 'importance',
      width: 120,
      render: (importance: number) => (
        <Space>
          {[1, 2, 3, 4, 5].map((star) => (
            <span key={star} style={{ color: star <= importance ? '#faad14' : '#d9d9d9' }}>
              ★
            </span>
          ))}
        </Space>
      ),
    },
    {
      title: '创建时间',
      dataIndex: 'created_at',
      key: 'created_at',
      width: 180,
      render: (date: string) => dayjs(date).format('YYYY-MM-DD HH:mm'),
    },
    {
      title: '解决时间',
      dataIndex: 'resolved_at',
      key: 'resolved_at',
      width: 180,
      render: (date: string | null) => (date ? dayjs(date).format('YYYY-MM-DD HH:mm') : '-'),
    },
    {
      title: '操作',
      key: 'action',
      width: 200,
      render: (_: any, record: Foreshadowing) => (
        <Space>
          <Button type="link" icon={<EyeOutlined />} onClick={() => navigate(`/novels/${novelId}/foreshadowings/${record.id}`)}>
            查看
          </Button>
          {record.status === 'unresolved' && (
            <>
              <Button
                type="link"
                icon={<CheckOutlined />}
                onClick={() => {
                  setSelectedForeshadowing(record)
                  setResolveModalVisible(true)
                }}
              >
                解决
              </Button>
              <Popconfirm title="确定放弃这个伏笔吗？" onConfirm={() => handleAbandon(record.id)} okText="确定" cancelText="取消">
                <Button type="link" danger icon={<CloseOutlined />}>
                  放弃
                </Button>
              </Popconfirm>
            </>
          )}
        </Space>
      ),
    },
  ]

  return (
    <div>
      <div style={{ marginBottom: 16, display: 'flex', justifyContent: 'space-between' }}>
        <Space>
          <Select
            placeholder="状态筛选"
            style={{ width: 150 }}
            allowClear
            onChange={(value) => {
              setStatusFilter(value)
              setPage(1)
            }}
          >
            <Option value="unresolved">未解决</Option>
            <Option value="resolved">已解决</Option>
            <Option value="abandoned">已放弃</Option>
          </Select>
          <Select
            placeholder="类型筛选"
            style={{ width: 150 }}
            allowClear
            onChange={(value) => {
              setTypeFilter(value)
              setPage(1)
            }}
          >
            <Option value="plot">情节</Option>
            <Option value="character">角色</Option>
            <Option value="item">物品</Option>
            <Option value="mystery">悬疑</Option>
            <Option value="other">其他</Option>
          </Select>
        </Space>
        <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateModalVisible(true)}>
          创建伏笔
        </Button>
      </div>

      <Table
        columns={columns}
        dataSource={foreshadowings}
        rowKey="id"
        loading={loading}
        pagination={{
          current: page,
          pageSize,
          total,
          showSizeChanger: true,
          showTotal: (total) => `共 ${total} 条`,
          onChange: (page, pageSize) => {
            setPage(page)
            setPageSize(pageSize)
          },
        }}
      />

      <Modal
        title="创建伏笔"
        open={createModalVisible}
        onCancel={() => {
          setCreateModalVisible(false)
          form.resetFields()
        }}
        footer={null}
      >
        <Form form={form} layout="vertical" onFinish={handleCreate}>
          <Form.Item label="标题" name="title" rules={[{ required: true, message: '请输入伏笔标题' }]}>
            <Input placeholder="请输入伏笔标题" />
          </Form.Item>
          <Form.Item label="描述" name="description">
            <TextArea rows={4} placeholder="请输入伏笔描述" />
          </Form.Item>
          <Form.Item label="类型" name="foreshadowing_type" initialValue="other">
            <Select>
              <Option value="plot">情节</Option>
              <Option value="character">角色</Option>
              <Option value="item">物品</Option>
              <Option value="mystery">悬疑</Option>
              <Option value="other">其他</Option>
            </Select>
          </Form.Item>
          <Form.Item label="重要程度" name="importance" initialValue={3}>
            <InputNumber min={1} max={5} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit">
              创建
            </Button>
            <Button style={{ marginLeft: 8 }} onClick={() => setCreateModalVisible(false)}>
              取消
            </Button>
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title="解决伏笔"
        open={resolveModalVisible}
        onCancel={() => {
          setResolveModalVisible(false)
          resolveForm.resetFields()
          setSelectedForeshadowing(null)
        }}
        footer={null}
      >
        <Form form={resolveForm} layout="vertical" onFinish={handleResolve}>
          <Form.Item label="解决章节ID" name="resolved_chapter_id" rules={[{ required: true, message: '请输入解决章节ID' }]}>
            <InputNumber min={1} style={{ width: '100%' }} placeholder="请输入解决章节ID" />
          </Form.Item>
          <Form.Item label="解决说明" name="resolution_notes">
            <TextArea rows={4} placeholder="请输入解决说明" />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit">
              确认解决
            </Button>
            <Button style={{ marginLeft: 8 }} onClick={() => setResolveModalVisible(false)}>
              取消
            </Button>
          </Form.Item>
        </Form>
      </Modal>
    </div>
  )
}

export default ForeshadowingList
