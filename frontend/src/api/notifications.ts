import { HubConnectionBuilder, HubConnectionState, type HubConnection } from '@microsoft/signalr'
import { getAccessToken } from './auth'

export interface OrderNotificationPayload {
  orderId: string
  status: string
  title: string
}

/** 事件信封：带事件 ID 与版本，客户端据此去重并判断能否解析。 */
export interface NotificationEnvelope {
  eventId: string
  type: string
  version: number
  occurredAt: string
  payload: OrderNotificationPayload | Record<string, unknown>
}

export interface NotificationItem {
  id: string
  eventId: string
  type: string
  version: number
  createdAt: string
  readAt: string | null
  payload: OrderNotificationPayload | Record<string, unknown>
}

export interface NotificationList {
  items: NotificationItem[]
  unreadCount: number
}

/** 客户端只处理当前信封版本；更高版本说明服务端已演进，忽略并靠 REST 刷新。 */
export const supportedEnvelopeVersion = 1

let connection: HubConnection | null = null

export async function connectNotifications(userId: string, onNotification: (envelope: NotificationEnvelope) => void) {
  await disconnectNotifications()
  const token = getAccessToken()
  connection = new HubConnectionBuilder()
    .withUrl(`/hubs/notifications?userId=${encodeURIComponent(userId)}`, { accessTokenFactory: () => token ?? '' })
    .withAutomaticReconnect()
    .build()
  connection.on('notification.created', (envelope: NotificationEnvelope) => {
    if (!envelope || typeof envelope.version !== 'number') return
    if (envelope.version > supportedEnvelopeVersion) return
    onNotification(envelope)
  })
  await connection.start()
}

export async function disconnectNotifications() {
  if (connection && connection.state !== HubConnectionState.Disconnected) await connection.stop()
  connection = null
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

export async function listNotifications(userId: string, limit = 20): Promise<NotificationList> {
  return parseResponse<NotificationList>(await fetch(`/api/v1/notifications?userId=${encodeURIComponent(userId)}&limit=${limit}`, { headers: authHeaders() }))
}

/** 标记已读：不传 ids 表示全部标记为已读，返回最新未读数。 */
export async function markNotificationsRead(userId: string, ids?: string[]): Promise<number> {
  const result = await parseResponse<{ unreadCount: number }>(await fetch('/api/v1/notifications/read', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ userId, ids: ids ?? null }),
  }))
  return result.unreadCount
}
