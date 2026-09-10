import { getAccessToken } from './auth'

export interface OrderMessage {
  id: string
  orderId: string
  senderId: string
  content: string
  createdAt: string
  readAt: string | null
}

export interface OrderMessageList {
  items: OrderMessage[]
  unreadCount: number
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

export async function listOrderMessages(orderId: string, userId: string, limit = 50): Promise<OrderMessageList> {
  return parseResponse<OrderMessageList>(await fetch(`/api/v1/orders/${orderId}/messages?userId=${encodeURIComponent(userId)}&limit=${limit}`, { headers: authHeaders() }))
}

export async function sendOrderMessage(orderId: string, senderId: string, content: string): Promise<OrderMessage> {
  return parseResponse<OrderMessage>(await fetch(`/api/v1/orders/${orderId}/messages`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ senderId, content }),
  }))
}

/** 标记该订单会话里对方发来的消息为已读，返回最新未读数。 */
export async function markOrderMessagesRead(orderId: string, userId: string): Promise<number> {
  const result = await parseResponse<{ unreadCount: number }>(await fetch(`/api/v1/orders/${orderId}/messages/read?userId=${encodeURIComponent(userId)}`, {
    method: 'POST',
    headers: authHeaders(),
  }))
  return result.unreadCount
}
