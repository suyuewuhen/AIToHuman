<!--
  运营后台（独立页面）：由 ops.html 挂载，和主应用（index.html）分开打包、分开访问。

  为什么单独一页：运营做的事（改配置、处置争议、复核风险、编辑规则目录）和需求方/服务者的
  日常流程是两种使用场景，塞在同一个页面的弹窗里既容易误点，也让主应用越来越重；
  拆开之后运营可以直接收藏 /ops.html，主应用只留一个入口链接。

  权限完全由服务端判定：这里每个请求都走 /api/v1/admin/* 与 /api/v1/admin/settings，
  非管理员一律 403；页面上的“有没有入口”只是提示，不是授权。
-->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { APP_HOME_URL } from '../api/base'
import { clearAccessToken, getAccessToken, getCurrentUser, login, type CurrentUser } from '../api/auth'
import { adminAuditActorLabel, adminOrderStatusLabel, adminTaskStatusLabel, cancelAdminTask, decideRiskAppeal, decideRiskReview, disputeResolutionLabel, getRiskRuleDetail, getRiskRules, listAdminAudits, listDisputedOrders, listRiskAppealHistory, listRiskAppeals, listRiskReviews, listRiskRuleVersions, resetRiskRules, resolveDispute, riskAppealStatusLabel, riskReviewStatusLabel, riskVerdictLabel, runRiskRecheck, searchAdminTasks, searchAdminUsers, updateRiskRules, type AdminAuditItem, type AdminOrderItem, type AdminRiskAppealItem, type AdminRiskAppealRecord, type AdminRiskReviewItem, type AdminTaskItem, type AdminUserItem, type DisputeDecision, type RiskAppealDecision, type RiskReviewDecision, type RiskRuleCatalog, type RiskRuleCatalogDetail, type RiskRuleCatalogVersion } from '../api/admin'
import { listSettingAudits, listSettings, resetSetting, settingChoiceLabel, settingSourceLabel, testSetting, updateSetting, type AdminSetting, type SettingAudit, type SettingTestResult } from '../api/settings'
import { formatDeadline } from '../utils/format'

// —— 页面自身的会话：独立页面自己处理“未登录”与“登录了但不是运营”两种情况 ——
const operator = ref<CurrentUser | null>(null)
const authChecking = ref(true)
const loginEmail = ref('')
const loginPassword = ref('')
const loginBusy = ref(false)
const loginError = ref('')

const isOperator = computed(() => operator.value?.isAdmin === true)

async function restoreOperator() {
  authChecking.value = true

  if (!getAccessToken()) {
    operator.value = null
    authChecking.value = false
    return
  }

  try {
    operator.value = await getCurrentUser()
  } catch {
    // 令牌过期或失效：当作未登录处理，让运营重新登录一次（getCurrentUser 已经清掉了本地令牌）。
    operator.value = null
  } finally {
    authChecking.value = false
  }
}

async function signIn() {
  if (!loginEmail.value.trim() || !loginPassword.value) {
    loginError.value = '请填写邮箱与密码。'
    return
  }

  loginBusy.value = true
  loginError.value = ''
  try {
    // login 存下令牌，但响应里只有角色；是不是管理员要问 /auth/me（名单在部署配置里）。
    await login({ email: loginEmail.value.trim(), password: loginPassword.value })
    operator.value = await getCurrentUser()
    loginPassword.value = ''
    if (isOperator.value) await openConsole()
  } catch (error) {
    loginError.value = error instanceof Error ? error.message : '登录失败'
    clearAccessToken()
    operator.value = null
  } finally {
    loginBusy.value = false
  }
}

function signOut() {
  clearAccessToken()
  operator.value = null
  loginPassword.value = ''
  // 运营数据只属于当前会话：退出时清空，避免下一个登录者看到上一个人的内容。
  settingsItems.value = []
  settingsAudits.value = []
  settingsTests.value = {}
  settingsDrafts.value = {}
  adminTasks.value = []
  adminUsers.value = []
  adminOrders.value = []
  adminRiskReviews.value = []
  adminRiskAppeals.value = []
  adminRuleDetail.value = null
  adminRuleVersions.value = []
}

/** 进后台先把默认页签（配置项）读出来，其余页签按需加载，避免一进来就打一堆请求。 */
async function openConsole() {
  settingsTab.value = 'values'
  await Promise.all([loadSettings(), refreshRuleSummary()])
}

/**
 * 规则目录的版本号要显示在页签上，所以进后台时先把概述读一次（很小的一个请求）；
 * 读失败不影响其它页签——规则目录页自己会再读一次并给出可读的错误。
 */
async function refreshRuleSummary() {
  try {
    adminRiskRules.value = await getRiskRules()
  } catch {
    adminRiskRules.value = null
  }
}

onMounted(async () => {
  await restoreOperator()
  if (isOperator.value) await openConsole()
})

// 运营配置：入口只对管理员展示，真正的授权在服务端（/api/v1/admin/settings 需要管理员身份）。
const settingsTab = ref<'values' | 'audits' | 'tasks' | 'users' | 'disputes' | 'risk' | 'appeals' | 'rules'>('values')
const settingsLoading = ref(false)
const settingsError = ref('')
const settingsNotice = ref('')
const settingsItems = ref<AdminSetting[]>([])
const settingsDrafts = ref<Record<string, string>>({})
const settingsBusyKey = ref('')
const settingsTests = ref<Record<string, SettingTestResult>>({})
const settingsAudits = ref<SettingAudit[]>([])

// 运营后台的人工兜底：跨所有者检索、人工下架，以及处置风险复核队列。
const adminTaskKeyword = ref('')
const adminTaskStatus = ref('')
const adminTasks = ref<AdminTaskItem[]>([])
const adminTaskBusy = ref(false)
const adminTaskError = ref('')
const adminTaskNotice = ref('')
const adminCancelTaskId = ref('')
const adminCancelReason = ref('')
const adminUserKeyword = ref('')
const adminUsers = ref<AdminUserItem[]>([])
const adminAudits = ref<AdminAuditItem[]>([])
// 争议处置：默认只看待处置的争议订单，三选一处置并必须填写依据。
const adminOrderStatus = ref('')
const adminOrders = ref<AdminOrderItem[]>([])
const adminResolveOrderId = ref('')
const adminResolveDecision = ref<DisputeDecision>('Approve')
const adminResolveNote = ref('')
// 风险复核：只列“转人工”的任务（禁止类别不会进队列），运营放行或驳回都必须写明依据。
const adminRiskReviews = ref<AdminRiskReviewItem[]>([])
const adminRiskRules = ref<RiskRuleCatalog | null>(null)
const adminRiskDecisionId = ref('')
const adminRiskNote = ref('')
// 误拦申诉：被拦下的所有者提交的申诉排在这里；禁止类别即使申诉成立也不会放行。
const adminRiskAppeals = ref<AdminRiskAppealItem[]>([])
const adminAppealDecisionId = ref('')
const adminAppealNote = ref('')
// 申诉轨迹：任务上只留最新一次状态，展开时才按需读这条任务的完整留档。
const appealHistoryTaskId = ref('')
const appealHistory = ref<AdminRiskAppealRecord[]>([])
const appealHistoryBusy = ref(false)
// 规则目录：查看当前生效的规则（连匹配词）+ 版本历史，并整份替换出一版新目录。
// 编辑态单独放一份草稿（匹配词在界面上是一行一个的文本框），点“保存为新版本”才提交。
const adminRuleDetail = ref<RiskRuleCatalogDetail | null>(null)
const adminRuleVersions = ref<RiskRuleCatalogVersion[]>([])
const adminRuleDrafts = ref<RuleDraft[]>([])
const adminRuleThreshold = ref(5000)
const adminRuleNightStart = ref('00:00')
const adminRuleNightEnd = ref('06:00')
const adminRuleReason = ref('')
const adminRuleEditing = ref(false)
const adminRuleResetting = ref(false)
const adminRuleResetNote = ref('')
const adminRuleBusy = ref(false)
const adminRuleError = ref('')
const adminRuleNotice = ref('')

