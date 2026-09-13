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

/**
 * 运营看到的订单（含争议信息）。`disputeResolution` 为空表示还没处置；
 * 提交说明与凭证说明一并返回，方便运营在处置前看清双方交了什么。
 */
export interface AdminOrderItem {
  id: string
  taskId: string
  title: string
  status: string
  ownerId: string
  ownerEmail: string | null
  workerId: string
  workerEmail: string | null
  rewardAmount: number
  rewardCurrency: string
  createdAt: string
  submittedAt: string | null
  evidenceNote: string | null
  reviewNote: string | null
  rejectionNote: string | null
  reworkCount: number
  cancelledAt: string | null
  cancelledBy: string | null
  cancellationReason: string | null
  disputeReason: string | null
  disputeOpenedBy: string | null
  disputeOpenedAt: string | null
  disputeResolution: string | null
  disputeResolutionNote: string | null
  disputeResolvedAt: string | null
}

export interface AdminOrderList {
  items: AdminOrderItem[]
  limit: number
}

/** 争议处置结果：强制完成 / 退回返工 / 终止订单。 */
export type DisputeDecision = 'Approve' | 'Rework' | 'Cancel'

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

/** 订单状态中文（运营视角与用户侧文案保持一致）。 */
export function adminOrderStatusLabel(status: string): string {
  const labels: Record<string, string> = {
    Accepted: '待执行',
    InProgress: '执行中',
    Submitted: '待验收',
    Rejected: '需补充执行',
    Disputed: '争议中',
    Approved: '已完成',
    Cancelled: '已取消',
  }
  return labels[status] ?? status
}

/** 争议处置结果中文。 */
export function disputeResolutionLabel(resolution: string): string {
  const labels: Record<string, string> = {
    Approve: '强制完成',
    Rework: '退回返工',
    Cancel: '终止订单',
  }
  return labels[resolution] ?? resolution
}

/**
 * 争议订单列表：`status` 省略时后端只返回待处置的争议（`Disputed`），传 `all` 返回全部历史。
 */
export async function listDisputedOrders(status = '', limit = 20): Promise<AdminOrderList> {
  const query = new URLSearchParams({ limit: String(limit) })
  if (status.trim()) query.set('status', status.trim())
  return parseResponse<AdminOrderList>(await fetch(`/api/v1/admin/orders?${query.toString()}`, { headers: authHeaders() }))
}

/** 处置争议：三选一 + 必填依据（依据会写进运营审计，并通知订单双方）。 */
export async function resolveDispute(orderId: string, decision: DisputeDecision, note: string): Promise<AdminOrderItem> {
  return parseResponse<AdminOrderItem>(await fetch(`/api/v1/admin/orders/${orderId}/resolve`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ decision, note }),
  }))
}
