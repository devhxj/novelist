import { useEffect, useState } from 'react'
import { Card, Descriptions, Tag, Button, Space, message } from 'antd'
import { useParams, useNavigate } from 'react-router-dom'
import { characterApi } from '@/services/characterService'
import type { CharacterDetail } from '@/types/character'
import dayjs from 'dayjs'

function CharacterDetailPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const [character, setCharacter] = useState<CharacterDetail | null>(null)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    if (id) {
      loadCharacter(parseInt(id))
    }
  }, [id])

  const loadCharacter = async (characterId: number) => {
    setLoading(true)
    try {
      const response = await characterApi.getCharacter(characterId)
      if (response.success) {
        setCharacter(response.data)
      }
    } catch (error: any) {
      message.error(error.error?.message || '加载角色详情失败')
    } finally {
      setLoading(false)
    }
  }

  if (!character) return null

  return (
    <Card
      title={character.name}
      extra={
        <Space>
          <Button onClick={() => navigate(`/novels/${character.novel_id}/characters`)}>
            返回列表
          </Button>
          <Button type="primary" onClick={() => navigate(`/characters/${id}/edit`)}>
            编辑
          </Button>
        </Space>
      }
      loading={loading}
    >
      <Descriptions bordered column={2}>
        <Descriptions.Item label="ID">{character.id}</Descriptions.Item>
        <Descriptions.Item label="所属小说">
          {character.novel?.title || '-'}
        </Descriptions.Item>
        <Descriptions.Item label="创建时间">
          {dayjs(character.created_at).format('YYYY-MM-DD HH:mm:ss')}
        </Descriptions.Item>
        <Descriptions.Item label="性格特征">
          <Space wrap>
            {character.personality?.traits?.map((trait, index) => (
              <Tag key={index} color="blue">{trait}</Tag>
            ))}
          </Space>
        </Descriptions.Item>
        <Descriptions.Item label="背景故事" span={2}>
          {character.personality?.background || '-'}
        </Descriptions.Item>
        <Descriptions.Item label="朋友关系">
          <Space wrap>
            {character.relationships?.friend && character.relationships.friend.length > 0 ? (
              character.relationships.friend.map((friendId, index) => (
                <Tag key={index} color="green">角色ID: {friendId}</Tag>
              ))
            ) : (
              <span>-</span>
            )}
          </Space>
        </Descriptions.Item>
        <Descriptions.Item label="敌对关系">
          <Space wrap>
            {character.relationships?.enemy && character.relationships.enemy.length > 0 ? (
              character.relationships.enemy.map((enemyId, index) => (
                <Tag key={index} color="red">角色ID: {enemyId}</Tag>
              ))
            ) : (
              <span>-</span>
            )}
          </Space>
        </Descriptions.Item>
        <Descriptions.Item label="能力" span={2}>
          <Space wrap>
            {character.abilities?.length > 0 ? (
              character.abilities.map((ability, index) => (
                <Tag key={index} color="purple">{ability}</Tag>
              ))
            ) : (
              <span>-</span>
            )}
          </Space>
        </Descriptions.Item>
      </Descriptions>
    </Card>
  )
}

export default CharacterDetailPage