interface RuleDraft {
  code: string
  category: string
  verdict: string
  description: string
  keywordsText: string
}

const settingsGroups = computed(() => {
  const categories: string[] = []
  for (const item of settingsItems.value) if (!categories.includes(item.category)) categories.push(item.category)
  return categories.map((category) => ({ category, items: settingsItems.value.filter((item) => item.category === category) }))
})

async function loadSettings() {
  settingsLoading.value = true
  settingsError.value = ''
  try {
    const items = await listSettings()
    settingsItems.value = items
    const drafts: Record<string, string> = {}
    for (const item of items) {
      // 机密项只下发掩码，输入框留空表示“不修改”，避免把 ****1234 当成新值写回去。
      drafts[item.key] = item.isSecret ? '' : item.value
    }
    settingsDrafts.value = drafts
  } catch (error) {
    settingsError.value = error instanceof Error ? error.message : '读取运营配置失败'
  } finally {
    settingsLoading.value = false
  }
}

async function loadSettingAudits() {
  settingsTab.value = 'audits'
  settingsLoading.value = true
  settingsError.value = ''
  try {
    if (settingsItems.value.length === 0) await loadSettings()
    settingsAudits.value = await listSettingAudits(20)
  } catch (error) {
    settingsError.value = error instanceof Error ? error.message : '读取变更记录失败'
  } finally {
    settingsLoading.value = false
  }
}

function settingDraft(item: AdminSetting): string {
  return settingsDrafts.value[item.key] ?? ''
}

function setSettingDraft(item: AdminSetting, value: string) {
  settingsDrafts.value = { ...settingsDrafts.value, [item.key]: value }
}

function settingTest(item: AdminSetting): SettingTestResult | null {
  return settingsTests.value[item.key] ?? null
}

function settingPlaceholder(item: AdminSetting): string {
  if (item.isSecret) return item.value ? `已配置 ${item.value}，留空表示不修改` : '尚未配置'
  return item.kind === 'Url' ? '留空表示停用' : '留空表示置空'
}

function settingLabel(key: string): string {
  return settingsItems.value.find((item) => item.key === key)?.displayName ?? key
}

async function saveSetting(item: AdminSetting, mode: 'draft' | 'clear' = 'draft') {
  const draft = mode === 'clear' ? '' : settingDraft(item)
  if (mode === 'draft' && item.isSecret && draft.trim().length === 0) {
    settingsError.value = `${item.displayName} 是机密项：接口只返回掩码，不会回写；请输入新值，或用“清空”显式置空。`
    return
  }

  settingsBusyKey.value = item.key
  settingsError.value = ''
  settingsNotice.value = ''
  try {
    const updated = await updateSetting(item.key, draft, item.overrideVersion)
    settingsNotice.value = `${item.displayName} 已保存（来源：${settingSourceLabel(updated.source)}），立即生效。`
    await loadSettings()
  } catch (error) {
    settingsError.value = error instanceof Error ? error.message : '保存失败'
  } finally {
    settingsBusyKey.value = ''
  }
}

async function resetSettingValue(item: AdminSetting) {
  settingsBusyKey.value = item.key
  settingsError.value = ''
  settingsNotice.value = ''
  try {
    const updated = await resetSetting(item.key)
    settingsNotice.value = `${item.displayName} 已恢复默认（来源：${settingSourceLabel(updated.source)}）。`
    await loadSettings()
  } catch (error) {
    settingsError.value = error instanceof Error ? error.message : '恢复默认失败'
  } finally {
    settingsBusyKey.value = ''
  }
}

async function runSettingTest(item: AdminSetting) {
  settingsBusyKey.value = item.key
  settingsError.value = ''
  try {
    const result = await testSetting(item.key)
    settingsTests.value = { ...settingsTests.value, [item.key]: result }
  } catch (error) {
    settingsError.value = error instanceof Error ? error.message : '自检失败'
  } finally {
    settingsBusyKey.value = ''
  }
}

async function loadAdminTasks() {
  adminTaskBusy.value = true
  adminTaskError.value = ''
  adminTaskNotice.value = ''
  try {
    const result = await searchAdminTasks(adminTaskKeyword.value, adminTaskStatus.value)
    adminTasks.value = result.items
    if (result.items.length === 0) adminTaskNotice.value = '没有匹配的任务。'
  } catch (error) {
    adminTaskError.value = error instanceof Error ? error.message : '检索任务失败'
  } finally {
    adminTaskBusy.value = false
  }
}

function startAdminCancel(task: AdminTaskItem) {
  adminCancelTaskId.value = task.id
  adminCancelReason.value = ''
  adminTaskError.value = ''
  adminTaskNotice.value = ''
}

async function confirmAdminCancel(task: AdminTaskItem) {
  if (!adminCancelReason.value.trim()) {
    adminTaskError.value = '下架任务必须填写原因，原因会记进运营审计。'
    return
  }

  adminTaskBusy.value = true
  adminTaskError.value = ''
  try {
    const updated = await cancelAdminTask(task.id, adminCancelReason.value.trim())
    adminTaskNotice.value = `任务「${updated.title}」已下架，原因已写入运营审计。`
    adminCancelTaskId.value = ''
    await loadAdminTasks()
  } catch (error) {
    adminTaskError.value = error instanceof Error ? error.message : '下架失败'
  } finally {
    adminTaskBusy.value = false
  }
}

/** 争议处置列表：默认只拉待处置的争议；传 all 看全部历史。 */
async function loadAdminOrders() {
  adminTaskBusy.value = true
  adminTaskError.value = ''
  adminTaskNotice.value = ''
  adminResolveOrderId.value = ''
  try {
    const result = await listDisputedOrders(adminOrderStatus.value)
    adminOrders.value = result.items
    if (result.items.length === 0) adminTaskNotice.value = adminOrderStatus.value ? '没有匹配的订单。' : '当前没有待处置的争议。'
  } catch (error) {
    adminTaskError.value = error instanceof Error ? error.message : '读取争议订单失败'
  } finally {
    adminTaskBusy.value = false
  }
}

