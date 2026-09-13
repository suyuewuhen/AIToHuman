import { apiFetch } from './base'
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

/**
 * 运营风险复核队列里的一条任务：命中的规则（原因代码 + 类别 + 说明 + 规则版本）与任务原文一并返回，
 * 运营不必再跳到别处凑上下文。被禁止的类别不会进这个队列——人工无权放行。
 */
export interface AdminRiskReviewItem {
  taskId: string
  title: string
  description: string
  district: string
  rewardAmount: number
  rewardCurrency: string
  deadline: string
  createdAt: string
  ownerId: string
  ownerDisplayName: string | null
  ownerEmail: string | null
  verdict: string
  ruleCode: string | null
  category: string | null
  summary: string | null
  ruleVersion: number
  assessedAt: string | null
  reviewStatus: string
  taskStatus: string
  reviewedAt: string | null
  reviewNote: string | null
  reviewedBy: string | null
}

export interface AdminRiskReviewList {
  items: AdminRiskReviewItem[]
  limit: number
}

/** 风险复核结论：放行（之后可发布）或驳回（不能发布）。 */
export type RiskReviewDecision = 'Approve' | 'Reject'

/** 规则目录自述：说明现在按什么规则拦（含版本），但不返回匹配词。 */
export interface RiskRuleItem {
  code: string
  category: string
  verdict: string
  description: string
  keywordCount: number
}

export interface RiskRuleCatalog {
  version: number
  highRewardThreshold: number
  rules: RiskRuleItem[]
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
  return parseResponse<AdminTaskList>(await apiFetch(`/api/v1/admin/tasks?${query.toString()}`, { headers: authHeaders() }))
}

/** 人工下架：必须给原因，原因会写进运营审计；已产生订单的任务会被服务端拒绝（422）。 */
export async function cancelAdminTask(taskId: string, reason: string): Promise<AdminTaskItem> {
  return parseResponse<AdminTaskItem>(await apiFetch(`/api/v1/admin/tasks/${taskId}/cancel`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ reason }),
  }))
}

export async function searchAdminUsers(keyword: string, limit = 20): Promise<{ items: AdminUserItem[]; limit: number }> {
  const query = new URLSearchParams({ limit: String(limit) })
  if (keyword.trim()) query.set('keyword', keyword.trim())
  return parseResponse<{ items: AdminUserItem[]; limit: number }>(await apiFetch(`/api/v1/admin/users?${query.toString()}`, { headers: authHeaders() }))
}

/** 运营操作审计（人工下架等），按时间倒序。 */
export async function listAdminAudits(limit = 20): Promise<AdminAuditItem[]> {
  return parseResponse<AdminAuditItem[]>(await apiFetch(`/api/v1/admin/audits?limit=${limit}`, { headers: authHeaders() }))
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
  return parseResponse<AdminOrderList>(await apiFetch(`/api/v1/admin/orders?${query.toString()}`, { headers: authHeaders() }))
}

/** 处置争议：三选一 + 必填依据（依据会写进运营审计，并通知订单双方）。 */
export async function resolveDispute(orderId: string, decision: DisputeDecision, note: string): Promise<AdminOrderItem> {
  return parseResponse<AdminOrderItem>(await apiFetch(`/api/v1/admin/orders/${orderId}/resolve`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ decision, note }),
  }))
}

/** 风险复核队列：等人工处置的任务，按创建时间升序（先来先处理）。 */
export async function listRiskReviews(limit = 20): Promise<AdminRiskReviewList> {
  return parseResponse<AdminRiskReviewList>(await apiFetch(`/api/v1/admin/risk/reviews?limit=${limit}`, { headers: authHeaders() }))
}

/**
 * 运营看到的待处置申诉。`canBeReleasedByAppeal` 为 false 表示这是禁止类别命中：
 * 申诉成立也只记录"规则误伤"的结论，任务依旧不能发布。
 */
export interface AdminRiskAppealItem {
  taskId: string
  title: string
  description: string
  district: string
  rewardAmount: number
  rewardCurrency: string
  ownerId: string
  ownerEmail: string | null
  verdict: string
  ruleCode: string | null
  category: string | null
  summary: string | null
  ruleVersion: number
  reviewStatus: string
  reviewNote: string | null
  appealStatus: string
  appealReason: string | null
  appealedAt: string | null
  canBeReleasedByAppeal: boolean
}

export interface AdminRiskAppealList {
  items: AdminRiskAppealItem[]
  limit: number
}

/** 申诉处置结论：Accept 认为误伤，Deny 维持原判。 */
export type RiskAppealDecision = 'Accept' | 'Deny'

/** 待处置的申诉队列，按提交时间升序。 */
export async function listRiskAppeals(limit = 20): Promise<AdminRiskAppealList> {
  return parseResponse<AdminRiskAppealList>(await apiFetch(`/api/v1/admin/risk/appeals?limit=${limit}`, { headers: authHeaders() }))
}

