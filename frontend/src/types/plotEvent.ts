export type EventType = 'battle' | 'dialogue' | 'travel' | 'discovery' | 'romance' | 'death' | 'mystery' | 'other'

export interface PlotEvent {
  id: number
  novel_id: number
  chapter_id: number
  event_type: EventType
  description: string
  characters_involved: number[]
  timeline: string
  consequences: {
    result: string
    impact: string[]
  }
  created_at: string
}

export interface PlotEventDetail extends PlotEvent {
  novel?: {
    id: number
    title: string
  }
  chapter?: {
    id: number
    chapter_number: number
    title: string
  }
}

export interface PlotEventCreate {
  chapter_id: number
  event_type: string
  description: string
  characters_involved: number[]
  timeline: string
  consequences?: {
    result: string
    impact: string[]
  }
}

export interface PlotEventUpdate {
  chapter_id?: number
  event_type?: string
  description?: string
  characters_involved?: number[]
  timeline?: string
  consequences?: {
    result: string
    impact: string[]
  }
}

export interface PlotEventListParams {
  page?: number
  page_size?: number
  chapter_id?: number
  event_type?: EventType
}
