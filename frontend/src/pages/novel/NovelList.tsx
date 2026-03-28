import { useState, useEffect } from 'react'
import { Table, Button, Space, Tag, Input, Select, message, Popconfirm } from 'antd'
import { PlusOutlined, EditOutlined, DeleteOutlined, EyeOutlined } from '@ant-design/icons'
import { useNavigate } from 'react-router-dom'
import { novelApi } from '@/services/novelService'
import { useNovelStore } from '@/stores/novelStore'
import type { Novel, NovelStatus } from '@/types/novel'
import dayjs from 'dayjs'

const { Search } = Input
const { Option } = Select

function NovelList() {
  const navigate = useNavigate()
  const { novels, setNovels, loading, setLoading, removeNovel } = useNovelStore()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [total, setTotal] = useState(0)
  const [search, setSearch] = useState('')
  const [statusFilter, setStatusFilter] = useState<NovelStatus | undefined>()

  useEffect(() => {
    loadNovels()
  }, [page, pageSize, statusFilter])

  const loadNovels = async () => {
    setLoading(true)
    try {
      const response = await novelApi.getNovels({
        page,
        page_size: pageSize,
        status: statusFilter,
        search: search || undefined,
      })
      if (response.success) {
        setNovels(response.data.items)
        setTotal(response.data.total)
      }
    } catch (error: any) {
      message.error(error.error?.message || '加载小说列表失败')
    } finally {
      setLoading(false)
    }
  }

  const handleDelete = async (id: number) => {
    try {
      const response = await novelApi.deleteNovel(id)
      if (response.success) {
        message.success('删除成功')
        removeNovel(id)
        setTotal(total - 1)
      }
    } catch (error: any) {
      message.error(error.error?.message || '删除失败')
    }
  }

  const getStatusTag = (status: NovelStatus) => {
    const statusMap = {
      draft: { color: 'default', text: '草稿' },
      writing: { color: 'processing', text: '写作中' },
      completed: { color: 'success', text: '已完成' },
      published: { color: 'blue', text: '已发布' },
    }
    const config = statusMap[status]
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
      render: (text: string, record: Novel) => (
        <a onClick={() => navigate(`/novels/${record.id}`)}>{text}</a>
      ),
    },
    {
      title: '类型',
      dataIndex: 'genre',
      key: 'genre',
      width: 120,
    },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 100,
      render: (status: NovelStatus) => getStatusTag(status),
    },
    {
      title: '章节数',
      dataIndex: 'chapter_count',
      key: 'chapter_count',
      width: 100,
    },
    {
      title: '字数',
      dataIndex: 'word_count',
      key: 'word_count',
      width: 120,
      render: (count: number) => count.toLocaleString(),
    },
    {
      title: '创建时间',
      dataIndex: 'created_at',
      key: 'created_at',
      width: 180,
      render: (date: string) => dayjs(date).format('YYYY-MM-DD HH:mm'),
    },
    {
      title: '操作',
      key: 'action',
      width: 200,
      render: (_: any, record: Novel) => (
        <Space>
          <Button
            type="link"
            icon={<EyeOutlined />}
            onClick={() => navigate(`/novels/${record.id}`)}
          >
            查看
          </Button>
          <Button
            type="link"
            icon={<EditOutlined />}
            onClick={() => navigate(`/novels/${record.id}/edit`)}
          >
            编辑
          </Button>
          <Popconfirm
            title="确定删除这本小说吗？"
            onConfirm={() => handleDelete(record.id)}
            okText="确定"
            cancelText="取消"
          >
            <Button type="link" danger icon={<DeleteOutlined />}>
              删除
            </Button>
          </Popconfirm>
        </Space>
      ),
    },
  ]

  return (
    <div>
      <div style={{ marginBottom: 16, display: 'flex', justifyContent: 'space-between' }}>
        <Space>
          <Search
            placeholder="搜索小说标题"
            onSearch={(value) => {
              setSearch(value)
              setPage(1)
              loadNovels()
            }}
            style={{ width: 300 }}
          />
          <Select
            placeholder="状态筛选"
            style={{ width: 150 }}
            allowClear
            onChange={(value) => {
              setStatusFilter(value)
              setPage(1)
            }}
          >
            <Option value="draft">草稿</Option>
            <Option value="writing">写作中</Option>
            <Option value="completed">已完成</Option>
            <Option value="published">已发布</Option>
          </Select>
        </Space>
        <Button
          type="primary"
          icon={<PlusOutlined />}
          onClick={() => navigate('/novels/create')}
        >
          创建小说
        </Button>
      </div>

      <Table
        columns={columns}
        dataSource={novels}
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
    </div>
  )
}

export default NovelList