function startAdminResolve(order: AdminOrderItem) {
  adminResolveOrderId.value = order.id
  adminResolveDecision.value = 'Approve'
  adminResolveNote.value = ''
  adminTaskError.value = ''
  adminTaskNotice.value = ''
}

async function confirmAdminResolve(order: AdminOrderItem) {
  if (!adminResolveNote.value.trim()) {
    adminTaskError.value = '处置争议必须写明依据，依据会写进运营审计。'
    return
  }

  adminTaskBusy.value = true
  adminTaskError.value = ''
  try {
    const resolved = await resolveDispute(order.id, adminResolveDecision.value, adminResolveNote.value.trim())
    adminTaskNotice.value = `订单「${resolved.title}」的争议已处置：${disputeResolutionLabel(resolved.disputeResolution ?? adminResolveDecision.value)}，双方已收到通知。`
    adminResolveOrderId.value = ''
    await loadAdminOrders()
  } catch (error) {
    adminTaskError.value = error instanceof Error ? error.message : '处置争议失败'
  } finally {
    adminTaskBusy.value = false
  }
}

async function loadAdminUsers() {
  adminTaskBusy.value = true
  adminTaskError.value = ''
  try {
    adminUsers.value = (await searchAdminUsers(adminUserKeyword.value)).items
  } catch (error) {
    adminTaskError.value = error instanceof Error ? error.message : '检索用户失败'
  } finally {
    adminTaskBusy.value = false
  }
}

async function loadAdminAudits() {
  settingsTab.value = 'audits'
  settingsLoading.value = true
  settingsError.value = ''
  try {
    adminAudits.value = await listAdminAudits(20)
    settingsAudits.value = await listSettingAudits(20)
  } catch (error) {
    settingsError.value = error instanceof Error ? error.message : '读取审计失败'
  } finally {
    settingsLoading.value = false
  }
}

/** 风险复核队列：规则判成“需要人工复核”的任务排在这里，禁止类别不会出现。 */
async function loadAdminRiskReviews() {
  settingsTab.value = 'risk'
  adminTaskBusy.value = true
  adminTaskError.value = ''
  try {
    const [queue, catalog] = await Promise.all([listRiskReviews(20), getRiskRules()])
    adminRiskReviews.value = queue.items
    adminRiskRules.value = catalog
  } catch (error) {
    adminTaskError.value = error instanceof Error ? error.message : '读取风险复核队列失败'
  } finally {
    adminTaskBusy.value = false
  }
}

/**
 * 手动跑一轮发布后复检：规则目录改完之后，仍然在线的任务要按最新规则重新判一遍。
 * 后台每五分钟也会自动跑一轮；这里给"改完想立刻看结果"的运营用，跑完刷新队列与审计。
 */
async function recheckPublishedTasks() {
  adminTaskBusy.value = true
  adminTaskError.value = ''
  adminTaskNotice.value = ''
  try {
    const result = await runRiskRecheck()
    adminTaskNotice.value = `复检完成（规则第 ${result.ruleVersion} 版）：扫描 ${result.scanned} 条，自动下架 ${result.unpublished} 条、冻结订单 ${result.frozen} 条、要求人工复检 ${result.flagged} 条${result.skipped > 0 ? `，另有 ${result.skipped} 条留给下一轮` : ''}。`
    const [queue, catalog] = await Promise.all([listRiskReviews(20), getRiskRules()])
    adminRiskReviews.value = queue.items
    adminRiskRules.value = catalog
  } catch (error) {
    adminTaskError.value = error instanceof Error ? error.message : '发布后复检失败'
  } finally {
    adminTaskBusy.value = false
  }
}

function startAdminRiskDecision(item: AdminRiskReviewItem) {  adminRiskDecisionId.value = item.taskId
  adminRiskNote.value = ''
  adminTaskError.value = ''
  adminTaskNotice.value = ''
}

async function confirmAdminRiskDecision(item: AdminRiskReviewItem, decision: RiskReviewDecision) {
  if (!adminRiskNote.value.trim()) {
    adminTaskError.value = '处置风险复核必须写明依据，依据会写进运营审计。'
    return
  }

  adminTaskBusy.value = true
  adminTaskError.value = ''
  try {
    const reviewed = await decideRiskReview(item.taskId, decision, adminRiskNote.value.trim())
    adminTaskNotice.value = decision === 'Approve'
      ? `任务「${reviewed.title}」已放行，需求方现在可以发布。`
      : `任务「${reviewed.title}」已驳回，需求方不能发布该任务。`
    adminRiskDecisionId.value = ''
    await loadAdminRiskReviews()
  } catch (error) {
    adminTaskError.value = error instanceof Error ? error.message : '处置风险复核失败'
  } finally {
    adminTaskBusy.value = false
  }
}

/** 误拦申诉队列（运营侧）：被拦下的所有者提交的申诉排在这里。 */
async function loadAdminRiskAppeals() {
  settingsTab.value = 'appeals'
  adminTaskBusy.value = true
  adminTaskError.value = ''
  try {
    adminRiskAppeals.value = (await listRiskAppeals(20)).items
  } catch (error) {
    adminTaskError.value = error instanceof Error ? error.message : '读取申诉队列失败'
  } finally {
    adminTaskBusy.value = false
  }
}

function startAdminAppealDecision(item: AdminRiskAppealItem) {
  adminAppealDecisionId.value = item.taskId
  adminAppealNote.value = ''
  adminTaskError.value = ''
  adminTaskNotice.value = ''
}

async function confirmAdminAppealDecision(item: AdminRiskAppealItem, decision: RiskAppealDecision) {
  if (!adminAppealNote.value.trim()) {
    adminTaskError.value = '处置申诉必须写明依据，依据会写进运营审计。'
    return
  }

  adminTaskBusy.value = true
  adminTaskError.value = ''
  try {
    const decided = await decideRiskAppeal(item.taskId, decision, adminAppealNote.value.trim())
    adminTaskNotice.value = decided.canBeReleasedByAppeal
      ? `任务「${decided.title}」的申诉按“误判”处理，需求方现在可以发布。`
      : `任务「${decided.title}」的申诉已记为误伤，但它命中的是禁止类别，仍然不能发布。`
    adminAppealDecisionId.value = ''
    await loadAdminRiskAppeals()
  } catch (error) {
    adminTaskError.value = error instanceof Error ? error.message : '处置申诉失败'
  } finally {
    adminTaskBusy.value = false
  }
}

/**
 * 展开/收起某条任务的申诉轨迹：任务上只留最新一次申诉状态，改过文案之后上一轮的理由与结论就没地方看了，
 * 所以轨迹按需从留档接口读（含每次提交时的规则版本与运营结论）。
 */
