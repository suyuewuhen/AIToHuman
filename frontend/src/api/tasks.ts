export interface TaskApplication {
  id: string
  workerId: string
  note: string
  status: string
  submittedAt: string
  /** 这名服务者的公开评价均分；没有公开评价时为 0，看 workerReviewCount 判断。 */
  workerAverageRating: number
  /** 公开评价条数（盲期内的评价不计入）。 */
  workerReviewCount: number
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
  /** 后端定时扫描发现截止时间已过时写入。 */
  expiredAt?: string | null
  cancelledAt?: string | null
  cancellationReason?: string | null
  /** 报名截止时间；为空表示只能报到大厅里的任务截止时间。 */
  applicationDeadline?: string | null
  /** 是否还在接收报名，由服务端按当前时间判定；客户端不要自己算。 */
  acceptingApplications?: boolean
  /** 确定性风险规则的结论：Allowed / NeedsReview / Blocked。 */
  riskVerdict?: string
  /** 命中的原因代码，例如 prohibited.exam_impersonation；放行时为空。 */
  riskRuleCode?: string | null
  /** 命中规则的类别名（面向人）。 */
  riskCategory?: string | null
  /** 可展示的说明：说清违反了哪一类规则，但不含具体命中的词。 */
  riskSummary?: string | null
  /** 判定时使用的规则目录版本。 */
  riskRuleVersion?: number
  /** 人工复核状态：NotRequired / Pending / Approved / Rejected。 */
  riskReviewStatus?: string
  /** 运营复核意见（仅复核过的任务有值）。 */
  riskReviewNote?: string | null
  /** 发布这一关是否被风险处置卡住，由服务端判定，客户端不要自己推算。 */
  riskPublishBlocked?: boolean
  /** 还能不能编辑草稿（只有 ReadyToPublish 可以），同样由服务端判定。 */
  draftEditable?: boolean
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
  cancelledAt?: string | null
  cancelledBy?: string | null
  cancellationReason?: string | null
  /** 争议：原因与发起人在发起时写入，处置结果与依据由运营填写。 */
  disputeReason?: string | null
  disputeOpenedBy?: string | null
  disputeOpenedAt?: string | null
  /** `Approve`（强制完成）/ `Rework`（退回返工）/ `Cancel`（终止订单）；为空表示还没处置。 */
  disputeResult?: string | null
  disputeResolutionNote?: string | null
  disputeResolvedAt?: string | null
}

/** 服务者视角的一条报名：`canWithdraw` 由服务端判定，客户端不要自己推算。 */
export interface MyApplicationItem {
  applicationId: string
  taskId: string
  title: string
  district: string
  reward: number
  currency: string
  deadline: string
  applicationDeadline: string | null
  taskStatus: string
  applicationStatus: string
  submittedAt: string
  canWithdraw: boolean
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
  /** 报名截止时间；必须在任务截止时间之前。 */
  applicationDeadline?: string
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

/** 草稿的一版历史快照：第 1 版是创建，之后每次编辑追加一版。 */
export interface TaskDraftRevision {
  id: string
  taskId: string
  revision: number
  title: string
  description: string
  district: string
  deadline: string
  reward: number
  currency: string
  acceptanceCriteria: string[]
  executionAddress: string | null
  applicationDeadline: string | null
  /** 这一版文本对应的风险结论（Allowed / NeedsReview / Blocked）。 */
  riskVerdict: string
  riskRuleCode: string | null
  riskRuleVersion: number
  editedBy: string
  /** 这次改了哪些字段，例如“标题、截止时间”；创建那一版是“创建草稿”。 */
  changeSummary: string
  createdAt: string
}

/** 草稿历史（仅所有者可读）。 */
export async function listTaskDraftRevisions(taskId: string, ownerId: string): Promise<TaskDraftRevision[]> {
  const response = await parseResponse<{ items: TaskDraftRevision[] }>(
    await fetch(`/api/v1/tasks/${taskId}/revisions?ownerId=${encodeURIComponent(ownerId)}`, { headers: authHeaders() }),
  )
  return response.items
}

/**
 * 编辑草稿：字段与创建任务完全一致，只有还没发布的草稿能改。
 * 服务端会在保存后重新判定风险并清空原有的人工复核结论，所以返回的任务里
 * `riskReviewStatus` 可能从 Approved 变回 Pending——页面要按返回值刷新提示。
 */
export async function updateTaskDraft(taskId: string, input: CreateTaskInput): Promise<TaskItem> {
  return parseResponse<TaskItem>(await fetch(`/api/v1/tasks/${taskId}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify(input),
  }))
}

export async function increaseTaskReward(taskId: string, ownerId: string, reward: number): Promise<TaskItem> {
  return parseResponse<TaskItem>(await fetch(`/api/v1/tasks/${taskId}/increase-reward?ownerId=${encodeURIComponent(ownerId)}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ reward }),
  }))
}

