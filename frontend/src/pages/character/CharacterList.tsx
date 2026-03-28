import { useState, useEffect } from 'react'
import { Table, Button, Space, Tag, Input, message, Popconfirm } from 'antd'
import { PlusOutlined, EditOutlined, DeleteOutlined, EyeOutlined } from '@ant-design/icons'
import { useNavigate, useParams } from 'react-router-dom'
import { characterApi } from '@/services/characterService'
import type { Character } from '@/types/character'
import dayjs from 'dayjs'

const { Search } = Input

function CharacterList() {
  const navigate = useNavigate()
  const { novelId } = useParams<{ novelId: string }>()
  const [characters, setCharacters] = useState<Character[]>([])
  const [loading, setLoading] = useState(false)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [total, setTotal] = useState(0)
  const [search, setSearch] = useState('')

  useEffect(() => {
    if (novelId) {
      loadCharacters()
    }
  }, [page, pageSize, novelId])

  const loadCharacters = async () => {
    if (!novelId) return
    
    setLoading(true)
    try {
      const response = await characterApi.getCharacters(parseInt(novelId), {
        page,
        page_size: pageSize,
        search: search || undefined,
      })
      if (response.success) {
        setCharacters(response.data.items)
        setTotal(response.data.total)
      }
    } catch (error: any) {
      message.error(error.error?.message || '加载角色列表失败')
    } finally {
      setLoading(false)
    }
  }

  const handleDelete = async (id: number) => {
    try {
      const response = await characterApi.deleteCharacter(id)
      if (response.success) {
        message.success('删除成功')
        setCharacters(characters.filter(c => c.id !== id))
        setTotal(total - 1)
      }
    } catch (error: any) {
      message.error(error.error?.message || '删除失败')
    }
  }

  const columns = [
    {
      title: 'ID',
      dataIndex: 'id',
      key: 'id',
      width: 80,
    },
    {
      title: '角色名',
      dataIndex: 'name',
      key: 'name',
      render: (text: string, record: Character) => (
        <a onClick={() => navigate(`/characters/${record.id}`)}>{text}</a>
      ),
    },
    {
      title: '性格特征',
      dataIndex: 'personality',
      key: 'traits',
      render: (personality: any) => (
        <Space wrap>
          {personality?.traits?.slice(0, 3).map((trait: string, index: number) => (
            <Tag key={index} color="blue">{trait}</Tag>
          ))}
          {personality?.traits?.length > 3 && (
            <Tag>+{personality.traits.length - 3}</Tag>
          )}
        </Space>
      ),
    },
    {
      title: '能力',
      dataIndex: 'abilities',
      key: 'abilities',
      render: (abilities: string[]) => (
        <Space wrap>
          {abilities?.slice(0, 2).map((ability, index) => (
            <Tag key={index} color="green">{ability}</Tag>
          ))}
          {abilities?.length > 2 && (
            <Tag>+{abilities.length - 2}</Tag>
          )}
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
      title: '操作',
      key: 'action',
      width: 200,
      render: (_: any, record: Character) => (
        <Space>
          <Button
            type="link"
            icon={<EyeOutlined />}
            onClick={() => navigate(`/characters/${record.id}`)}
          >
            查看
          </Button>
          <Button
            type="link"
            icon={<EditOutlined />}
            onClick={() => navigate(`/characters/${record.id}/edit`)}
          >
            编辑
          </Button>
          <Popconfirm
            title="确定删除这个角色吗？"
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
        <Search
          placeholder="搜索角色名"
          onSearch={(value) => {
            setSearch(value)
            setPage(1)
            loadCharacters()
          }}
          style={{ width: 300 }}
        />
        <Button
          type="primary"
          icon={<PlusOutlined />}
          onClick={() => navigate(`/novels/${novelId}/characters/create`)}
        >
          创建角色
        </Button>
      </div>

      <Table
        columns={columns}
        dataSource={characters}
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

export default CharacterList
