export interface Character {
  id: number
  novel_id: number
  name: string
  personality: {
    traits: string[]
    background: string
  }
  relationships: {
    friend?: number[]
    enemy?: number[]
  }
  abilities: string[]
  created_at: string
}

export interface CharacterDetail extends Character {
  novel?: {
    id: number
    title: string
  }
}

export interface CharacterCreate {
  name: string
  personality: {
    traits: string[]
    background: string
  }
  relationships?: {
    friend?: number[]
    enemy?: number[]
  }
  abilities?: string[]
}

export interface CharacterUpdate {
  name?: string
  personality?: {
    traits: string[]
    background: string
  }
  relationships?: {
    friend?: number[]
    enemy?: number[]
  }
  abilities?: string[]
}

export interface CharacterListParams {
  page?: number
  page_size?: number
  search?: string
}