async function toggleAppealHistory(item: AdminRiskAppealItem) {
  if (appealHistoryTaskId.value === item.taskId) {
    appealHistoryTaskId.value = ''
    return
  }

  appealHistoryBusy.value = true
  adminTaskError.value = ''
  adminTaskNotice.value = ''
  try {
    const history = await listRiskAppealHistory(item.taskId)
    appealHistory.value = history.items
    appealHistoryTaskId.value = item.taskId
    if (history.items.length === 0) adminTaskNotice.value = '这条任务还没有申诉留档。'
  } catch (error) {
    adminTaskError.value = error instanceof Error ? error.message : '读取申诉轨迹失败'
  } finally {
    appealHistoryBusy.value = false
  }
}

// 规则目录：读取当前生效的一版 + 版本历史，编辑后整份提交（服务端把版本号 +1 并写运营审计）。
async function loadRiskRuleCatalog() {
  settingsTab.value = 'rules'
  adminRuleBusy.value = true
  adminRuleError.value = ''
  adminRuleNotice.value = ''
  try {
    const [detail, versions] = await Promise.all([getRiskRuleDetail(), listRiskRuleVersions(20)])
    adminRuleDetail.value = detail
    adminRuleVersions.value = versions.items
    resetRuleDrafts()
  } catch (error) {
    adminRuleError.value = error instanceof Error ? error.message : '读取风险规则目录失败'
  } finally {
    adminRuleBusy.value = false
  }
}

/** 把服务端下发的目录抄进编辑草稿：匹配词在界面上是一行一个的文本框。 */
function resetRuleDrafts() {
  const detail = adminRuleDetail.value
  if (!detail) return
  adminRuleThreshold.value = detail.highRewardThreshold
  adminRuleNightStart.value = detail.nightWindowStart
  adminRuleNightEnd.value = detail.nightWindowEnd
  adminRuleDrafts.value = detail.rules.map((rule) => ({
    code: rule.code,
    category: rule.category,
    verdict: rule.verdict === 'Blocked' ? 'Blocked' : 'NeedsReview',
    description: rule.description,
    keywordsText: rule.keywords.join('\n'),
  }))
  adminRuleReason.value = ''
  adminRuleEditing.value = false
}

function addRuleDraft() {
  adminRuleDrafts.value = [...adminRuleDrafts.value, { code: '', category: '', verdict: 'Blocked', description: '', keywordsText: '' }]
}

function removeRuleDraft(index: number) {
  adminRuleDrafts.value = adminRuleDrafts.value.filter((_, position) => position !== index)
}

/** 匹配词允许用换行、逗号或空格分隔；去掉空白与重复项后再提交。 */
function parseRuleKeywords(text: string): string[] {
  const seen: string[] = []
  for (const item of text.split(/[\s,，、;；]+/)) {
    const keyword = item.trim()
    if (keyword && !seen.includes(keyword)) seen.push(keyword)
  }
  return seen
}

async function saveRiskRules() {
  const detail = adminRuleDetail.value
  if (!detail) return
  adminRuleBusy.value = true
  adminRuleError.value = ''
  adminRuleNotice.value = ''
  try {
    const updated = await updateRiskRules({
      expectedVersion: detail.version,
      reason: adminRuleReason.value.trim(),
      highRewardThreshold: Number(adminRuleThreshold.value),
      nightWindowStart: adminRuleNightStart.value.trim(),
      nightWindowEnd: adminRuleNightEnd.value.trim(),
      rules: adminRuleDrafts.value.map((rule) => ({
        code: rule.code.trim(),
        category: rule.category.trim(),
        verdict: rule.verdict,
        description: rule.description.trim(),
        keywords: parseRuleKeywords(rule.keywordsText),
      })),
    })

    adminRuleDetail.value = updated
    adminRiskRules.value = await getRiskRules()
    adminRuleVersions.value = (await listRiskRuleVersions(20)).items
    // 保存成功才退出编辑态；失败时保留草稿，运营可以直接改完再交。
    resetRuleDrafts()
    adminRuleNotice.value = `已保存为第 ${updated.version} 版：${updated.changeSummary ?? '目录已更新'}。后续写入按这一版判定，历史结论不会被改写。`
  } catch (error) {
    // 409（别人刚改过）与 422（内容不合法）都带可读说明，直接展示给运营。
    adminRuleError.value = error instanceof Error ? error.message : '保存风险规则失败'
  } finally {
    adminRuleBusy.value = false
  }
}

/** 恢复到代码内置目录：改坏了要有退路；同样是追加一版，历史里的旧版本仍然看得到。 */
async function confirmResetRiskRules() {
  const detail = adminRuleDetail.value
  if (!detail) return
  adminRuleBusy.value = true
  adminRuleError.value = ''
  adminRuleNotice.value = ''
  try {
    const restored = await resetRiskRules(detail.version, adminRuleResetNote.value.trim())
    adminRuleDetail.value = restored
    adminRiskRules.value = await getRiskRules()
    adminRuleVersions.value = (await listRiskRuleVersions(20)).items
    resetRuleDrafts()
    adminRuleResetting.value = false
    adminRuleResetNote.value = ''
    adminRuleNotice.value = `已恢复内置目录（第 ${restored.version} 版）：${restored.changeSummary ?? ''}`
  } catch (error) {
    adminRuleError.value = error instanceof Error ? error.message : '恢复内置目录失败'
  } finally {
    adminRuleBusy.value = false
  }
}

</script>