export async function cancelTask(taskId: string, ownerId: string, reason: string): Promise<TaskItem> {
  return parseResponse<TaskItem>(await fetch(`/api/v1/tasks/${taskId}/cancel`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ ownerId, reason }),
  }))
}

export async function listMyOrders(userId: string): Promise<OrderItem[]> {
  return parseResponse<OrderItem[]>(await fetch(`/api/v1/orders?userId=${encodeURIComponent(userId)}`, { headers: authHeaders() }))
}
async function orderAction(orderId: string, action: 'start' | 'submit' | 'approve' | 'reject' | 'resume' | 'cancel' | 'dispute', actorId: string, note?: string): Promise<OrderItem> {
  return parseResponse<OrderItem>(await fetch(`/api/v1/orders/${orderId}/${action}`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', ...authHeaders() }, body: JSON.stringify({ actorId, note }),
  }))
}

export const startOrder = (orderId: string, actorId: string) => orderAction(orderId, 'start', actorId)
export const submitOrder = (orderId: string, actorId: string, note: string) => orderAction(orderId, 'submit', actorId, note)
export const approveOrder = (orderId: string, actorId: string, note?: string) => orderAction(orderId, 'approve', actorId, note)
export const rejectOrder = (orderId: string, actorId: string, note: string) => orderAction(orderId, 'reject', actorId, note)
export const resumeOrder = (orderId: string, actorId: string) => orderAction(orderId, 'resume', actorId)
/** 取消订单：原因必填（≤200 字），会随订单取消记录一起通知对方。 */
export const cancelOrder = (orderId: string, actorId: string, reason: string) => orderAction(orderId, 'cancel', actorId, reason)

/**
 * 申请平台介入：需求方在服务者提交验收后发起，服务者在验收被驳回后发起，原因必填（≤500 字）。
 * 争议期间订单冻结，双方都不能再提交/验收/驳回/取消，等运营处置。
 */
export const openDispute = (orderId: string, actorId: string, reason: string) => orderAction(orderId, 'dispute', actorId, reason)

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

/** 服务者视角的“我的报名”，含是否还能撤回（服务端判定）。 */
export async function listMyApplications(workerId: string, limit = 20): Promise<MyApplicationItem[]> {
  const params = new URLSearchParams({ workerId, limit: String(limit) })
  const result = await parseResponse<{ items: MyApplicationItem[] }>(await fetch(`/api/v1/tasks/applications/mine?${params.toString()}`, { headers: authHeaders() }))
  return result.items
}

/** 撤回报名：只能在报名还处于待处理、且任务尚未选中别人时撤回；撤回后可以重新报名。 */
export async function withdrawApplication(taskId: string, applicationId: string, workerId: string): Promise<TaskItem> {
  return parseResponse<TaskItem>(await fetch(`/api/v1/tasks/${taskId}/applications/${applicationId}/withdraw`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ workerId }),
  }))
}

export async function selectTaskApplication(taskId: string, applicationId: string, ownerId: string): Promise<SelectTaskResult> {
  return parseResponse<SelectTaskResult>(await fetch(`/api/v1/tasks/${taskId}/applications/${applicationId}/select`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ ownerId }),
  }))
}
import { getAccessToken } from './auth'
