import type { AiTaskPlan } from './ai'
import { getAccessToken } from './auth'

export interface ConversationMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  createdAt: string
  readyToDraft: boolean
  plan: AiTaskPlan | null
}

export interface Conversation {
  id: string
  userId: string
  createdAt: string
  updatedAt: string
  messages: ConversationMessage[]
  draft: AiTaskPlan | null
}

export interface ConversationSummary {
  id: string
  createdAt: string
  updatedAt: string
  messageCount: number
  preview: string
  hasDraft: boolean
}

async function parseResponse<T>(response: Response): Promise<T> {
  if (response.ok) return response.json() as Promise<T>
  const problem = await response.json().catch(() => null) as { detail?: string } | null
  throw new Error(problem?.detail ?? `请求失败（${response.status}）`)
}

function authHeaders(): HeadersInit {
  const token = getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

export async function createConversation(userId: string): Promise<Conversation> {
  return parseResponse<Conversation>(await fetch('/api/v1/conversations', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ userId }),
  }))
}

export async function getConversation(conversationId: string, userId: string): Promise<Conversation> {
  return parseResponse<Conversation>(await fetch(`/api/v1/conversations/${conversationId}?userId=${encodeURIComponent(userId)}`, { headers: authHeaders() }))
}

export async function listConversations(userId: string, limit = 20): Promise<ConversationSummary[]> {
  return parseResponse<ConversationSummary[]>(await fetch(`/api/v1/conversations?userId=${encodeURIComponent(userId)}&limit=${limit}`, { headers: authHeaders() }))
}
