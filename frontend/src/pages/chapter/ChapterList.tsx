import { useState, useEffect } from 'react'
import { Table, Button, Space, Tag, Input, Select, message, Popconfirm } from 'antd'
import { PlusOutlined, EditOutlined, DeleteOutlined, EyeOutlined, RobotOutlined } from '@ant-design/icons'
import { useNavigate, useParams } from 'react-router-dom'
import { chapterApi } from '@/services/chapterService'
import type { Chapter, ChapterStatus } from '@/types/chapter'
import dayjs from 'dayjs'

const { Search } = Input
const { Option } = Select

function ChapterList() {
  const navigate = useNavigate()
  const { novelId } = useParams<{ novelId: string }>()
  const [chapters, setChapters] = useState<Chapter[]>([])
  const [loading, setLoading] = useState(false)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [total, setTotal] = useState(0)
  const [statusFilter, setStatusFilter] = useState<ChapterStatus | undefined>()
  const [search, setSearch] = useState('')

  useEffect(() => {
    if (novelId) {
      loadChapters()
    }
  }, [page, pageSize, novelId, statusFilter])

  const loadChapters = async () => {
    if (!novelId) return
    
    setLoading(true)
    try {
      const response = await chapterApi.getChapters(parseInt(novelId), {
        page,
        page_size: pageSize,
        status: statusFilter,
        search: search || undefined,
      })
      if (response.success) {
        setChapters(response.data.items)
        setTotal(response.data.total)
      }
    } catch (error: any) {
      message.error(error.error?.message || '加载章节列表失败')
    } finally {
      setLoading(false)
    }
  }

  const handleDelete = async (id: number) => {
    try {
      const response = await chapterApi.deleteChapter(id)
      if (response.success) {
        message.success('删除成功')
        setChapters(chapters.filter(c => c.id !== id))
        setTotal(total - 1)
      }
    } catch (error: any) {
      message.error(error.error?.message || '删除失败')
    }
  }

  const getStatusTag = (status: ChapterStatus) => {
    const statusMap = {
      draft: { color: 'default', text: '草稿' },
      completed: { color: 'success', text: '已完成' },
    }
    const config = statusMap[status]
    return <Tag color={config.color}>{config.text}</Tag>
  }

  const columns = [
    {
      title: '章节',
      dataIndex: 'chapter_number',
      key: 'chapter_number',
      width: 80,
      render: (num: number) => `第${num}章`,
    },
    {
      title: '标题',
      dataIndex: 'title',
      key: 'title',
      render: (text: string, record: Chapter) => (
        <a onClick={() => navigate(`/chapters/${record.id}`)}>{text}</a>
      ),
    },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 100,
      render: (status: ChapterStatus) => getStatusTag(status),
    },
    {
      title: '字数',
      dataIndex: 'word_count',
      key: 'word_count',
      width: 100,
      render: (count: number) => count.toLocaleString(),
    },
    {
      title: '摘要',
      dataIndex: 'summary',
      key: 'summary',
      ellipsis: true,
      width: 200,
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
      width: 250,
      render: (_: any, record: Chapter) => (
        <Space>
          <Button
            type="link"
            icon={<EyeOutlined />}
            onClick={() => navigate(`/chapters/${record.id}`)}
          >
            查看
          </Button>
          <Button
            type="link"
            icon={<RobotOutlined />}
            onClick={() => navigate(`/chapters/${record.id}/generate`)}
          >
            AI生成
          </Button>
          <Button
            type="link"
            icon={<EditOutlined />}
            onClick={() => navigate(`/chapters/${record.id}/edit`)}
          >
            编辑
          </Button>
          <Popconfirm
            title="确定删除这个章节吗？"
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
            placeholder="搜索章节标题"
            onSearch={(value) => {
              setSearch(value)
              setPage(1)
              loadChapters()
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
            <Option value="completed">已完成</Option>
          </Select>
        </Space>
        <Button
          type="primary"
          icon={<PlusOutlined />}
          onClick={() => navigate(`/novels/${novelId}/chapters/create`)}
        >
          创建章节
        </Button>
      </div>

      <Table
        columns={columns}
        dataSource={chapters}
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

export default ChapterList