/** 处置申诉：依据必填（写进运营审计），并通知任务所有者。 */
export async function decideRiskAppeal(taskId: string, decision: RiskAppealDecision, note: string): Promise<AdminRiskAppealItem> {
  return parseResponse<AdminRiskAppealItem>(await apiFetch(`/api/v1/admin/risk/appeals/${taskId}/decide`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ decision, note }),
  }))
}

export function riskAppealStatusLabel(status: string): string {
  const labels: Record<string, string> = {
    None: '未申诉',
    Pending: '申诉待处置',
    Accepted: '申诉成立（误判）',
    Denied: '申诉被驳回',
  }
  return labels[status] ?? status
}

/** 处置风险复核：放行或驳回 + 必填依据（依据写进运营审计，并通知任务所有者）。 */
export async function decideRiskReview(taskId: string, decision: RiskReviewDecision, note: string): Promise<AdminRiskReviewItem> {
  return parseResponse<AdminRiskReviewItem>(await apiFetch(`/api/v1/admin/risk/reviews/${taskId}/decide`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ decision, note }),
  }))
}

/** 规则目录：现在按什么规则拦、哪一版；只给匹配词数量，不给匹配词本身。 */
export async function getRiskRules(): Promise<RiskRuleCatalog> {
  return parseResponse<RiskRuleCatalog>(await apiFetch('/api/v1/admin/risk/rules', { headers: authHeaders() }))
}

/** 规则明细里的一条：连匹配词一起给出（只有运营能读这个接口）。 */
export interface RiskRuleDetailItem {
  code: string
  category: string
  verdict: string
  description: string
  keywords: string[]
}

/**
 * 当前生效的规则目录明细。`isBuiltIn` 为真表示还没有运营覆盖（版本 1 就是代码里的内置目录）；
 * `changeSummary` / `changeReason` / `updatedBy` 描述最近一版改动的来龙去脉。
 */
export interface RiskRuleCatalogDetail {
  version: number
  isBuiltIn: boolean
  highRewardThreshold: number
  nightWindowStart: string
  nightWindowEnd: string
  rules: RiskRuleDetailItem[]
  changeSummary: string | null
  changeReason: string | null
  updatedBy: string | null
  updatedByName: string | null
  updatedAt: string | null
}

/** 版本历史里的一条：只看改动摘要与依据，不看当时的完整词表。 */
export interface RiskRuleCatalogVersion {
  version: number
  changeSummary: string
  changeReason: string
  updatedBy: string
  updatedByName: string | null
  createdAt: string
  ruleCount: number
  blockedRuleCount: number
  highRewardThreshold: number
  nightWindowStart: string
  nightWindowEnd: string
}

/** 提交一版规则：整份目录替换，`expectedVersion` 用来挡住“拿着旧目录覆盖别人刚改的内容”（不一致会返回 409）。 */
export interface UpdateRiskRuleCatalogInput {
  expectedVersion: number
  reason: string
  highRewardThreshold: number
  nightWindowStart: string
  nightWindowEnd: string
  rules: RiskRuleDetailItem[]
}

export async function getRiskRuleDetail(): Promise<RiskRuleCatalogDetail> {
  return parseResponse<RiskRuleCatalogDetail>(await apiFetch('/api/v1/admin/risk/rules/detail', { headers: authHeaders() }))
}

export async function listRiskRuleVersions(limit = 20): Promise<{ items: RiskRuleCatalogVersion[]; limit: number }> {
  return parseResponse<{ items: RiskRuleCatalogVersion[]; limit: number }>(
    await apiFetch(`/api/v1/admin/risk/rules/versions?limit=${limit}`, { headers: authHeaders() }))
}

/** 保存规则目录：版本号自动 +1，依据必填并写进运营审计；改动内容不合法时服务端返回可读的 422。 */
export async function updateRiskRules(input: UpdateRiskRuleCatalogInput): Promise<RiskRuleCatalogDetail> {
  return parseResponse<RiskRuleCatalogDetail>(await apiFetch('/api/v1/admin/risk/rules', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify(input),
  }))
}

/** 恢复到代码内置目录：同样追加一版（版本历史不丢），依据必填并写进运营审计。 */
export async function resetRiskRules(expectedVersion: number, reason: string): Promise<RiskRuleCatalogDetail> {
  return parseResponse<RiskRuleCatalogDetail>(await apiFetch('/api/v1/admin/risk/rules/reset', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ expectedVersion, reason }),
  }))
}

export function riskVerdictLabel(verdict: string): string {
  const labels: Record<string, string> = {
    Allowed: '规则放行',
    NeedsReview: '需人工复核',
    Blocked: '平台禁止',
  }
  return labels[verdict] ?? verdict
}

export function riskReviewStatusLabel(status: string): string {
  const labels: Record<string, string> = {
    NotRequired: '无需复核',
    Pending: '待复核',
    Approved: '已放行',
    Rejected: '已驳回',
  }
  return labels[status] ?? status
}