<template>
  <div class="ops-page">
    <header class="ops-header">
      <div>
        <p class="eyebrow">OPERATIONS CONSOLE / AITOHUMAN</p>
        <h1>运营后台</h1>
      </div>
      <div class="ops-header-actions">
        <a class="settings-secondary" :href="APP_HOME_URL">← 返回任务工作台</a>
        <template v-if="operator">
          <span class="ops-operator">{{ operator.displayName }} · {{ operator.email }}</span>
          <button type="button" class="settings-secondary" @click="signOut()">退出登录</button>
        </template>
      </div>
    </header>

    <p v-if="authChecking" class="settings-empty">正在确认登录状态…</p>

    <section v-else-if="!operator" class="auth-dialog ops-login">
      <h2>用运营账号登录</h2>
      <p class="settings-hint">运营后台的权限由服务端判定：登录后如果这个账号不在管理员名单里，下面的每个接口都会返回 403。</p>
      <p v-if="loginError" class="auth-error">{{ loginError }}</p>
      <label>邮箱<input v-model.trim="loginEmail" type="email" autocomplete="username" placeholder="运营账号邮箱" /></label>
      <label>密码<input v-model="loginPassword" type="password" autocomplete="current-password" placeholder="密码" @keyup.enter="signIn()" /></label>
      <button type="button" class="settings-primary" :disabled="loginBusy" @click="signIn()">{{ loginBusy ? '登录中…' : '登录' }}</button>
    </section>

    <section v-else-if="!isOperator" class="auth-dialog ops-login">
      <h2>这个账号没有运营权限</h2>
      <p class="settings-hint">已经以 {{ operator.email }} 登录，但它不在部署配置的管理员名单里（<code>Admin__UserIds</code> / <code>Admin__Emails</code>）。换一个账号登录，或让部署方把这个账号加进名单。</p>
      <button type="button" class="settings-secondary" @click="signOut()">换个账号登录</button>
    </section>

    <section v-else class="auth-dialog settings-dialog ops-console">
      <div class="auth-dialog-head"><div><p class="eyebrow">OPERATIONS CONSOLE / 06</p><h2>运营管理</h2></div></div>
        <p class="settings-hint">生效顺序：后台覆盖 → 部署配置 → 代码默认值。机密只以掩码展示、审计也只留掩码；保存后立即生效，不需要重启服务。</p>
        <div class="settings-tabs">
          <button type="button" :class="{ selected: settingsTab === 'values' }" @click="settingsTab = 'values'">配置项 <b>{{ settingsItems.length }}</b></button>
          <button type="button" :class="{ selected: settingsTab === 'audits' }" @click="loadAdminAudits()">变更记录</button>
          <button type="button" :class="{ selected: settingsTab === 'tasks' }" @click="settingsTab = 'tasks'; loadAdminTasks()">任务检索</button>
          <button type="button" :class="{ selected: settingsTab === 'users' }" @click="settingsTab = 'users'; loadAdminUsers()">用户检索</button>
          <button type="button" :class="{ selected: settingsTab === 'disputes' }" @click="settingsTab = 'disputes'; loadAdminOrders()">争议处置 <b>{{ adminOrders.length }}</b></button>
          <button type="button" :class="{ selected: settingsTab === 'risk' }" @click="loadAdminRiskReviews()">风险复核 <b>{{ adminRiskReviews.length }}</b></button>
          <button type="button" :class="{ selected: settingsTab === 'appeals' }" @click="loadAdminRiskAppeals()">误拦申诉 <b>{{ adminRiskAppeals.length }}</b></button>
          <button type="button" :class="{ selected: settingsTab === 'rules' }" @click="loadRiskRuleCatalog()">规则目录 <b>v{{ adminRuleDetail?.version ?? adminRiskRules?.version ?? 1 }}</b></button>
          <button type="button" class="settings-refresh" :disabled="settingsLoading || adminTaskBusy" @click="settingsTab === 'audits' ? loadAdminAudits() : (settingsTab === 'tasks' ? loadAdminTasks() : (settingsTab === 'users' ? loadAdminUsers() : (settingsTab === 'disputes' ? loadAdminOrders() : (settingsTab === 'risk' ? loadAdminRiskReviews() : (settingsTab === 'appeals' ? loadAdminRiskAppeals() : (settingsTab === 'rules' ? loadRiskRuleCatalog() : loadSettings()))))))">{{ settingsLoading || adminTaskBusy ? '读取中…' : '刷新 ↻' }}</button>
        </div>
        <p v-if="settingsError" class="auth-error">{{ settingsError }}</p>
        <p v-if="settingsNotice" class="settings-notice">{{ settingsNotice }}</p>

        <div v-if="settingsTab === 'tasks'" class="settings-body">
          <p class="settings-hint">跨所有者检索任务，并对尚未分配的任务做人工下架（原因会写进运营审计）。风险拦截由确定性规则在创建草稿与发布时自动完成，这里只是人工兜底。</p>
          <div class="setting-control">
            <input v-model.trim="adminTaskKeyword" type="text" placeholder="按标题 / 描述 / 区域搜索" @keyup.enter="loadAdminTasks()" />
            <select v-model="adminTaskStatus">
              <option value="">全部状态</option>
              <option value="ReadyToPublish">草稿</option>
              <option value="Published">大厅中</option>
              <option value="Assigned">已分配</option>
              <option value="Closed">已结束</option>
              <option value="Expired">已过期</option>
              <option value="Cancelled">已下架</option>
            </select>
            <button type="button" class="settings-primary" :disabled="adminTaskBusy" @click="loadAdminTasks()">检索</button>
          </div>
          <p v-if="adminTaskError" class="auth-error">{{ adminTaskError }}</p>
          <p v-if="adminTaskNotice" class="settings-notice">{{ adminTaskNotice }}</p>
          <span v-if="adminTasks.length === 0" class="settings-empty">还没有检索结果。</span>
          <div v-for="task in adminTasks" v-else :key="task.id" class="admin-row">
            <div>
              <strong>{{ task.title }}</strong>
              <small>{{ adminTaskStatusLabel(task.status) }} · {{ task.district }} · ¥{{ task.rewardAmount }} · 报名 {{ task.applicationCount }}<template v-if="task.orderStatus"> · 订单 {{ task.orderStatus }}</template> · {{ task.ownerDisplayName ?? '未知需求方' }}{{ task.ownerEmail ? `（${task.ownerEmail}）` : '' }}</small>
              <small class="evidence-note">任务号 {{ task.id.slice(0, 8) }}</small>
            </div>
            <div class="setting-actions">
              <button v-if="adminCancelTaskId !== task.id" type="button" class="settings-secondary danger" :disabled="adminTaskBusy || task.status === 'Cancelled'" @click="startAdminCancel(task)">下架</button>
              <template v-else>
                <input v-model.trim="adminCancelReason" type="text" placeholder="填写下架原因（必填）" />
                <button type="button" class="settings-secondary danger" :disabled="adminTaskBusy" @click="confirmAdminCancel(task)">确认下架</button>
                <button type="button" class="settings-secondary" :disabled="adminTaskBusy" @click="adminCancelTaskId = ''">取消</button>
              </template>
            </div>
          </div>
        </div>

        <div v-else-if="settingsTab === 'disputes'" class="settings-body">
          <p class="settings-hint">争议由参与者发起、运营处置：需求方在服务者提交验收后、服务者在验收被驳回后可以申请平台介入。处置结果只有三种（强制完成 / 退回返工 / 终止订单），依据会写进运营审计并通知双方。</p>
          <div class="setting-control">
            <select v-model="adminOrderStatus" @change="loadAdminOrders()">
              <option value="">待处置争议</option>
              <option value="Disputed">争议中</option>
              <option value="Submitted">待验收</option>
              <option value="Rejected">需补充执行</option>
              <option value="all">全部订单</option>
            </select>
            <button type="button" class="settings-primary" :disabled="adminTaskBusy" @click="loadAdminOrders()">检索</button>
          </div>
          <p v-if="adminTaskError" class="auth-error">{{ adminTaskError }}</p>
          <p v-if="adminTaskNotice" class="settings-notice">{{ adminTaskNotice }}</p>
          <span v-if="adminOrders.length === 0" class="settings-empty">当前没有可处置的订单。</span>
          <div v-for="order in adminOrders" v-else :key="order.id" class="admin-row">
            <div>
              <strong>{{ order.title }}</strong>
              <small>{{ adminOrderStatusLabel(order.status) }} · ¥{{ order.rewardAmount }} {{ order.rewardCurrency }} · 订单 {{ order.id.slice(0, 8) }} · 返工 {{ order.reworkCount }} 次</small>
              <small>需求方 {{ order.ownerEmail ?? order.ownerId.slice(0, 8) }} · 服务者 {{ order.workerEmail ?? order.workerId.slice(0, 8) }} · 创建于 {{ formatDeadline(order.createdAt) }}</small>
              <small v-if="order.evidenceNote" class="evidence-note">提交说明：{{ order.evidenceNote }}</small>
              <small v-if="order.rejectionNote" class="evidence-note">驳回原因：{{ order.rejectionNote }}</small>
              <small v-if="order.disputeReason" class="evidence-note">争议原因：{{ order.disputeReason }}<template v-if="order.disputeOpenedAt"> · {{ formatDeadline(order.disputeOpenedAt) }} 发起</template></small>
              <small v-if="order.disputeResolution" class="evidence-note">处置结果：{{ disputeResolutionLabel(order.disputeResolution) }}<template v-if="order.disputeResolutionNote"> · {{ order.disputeResolutionNote }}</template></small>
            </div>
            <div class="setting-actions">
              <template v-if="adminResolveOrderId !== order.id">
                <button type="button" class="settings-secondary" :disabled="adminTaskBusy" @click="startAdminResolve(order)">处置争议</button>
              </template>
              <template v-else>
                <select :value="adminResolveDecision" @change="adminResolveDecision = ($event.target as HTMLSelectElement).value as DisputeDecision">
                  <option value="Approve">强制完成</option>
                  <option value="Rework">退回返工</option>
                  <option value="Cancel">终止订单</option>
                </select>
                <input v-model.trim="adminResolveNote" type="text" maxlength="500" placeholder="填写处置依据（必填，最多 500 字）" />
                <button type="button" class="settings-secondary danger" :disabled="adminTaskBusy" @click="confirmAdminResolve(order)">确认处置</button>
                <button type="button" class="settings-secondary" :disabled="adminTaskBusy" @click="adminResolveOrderId = ''">取消</button>
              </template>
            </div>
          </div>
        </div>

        <div v-else-if="settingsTab === 'risk'" class="settings-body">
          <p class="settings-hint">风险拦截是确定性规则：创建草稿时就判定，发布前再判一次。禁止类别（例如代考、违禁品、跟踪偷拍）一律不能发布，人工也无权放行；命中“需人工复核”的任务排在这里，运营放行后需求方才能发布。依据会写进运营审计并通知任务所有者。</p>
          <p class="settings-hint"><b>发布后复检</b>：规则目录改过之后，已经在线的任务会按最新一版规则重新判一次——命中禁止类别的当场下架（有订单的改为冻结订单，进争议队列），只命中“需人工复核”的任务保持在线并回到这个队列。后台每五分钟自动跑一轮，也可以在这里立刻跑一轮。</p>
          <div class="setting-control">
            <button type="button" class="settings-secondary" :disabled="adminTaskBusy" @click="recheckPublishedTasks()">{{ adminTaskBusy ? '复检中…' : '立即复检在线任务' }}</button>
          </div>
          <p v-if="adminRiskRules" class="settings-hint">当前规则目录第 <b>{{ adminRiskRules.version }}</b> 版，共 {{ adminRiskRules.rules.length }} 条，其中禁止 {{ adminRiskRules.rules.filter(rule => rule.verdict === 'Blocked').length }} 条；悬赏超过 ¥{{ adminRiskRules.highRewardThreshold }} 转人工。只展示规则类别，不展示匹配词。</p>
          <p v-if="adminTaskError" class="auth-error">{{ adminTaskError }}</p>
          <p v-if="adminTaskNotice" class="settings-notice">{{ adminTaskNotice }}</p>
          <span v-if="adminRiskReviews.length === 0" class="settings-empty">当前没有待复核的任务。</span>
          <div v-for="item in adminRiskReviews" v-else :key="item.taskId" class="admin-row">
            <div>
              <strong>{{ item.title }}</strong>
              <small>{{ riskVerdictLabel(item.verdict) }} · {{ item.ruleCode }} · {{ item.category }} · 规则第 {{ item.ruleVersion }} 版 · 判定于 {{ item.assessedAt ? formatDeadline(item.assessedAt) : '—' }}</small>
              <small>悬赏 ¥{{ item.rewardAmount }} {{ item.rewardCurrency }} · 截止 {{ formatDeadline(item.deadline) }} · {{ item.district }} · 任务{{ adminTaskStatusLabel(item.taskStatus) }}</small>
              <small>需求方 {{ item.ownerDisplayName ?? '未知需求方' }}{{ item.ownerEmail ? `（${item.ownerEmail}）` : '' }} · 任务号 {{ item.taskId.slice(0, 8) }}</small>
              <small class="evidence-note">{{ item.description }}</small>
              <small class="evidence-note">命中原因：{{ item.summary }}</small>
              <small v-if="item.enforcementStatus === 'RecheckRequired'" class="evidence-note">规则升级后的复检要求：{{ item.enforcementReason }}（任务仍在线上）</small>
              <small v-else-if="item.enforcementStatus === 'Suspended'" class="evidence-note">平台已按风控处置：{{ item.enforcementReason }}</small>
            </div>
            <div class="setting-actions">
              <template v-if="adminRiskDecisionId !== item.taskId">
                <button type="button" class="settings-secondary" :disabled="adminTaskBusy" @click="startAdminRiskDecision(item)">处置</button>
              </template>
              <template v-else>
                <input v-model.trim="adminRiskNote" type="text" maxlength="200" placeholder="填写复核依据（必填，最多 200 字）" />
                <button type="button" class="settings-secondary" :disabled="adminTaskBusy" @click="confirmAdminRiskDecision(item, 'Approve')">放行</button>
                <button type="button" class="settings-secondary danger" :disabled="adminTaskBusy" @click="confirmAdminRiskDecision(item, 'Reject')">驳回</button>
                <button type="button" class="settings-secondary" :disabled="adminTaskBusy" @click="adminRiskDecisionId = ''">取消</button>
              </template>
            </div>
          </div>
        </div>

        <div v-else-if="settingsTab === 'rules'" class="settings-body">
          <p class="settings-hint">规则目录是确定性风险判定的依据：命中“禁止发布”的类别一律拦死（人工也无权放行），命中“需人工复核”的进复核队列。每次保存都会生成新的一版（版本号 +1），旧版本永不改写；任务上记的是判定当时的版本号，所以每条拦截结论都能回溯到当时用的是哪一版规则。</p>
          <p v-if="adminRuleDetail" class="settings-hint">
            当前第 <b>{{ adminRuleDetail.version }}</b> 版<template v-if="adminRuleDetail.isBuiltIn">（代码内置目录，还没有运营覆盖）</template>：共 {{ adminRuleDetail.rules.length }} 条规则，其中禁止 {{ adminRuleDetail.rules.filter((rule) => rule.verdict === 'Blocked').length }} 条；悬赏超过 ¥{{ adminRuleDetail.highRewardThreshold }} 转人工；深夜时段 {{ adminRuleDetail.nightWindowStart }}–{{ adminRuleDetail.nightWindowEnd }}（北京时间）。
          </p>
          <p v-if="adminRuleDetail?.updatedAt" class="settings-hint">
            最近一次修改：{{ adminRuleDetail.updatedByName ?? adminRuleDetail.updatedBy?.slice(0, 8) }} · {{ formatDeadline(adminRuleDetail.updatedAt) }} · 依据“{{ adminRuleDetail.changeReason }}” · {{ adminRuleDetail.changeSummary }}
          </p>
          <p v-if="adminRuleError" class="auth-error">{{ adminRuleError }}</p>
          <p v-if="adminRuleNotice" class="settings-notice">{{ adminRuleNotice }}</p>

          <div class="setting-control">
            <button v-if="!adminRuleEditing" type="button" class="settings-secondary" :disabled="adminRuleBusy" @click="adminRuleEditing = true">编辑规则目录</button>
            <template v-else>
              <button type="button" class="settings-secondary" :disabled="adminRuleBusy" @click="resetRuleDrafts()">放弃编辑</button>
              <button type="button" class="settings-primary" :disabled="adminRuleBusy" @click="saveRiskRules()">{{ adminRuleBusy ? '保存中…' : '保存为新版本' }}</button>
            </template>
            <template v-if="!adminRuleResetting">
              <button type="button" class="settings-secondary" :disabled="adminRuleBusy || adminRuleDetail?.isBuiltIn" @click="adminRuleResetting = true; adminRuleResetNote = ''">恢复内置目录</button>
            </template>
            <template v-else>
              <input v-model.trim="adminRuleResetNote" type="text" maxlength="200" placeholder="恢复依据（必填，最多 200 字，会写进运营审计）" />
              <button type="button" class="settings-secondary danger" :disabled="adminRuleBusy" @click="confirmResetRiskRules()">确认恢复</button>
              <button type="button" class="settings-secondary" :disabled="adminRuleBusy" @click="adminRuleResetting = false">取消</button>
            </template>
          </div>

          <template v-if="adminRuleEditing">
            <p class="settings-hint">匹配词一行一个，至少 2 个字（单字会误伤“代取”“代送”这类正常任务）；目录里至少要保留一条禁止类规则。改动内容会先过服务端校验，不合法会直接告诉你哪里不行。</p>
            <div class="setting-control">
              <input v-model.number="adminRuleThreshold" type="number" min="1" step="100" placeholder="高金额阈值（元）" />
              <input v-model.trim="adminRuleNightStart" type="text" maxlength="5" placeholder="深夜起点 HH:mm" />
              <input v-model.trim="adminRuleNightEnd" type="text" maxlength="5" placeholder="深夜终点 HH:mm" />
            </div>
            <div v-for="(rule, index) in adminRuleDrafts" :key="index" class="admin-row rule-draft">
              <div>
                <div class="setting-control">
                  <input v-model.trim="rule.code" type="text" maxlength="80" placeholder="原因代码，例如 prohibited.no_drones" />
                  <input v-model.trim="rule.category" type="text" maxlength="60" placeholder="类别名" />
                  <select v-model="rule.verdict">
                    <option value="Blocked">禁止发布</option>
                    <option value="NeedsReview">需人工复核</option>
                  </select>
                </div>
                <input v-model.trim="rule.description" type="text" maxlength="300" placeholder="给用户看的说明（不要写出具体匹配词）" />
                <textarea v-model="rule.keywordsText" rows="3" placeholder="匹配词，一行一个"></textarea>
              </div>
              <div class="setting-actions">
                <button type="button" class="settings-secondary danger" :disabled="adminRuleBusy" @click="removeRuleDraft(index)">删除这条</button>
              </div>
            </div>
            <div class="setting-control">
              <button type="button" class="settings-secondary" :disabled="adminRuleBusy" @click="addRuleDraft()">新增一条规则</button>
              <input v-model.trim="adminRuleReason" type="text" maxlength="200" placeholder="变更依据（必填，最多 200 字，会写进运营审计）" />
            </div>
          </template>

          <template v-else>
            <div v-for="rule in adminRuleDetail?.rules ?? []" :key="rule.code" class="admin-row">
              <div>
                <strong>{{ rule.category }} · {{ riskVerdictLabel(rule.verdict) }}</strong>
                <small class="evidence-note">{{ rule.code }} · 匹配词 {{ rule.keywords.length }} 个：{{ rule.keywords.join('、') }}</small>
                <small>{{ rule.description }}</small>
              </div>
            </div>
          </template>

          <p class="settings-hint"><b>版本历史</b></p>
          <span v-if="adminRuleVersions.length === 0" class="settings-empty">还没有运营改动，当前用的是代码内置目录（第 1 版）。</span>
          <div v-for="version in adminRuleVersions" v-else :key="version.version" class="admin-row">
            <div>
              <strong>第 {{ version.version }} 版 · {{ version.ruleCount }} 条规则（禁止 {{ version.blockedRuleCount }} 条）· 阈值 ¥{{ version.highRewardThreshold }}</strong>
              <small>{{ formatDeadline(version.createdAt) }} · {{ version.updatedByName ?? version.updatedBy.slice(0, 8) }}</small>
              <small class="evidence-note">依据：{{ version.changeReason }}</small>
              <small class="evidence-note">{{ version.changeSummary }}</small>
            </div>
          </div>
        </div>

        <div v-else-if="settingsTab === 'appeals'" class="settings-body">
          <p class="settings-hint">被风险规则拦下的所有者可以提交申诉。处置能力分两档：<b>转人工后被驳回</b>的任务，申诉成立即可放行（运营本来就有这个权限）；<b>被禁止类别命中</b>的任务，申诉成立也只记录"规则误伤"的结论，任务依旧不能发布——平台红线不因为多了一个入口而放开。依据会写进运营审计并通知所有者。</p>
          <p v-if="adminTaskError" class="auth-error">{{ adminTaskError }}</p>
          <p v-if="adminTaskNotice" class="settings-notice">{{ adminTaskNotice }}</p>
          <span v-if="adminRiskAppeals.length === 0" class="settings-empty">当前没有待处置的申诉。</span>
          <div v-for="item in adminRiskAppeals" v-else :key="item.taskId" class="admin-row">
            <div>
              <strong>{{ item.title }}</strong>
              <small>{{ riskVerdictLabel(item.verdict) }} · {{ item.ruleCode }} · {{ item.category }} · 规则第 {{ item.ruleVersion }} 版 · 复核 {{ riskReviewStatusLabel(item.reviewStatus) }}</small>
              <small>{{ item.canBeReleasedByAppeal ? '申诉成立可以放行' : '禁止类别：申诉成立也不放行（只记录误伤）' }} · 悬赏 ¥{{ item.rewardAmount }} {{ item.rewardCurrency }} · {{ item.district }}</small>
              <small>需求方 {{ item.ownerEmail ?? item.ownerId.slice(0, 8) }} · 申诉于 {{ item.appealedAt ? formatDeadline(item.appealedAt) : '—' }} · 这条任务累计申诉 <b>{{ item.appealCount }}</b> 次</small>
              <small class="evidence-note">申诉理由：{{ item.appealReason }}</small>
              <small class="evidence-note">命中原因：{{ item.summary }}</small>
              <small v-if="item.reviewNote" class="evidence-note">上次复核依据：{{ item.reviewNote }}</small>
              <div v-if="appealHistoryTaskId === item.taskId" class="appeal-history">
                <small v-if="appealHistory.length === 0" class="evidence-note">还没有留档记录（这张表上线之前提交的申诉查不到轨迹）。</small>
                <small v-for="record in appealHistory" v-else :key="record.id" class="evidence-note">
                  第 {{ record.ruleVersion }} 版规则 · {{ riskVerdictLabel(record.verdict) }} · {{ record.ruleCode }} · {{ formatDeadline(record.submittedAt) }} · {{ riskAppealStatusLabel(record.status) }}<template v-if="record.decisionNote"> · 结论：{{ record.decisionNote }}</template>
                  <br />理由：{{ record.reason }}
                </small>
              </div>
            </div>
            <div class="setting-actions">
              <button type="button" class="settings-secondary" :disabled="adminTaskBusy || appealHistoryBusy" @click="toggleAppealHistory(item)">{{ appealHistoryTaskId === item.taskId ? '收起轨迹' : '申诉轨迹' }}</button>
              <template v-if="adminAppealDecisionId !== item.taskId">
                <button type="button" class="settings-secondary" :disabled="adminTaskBusy" @click="startAdminAppealDecision(item)">处置申诉</button>
              </template>
              <template v-else>
                <input v-model.trim="adminAppealNote" type="text" maxlength="200" placeholder="填写处置依据（必填，最多 200 字）" />
                <button type="button" class="settings-secondary" :disabled="adminTaskBusy" @click="confirmAdminAppealDecision(item, 'Accept')">认为误伤</button>
                <button type="button" class="settings-secondary danger" :disabled="adminTaskBusy" @click="confirmAdminAppealDecision(item, 'Deny')">维持原判</button>
                <button type="button" class="settings-secondary" :disabled="adminTaskBusy" @click="adminAppealDecisionId = ''">取消</button>
              </template>
            </div>
          </div>
        </div>

        <div v-else-if="settingsTab === 'users'" class="settings-body">
          <p class="settings-hint">按邮箱或昵称检索用户；没有配置 PostgreSQL 时返回空列表。</p>
          <div class="setting-control">
            <input v-model.trim="adminUserKeyword" type="text" placeholder="按邮箱 / 昵称搜索" @keyup.enter="loadAdminUsers()" />
            <button type="button" class="settings-primary" :disabled="adminTaskBusy" @click="loadAdminUsers()">检索</button>
          </div>
          <span v-if="adminUsers.length === 0" class="settings-empty">还没有检索结果。</span>
          <div v-for="user in adminUsers" v-else :key="user.id" class="admin-row">
            <div>
              <strong>{{ user.displayName }}</strong>
              <small>{{ user.email }} · {{ user.role === 'worker' ? '服务者' : '需求方' }} · {{ formatDeadline(user.createdAt) }}</small>
            </div>
          </div>
        </div>

        <div v-else-if="settingsTab === 'audits'" class="settings-body">
          <span v-if="adminAudits.length === 0 && settingsAudits.length === 0" class="settings-empty">还没有变更记录。</span>
          <div v-for="audit in adminAudits" :key="audit.id" class="setting-audit">
            <div><strong>{{ audit.action }} · {{ audit.targetType }}</strong><small>{{ formatDeadline(audit.occurredAt) }} · {{ adminAuditActorLabel(audit.actorId) }}</small></div>
            <p><span>{{ audit.targetId.slice(0, 8) }}</span> → <b>{{ audit.reason }}</b></p>
          </div>
          <div v-for="audit in settingsAudits" :key="audit.id" class="setting-audit">
            <div><strong>{{ settingLabel(audit.key) }}</strong><small>{{ audit.action === 'Reset' ? '恢复默认' : '更新' }} · {{ formatDeadline(audit.occurredAt) }}</small></div>
            <p><span>{{ audit.oldValue }}</span> → <b>{{ audit.newValue }}</b></p>
          </div>
        </div>

        <div v-else class="settings-body">
          <span v-if="settingsLoading && settingsItems.length === 0" class="settings-empty">正在读取配置…</span>
          <div v-for="group in settingsGroups" :key="group.category" class="setting-group">
            <p class="setting-group-head">{{ group.category }}</p>
            <div v-for="item in group.items" :key="item.key" class="setting-row">
              <div class="setting-title">
                <strong>{{ item.displayName }}</strong>
                <span class="setting-badge" :class="`source-${item.source}`">{{ settingSourceLabel(item.source) }}</span>
                <span v-if="item.isSecret" class="setting-badge secret">机密</span>
                <span v-if="item.hasOverride" class="setting-badge version">第 {{ item.overrideVersion }} 版</span>
              </div>
              <p class="setting-desc">{{ item.description }}</p>
              <p class="setting-key">{{ item.key }} · 部署配置键 {{ item.configurationKey }} · 默认值 {{ item.defaultValue || '（空）' }}</p>
              <div class="setting-control">
                <select v-if="item.kind === 'Choice'" :value="settingDraft(item)" @change="setSettingDraft(item, ($event.target as HTMLSelectElement).value)">
                  <option v-for="option in item.allowedValues" :key="option" :value="option">{{ settingChoiceLabel(option) }}</option>
                </select>
                <select v-else-if="item.kind === 'Bool'" :value="settingDraft(item)" @change="setSettingDraft(item, ($event.target as HTMLSelectElement).value)">
                  <option value="true">true</option>
                  <option value="false">false</option>
                </select>
                <input v-else :value="settingDraft(item)" :type="item.kind === 'Int' ? 'number' : 'text'" :placeholder="settingPlaceholder(item)" @input="setSettingDraft(item, ($event.target as HTMLInputElement).value)" />
                <div class="setting-actions">
                  <button type="button" class="settings-primary" :disabled="settingsBusyKey === item.key" @click="saveSetting(item)">{{ settingsBusyKey === item.key ? '保存中…' : '保存' }}</button>
                  <button type="button" class="settings-secondary" :disabled="settingsBusyKey === item.key || !item.hasOverride" @click="resetSettingValue(item)">恢复默认</button>
                  <button type="button" class="settings-secondary" :disabled="settingsBusyKey === item.key" @click="runSettingTest(item)">测试连接</button>
                  <button v-if="item.isSecret" type="button" class="settings-secondary danger" :disabled="settingsBusyKey === item.key" @click="saveSetting(item, 'clear')">清空</button>
                </div>
                <p v-if="settingTest(item)" class="setting-test" :class="{ ok: settingTest(item)?.ok }">自检：{{ settingTest(item)?.message }}</p>
              </div>
            </div>
          </div>
        </div>
    </section>
  </div>
</template>
