export interface TaskApplication {
  id: string
  workerId: string
  note: string
  status: string
  submittedAt: string
}

export interface TaskItem {
  id: string
  ownerId: string
  title: string
  description: string
  district: string
  deadline: string
  reward: number
  currency: string
  status: string
  acceptanceCriteria: string[]
  applicationCount: number
  hasExecutionAddress?: boolean
}

export interface OrderItem {
  id: string
  taskId: string
  ownerId: string
  workerId: string
  title: string
  reward: number
  currency: string
  status: string
  createdAt: string
  evidenceNote?: string | null
  reviewNote?: string | null
  submittedAt?: string | null
  reviewedAt?: string | null
  reworkCount: number
  rejectionNote?: string | null
  unreadMessageCount?: number
}

export interface ReviewItem {
  id: string
  orderId: string
  reviewerId: string
  revieweeId: string
  rating: number
  comment: string
  createdAt: string
  isVisible: boolean
}
export interface ReviewSummary {
  userId: string
  averageRating: number
  reviewCount: number
  recentReviews: ReviewItem[]
}

export interface SelectTaskResult {
  task: TaskItem
  order: OrderItem
}

export interface CreateTaskInput {
  ownerId: string
  title: string
  description: string
  district: string
  deadline: string
  reward: number
  acceptanceCriteria: string[]
  executionAddress?: string
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

export interface TaskListPage {
  items: TaskItem[]
  nextCursor: string | null
  hasMore: boolean
}

export interface TaskListQuery {
  district?: string
  minReward?: number
  maxReward?: number
  limit?: number
  cursor?: string
}

export async function listPublishedTasks(query: TaskListQuery = {}): Promise<TaskListPage> {
  const params = new URLSearchParams()
  if (query.district) params.set('district', query.district)
  if (query.minReward !== undefined) params.set('minReward', String(query.minReward))
  if (query.maxReward !== undefined) params.set('maxReward', String(query.maxReward))
  params.set('limit', String(query.limit ?? 12))
  if (query.cursor) params.set('cursor', query.cursor)
  return parseResponse<TaskListPage>(await fetch(`/api/v1/tasks?${params.toString()}`, { headers: authHeaders() }))
}

/** 精确执行地址：只有所有者与被选中的服务者能读到值。 */
export async function getExecutionAddress(taskId: string, userId: string): Promise<{ taskId: string; executionAddress: string | null }> {
  return parseResponse<{ taskId: string; executionAddress: string | null }>(
    await fetch(`/api/v1/tasks/${taskId}/execution-address?userId=${encodeURIComponent(userId)}`, { headers: authHeaders() }),
  )
}

export async function listMyTasks(ownerId: string): Promise<TaskItem[]> {
  return parseResponse<TaskItem[]>(await fetch(`/api/v1/tasks/mine?ownerId=${encodeURIComponent(ownerId)}`, { headers: authHeaders() }))
}

export async function createTask(input: CreateTaskInput): Promise<TaskItem> {
  return parseResponse<TaskItem>(await fetch('/api/v1/tasks', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify(input),
  }))
}

export async function publishTask(taskId: string, ownerId: string): Promise<TaskItem> {
  return parseResponse<TaskItem>(await fetch(`/api/v1/tasks/${taskId}/publish?ownerId=${encodeURIComponent(ownerId)}`, {
    method: 'POST',
    headers: authHeaders(),
  }))
}

export async function increaseTaskReward(taskId: string, ownerId: string, reward: number): Promise<TaskItem> {
  return parseResponse<TaskItem>(await fetch(`/api/v1/tasks/${taskId}/increase-reward?ownerId=${encodeURIComponent(ownerId)}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ reward }),
  }))
}

export async function listMyOrders(userId: string): Promise<OrderItem[]> {
  return parseResponse<OrderItem[]>(await fetch(`/api/v1/orders?userId=${encodeURIComponent(userId)}`, { headers: authHeaders() }))
}
async function orderAction(orderId: string, action: 'start' | 'submit' | 'approve' | 'reject' | 'resume', actorId: string, note?: string): Promise<OrderItem> {
  return parseResponse<OrderItem>(await fetch(`/api/v1/orders/${orderId}/${action}`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', ...authHeaders() }, body: JSON.stringify({ actorId, note }),
  }))
}

export const startOrder = (orderId: string, actorId: string) => orderAction(orderId, 'start', actorId)
export const submitOrder = (orderId: string, actorId: string, note: string) => orderAction(orderId, 'submit', actorId, note)
export const approveOrder = (orderId: string, actorId: string, note?: string) => orderAction(orderId, 'approve', actorId, note)
export const rejectOrder = (orderId: string, actorId: string, note: string) => orderAction(orderId, 'reject', actorId, note)
export const resumeOrder = (orderId: string, actorId: string) => orderAction(orderId, 'resume', actorId)

export async function listOrderReviews(orderId: string): Promise<ReviewItem[]> {
  return parseResponse<ReviewItem[]>(await fetch(`/api/v1/orders/${orderId}/reviews`, { headers: authHeaders() }))
}

export async function createOrderReview(orderId: string, reviewerId: string, rating: number, comment: string): Promise<ReviewItem> {
  return parseResponse<ReviewItem>(await fetch(`/api/v1/orders/${orderId}/reviews`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', ...authHeaders() }, body: JSON.stringify({ reviewerId, rating, comment }),
  }))
}

export async function getReviewSummary(userId: string): Promise<ReviewSummary> {
  return parseResponse<ReviewSummary>(await fetch(`/api/v1/users/${userId}/review-summary`, { headers: authHeaders() }))
}

export async function applyForTask(taskId: string, workerId: string, note: string): Promise<TaskItem> {
  return parseResponse<TaskItem>(await fetch(`/api/v1/tasks/${taskId}/applications`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ workerId, note }),
  }))
}

export async function listTaskApplications(taskId: string, ownerId: string): Promise<TaskApplication[]> {
  return parseResponse<TaskApplication[]>(await fetch(`/api/v1/tasks/${taskId}/applications?ownerId=${encodeURIComponent(ownerId)}`, { headers: authHeaders() }))
}

export async function selectTaskApplication(taskId: string, applicationId: string, ownerId: string): Promise<SelectTaskResult> {
  return parseResponse<SelectTaskResult>(await fetch(`/api/v1/tasks/${taskId}/applications/${applicationId}/select`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ ownerId }),
  }))
}
import { getAccessToken } from './auth'
