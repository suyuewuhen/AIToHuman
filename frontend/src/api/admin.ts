import { getAccessToken } from './auth'

/** 运营后台的任务摘要（跨所有者）。 */
export interface AdminTaskItem {
  id: string
  title: string
  district: string
  status: string
  rewardAmount: number
  rewardCurrency: string
  deadline: string
  createdAt: string
  ownerId: string
  ownerDisplayName: string | null
  ownerEmail: string | null
  applicationCount: number
  hasExecutionAddress: boolean
  orderId: string | null
  orderStatus: string | null
}

export interface AdminTaskList {
  items: AdminTaskItem[]
  limit: number
}

export interface AdminUserItem {
  id: string
  email: string
  displayName: string
  role: string
  createdAt: string
}

export interface AdminAuditItem {
  id: string
  actorId: string
  action: string
  targetType: string
  targetId: string
  reason: string
  occurredAt: string
}

async function parseResponse<T>(response: Response): Promise<T> {
  if (response.ok) return response.json() as Promise<T>
  const problem = await response.json().catch(() => null) as { detail?: string; title?: string } | null
  throw new Error(problem?.detail ?? problem?.title ?? `请求失败（${response.status}）`)
}

function authHeaders(): HeadersInit {
  const token = getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

export async function searchAdminTasks(keyword: string, status: string, limit = 20): Promise<AdminTaskList> {
  const query = new URLSearchParams({ limit: String(limit) })
  if (keyword.trim()) query.set('keyword', keyword.trim())
  if (status.trim()) query.set('status', status.trim())
  return parseResponse<AdminTaskList>(await fetch(`/api/v1/admin/tasks?${query.toString()}`, { headers: authHeaders() }))
}

/** 人工下架：必须给原因，原因会写进运营审计；已产生订单的任务会被服务端拒绝（422）。 */
export async function cancelAdminTask(taskId: string, reason: string): Promise<AdminTaskItem> {
  return parseResponse<AdminTaskItem>(await fetch(`/api/v1/admin/tasks/${taskId}/cancel`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ reason }),
  }))
}

export async function searchAdminUsers(keyword: string, limit = 20): Promise<{ items: AdminUserItem[]; limit: number }> {
  const query = new URLSearchParams({ limit: String(limit) })
  if (keyword.trim()) query.set('keyword', keyword.trim())
  return parseResponse<{ items: AdminUserItem[]; limit: number }>(await fetch(`/api/v1/admin/users?${query.toString()}`, { headers: authHeaders() }))
}

/** 运营操作审计（人工下架等），按时间倒序。 */
export async function listAdminAudits(limit = 20): Promise<AdminAuditItem[]> {
  return parseResponse<AdminAuditItem[]>(await fetch(`/api/v1/admin/audits?limit=${limit}`, { headers: authHeaders() }))
}

export function adminTaskStatusLabel(status: string): string {
  const labels: Record<string, string> = {
    ReadyToPublish: '草稿',
    Published: '大厅中',
    Assigned: '已分配',
    Closed: '已结束',
    Expired: '已过期',
    Cancelled: '已下架',
  }
  return labels[status] ?? status
}
