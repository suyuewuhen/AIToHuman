<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { getDevSession, type DevSession } from './api/session'
import { clearAccessToken, getAccessToken, getCurrentUser, login, register, switchRole, type ActiveRole, type CurrentUser } from './api/auth'
import { applyForTask, approveOrder, createOrderReview, createTask, getExecutionAddress, getReviewSummary, increaseTaskReward, listMyOrders, listMyTasks, listOrderReviews, listPublishedTasks, listTaskApplications, publishTask, rejectOrder, resumeOrder, selectTaskApplication, startOrder, submitOrder, type OrderItem, type ReviewItem, type TaskApplication, type TaskItem, type ReviewSummary } from './api/tasks'
import { connectNotifications as connectNotificationHub, disconnectNotifications, listNotifications, markNotificationsRead, type NotificationEnvelope, type NotificationItem } from './api/notifications'
import { listOrderMessages, markOrderMessagesRead, sendOrderMessage, type OrderMessage } from './api/messages'
import { absoluteMaxEvidenceBytes, allowedEvidenceTypes, downloadEvidence, listOrderEvidence, uploadOrderEvidence, type EvidenceItem } from './api/evidence'
import { continueTaskConversation, type AiTaskPlan } from './api/ai'
import { createConversation, getConversation, type Conversation } from './api/conversations'
import { listSettingAudits, listSettings, resetSetting, settingChoiceLabel, settingSourceLabel, testSetting, updateSetting, type AdminSetting, type SettingAudit, type SettingTestResult } from './api/settings'

type Step = { label: string; done: boolean }
type ChatMessage = { id: string; role: 'user' | 'assistant'; content: string }

const prompt = ref('')
const reward = ref(65)
const planning = ref(false)
const planningStreamText = ref('')
const aiPlan = ref<AiTaskPlan | null>(null)
const chatMessages = ref<ChatMessage[]>([])
const conversationId = ref('')
const conversationBusy = ref(false)
const conversationError = ref('')
const suggestionMin = ref(55)
const suggestionMax = ref(75)
const suggestionSource = ref('演示建议')
const session = ref<DevSession | null>(null)
const authUser = ref<CurrentUser | null>(null)
const sessionError = ref('')
const authOpen = ref(false)
const authMode = ref<'login' | 'register'>('login')
const authBusy = ref(false)
const authError = ref('')
const authForm = ref({ email: '', password: '', displayName: '', role: 'worker' as 'owner' | 'worker' })
const roleMenuOpen = ref(false)
const roleSwitchBusy = ref(false)
const roleSwitchError = ref('')
const tasks = ref<TaskItem[]>([])
const reviewSummaries = ref<Record<string, ReviewSummary>>({})
const hallLoading = ref(true)
const hallError = ref('')
const applyingTaskId = ref('')
const applicationNotice = ref('')
const viewingApplicationsTaskId = ref('')
const taskApplications = ref<TaskApplication[]>([])
const applicationsLoading = ref(false)
const selectingApplicationId = ref('')
const publishing = ref(false)
const publishPreview = ref<TaskItem | null>(null)
const publishError = ref('')
const orders = ref<OrderItem[]>([])
const orderReviews = ref<Record<string, ReviewItem[]>>({})
const reviewDraft = ref<{ orderId: string; rating: number; comment: string } | null>(null)
const reviewBusy = ref(false)
const ordersLoading = ref(false)
const ordersError = ref('')
const orderTab = ref<'published' | 'taken'>('taken')
const steps = ref<Step[]>([])
const hallTab = ref<'public' | 'mine'>('public')
const hallFilter = ref({ district: '', minReward: '' as number | '', maxReward: '' as number | '' })
const taskCursor = ref<string | null>(null)
const hasMoreTasks = ref(false)
const orderAddresses = ref<Record<string, string>>({})
const executionAddress = ref('')
const myTasks = ref<TaskItem[]>([])
const myTasksLoading = ref(false)
const myTasksError = ref('')
const publishingTaskId = ref('')
const raisingTaskId = ref('')
const notifications = ref<NotificationItem[]>([])
const unreadCount = ref(0)
const notificationsOpen = ref(false)
const chatOrder = ref<OrderItem | null>(null)
const orderChatMessages = ref<OrderMessage[]>([])
const orderChatUnread = ref(0)
const orderChatDraft = ref('')
const orderChatBusy = ref(false)
const orderChatError = ref('')
const evidenceByOrder = ref<Record<string, EvidenceItem[]>>({})
const evidenceOpenOrderId = ref('')
const evidenceBusy = ref(false)
const evidenceError = ref('')

/** 凭证列表按需拉取：展开某个订单时才请求，避免订单列表产生 N 次查询。 */
async function toggleEvidence(order: OrderItem) {
  if (evidenceOpenOrderId.value === order.id) {
    evidenceOpenOrderId.value = ''
    return
  }
  const userId = currentUserId.value
  if (!userId) {
    applicationNotice.value = '请先登录后查看执行凭证。'
    return
  }
  evidenceOpenOrderId.value = order.id
  evidenceError.value = ''
  evidenceBusy.value = true
  try {
    evidenceByOrder.value = { ...evidenceByOrder.value, [order.id]: await listOrderEvidence(order.id, userId) }
  } catch (error) {
    evidenceError.value = error instanceof Error ? error.message : '凭证读取失败'
  } finally {
    evidenceBusy.value = false
  }
}

async function uploadEvidence(order: OrderItem, event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  const userId = currentUserId.value
  if (!file || !userId) return

  evidenceError.value = ''
  // 先在浏览器侧挡掉明显不合规的文件，减少无谓上传。
  if (!allowedEvidenceTypes.includes(file.type)) {
    evidenceError.value = '只支持 JPEG / PNG / WebP 图片和 PDF。'
    input.value = ''
    return
  }
  if (file.size > absoluteMaxEvidenceBytes) {
    // 实际上限由服务端运营配置决定，这里只挡掉连硬上限都超过的文件，其余交给服务端判断并给出准确原因。
    evidenceError.value = `凭证大小不能超过 ${absoluteMaxEvidenceBytes / 1024 / 1024} MB（服务端硬上限）。`
    input.value = ''
    return
  }

  evidenceBusy.value = true
  try {
    await uploadOrderEvidence(order.id, userId, file)
    evidenceByOrder.value = { ...evidenceByOrder.value, [order.id]: await listOrderEvidence(order.id, userId) }
    applicationNotice.value = `已上传凭证「${file.name}」。`
  } catch (error) {
    evidenceError.value = error instanceof Error ? error.message : '凭证上传失败'
  } finally {
    evidenceBusy.value = false
    input.value = ''
  }
}

async function saveEvidence(item: EvidenceItem) {
  const userId = currentUserId.value
  if (!userId) return
  evidenceError.value = ''
  try {
    await downloadEvidence(item, userId)
  } catch (error) {
    evidenceError.value = error instanceof Error ? error.message : '凭证下载失败'
  }
}

function evidenceStatusLabel(status: string) {
  return ({ Pending: '检查中', Clean: '已通过检查', Rejected: '未通过检查' } as Record<string, string>)[status] ?? status
}

// 运营配置：入口只对管理员展示，真正的授权在服务端（/api/v1/admin/settings 需要管理员身份）。
const settingsOpen = ref(false)
const settingsTab = ref<'values' | 'audits'>('values')
const settingsLoading = ref(false)
const settingsError = ref('')
const settingsNotice = ref('')
const settingsItems = ref<AdminSetting[]>([])
const settingsDrafts = ref<Record<string, string>>({})
const settingsBusyKey = ref('')
const settingsTests = ref<Record<string, SettingTestResult>>({})
const settingsAudits = ref<SettingAudit[]>([])

const isAdmin = computed(() => authUser.value?.isAdmin === true)
const settingsGroups = computed(() => {
  const categories: string[] = []
  for (const item of settingsItems.value) if (!categories.includes(item.category)) categories.push(item.category)
  return categories.map((category) => ({ category, items: settingsItems.value.filter((item) => item.category === category) }))
})

async function openSettings() {
  settingsOpen.value = true
  settingsTab.value = 'values'
  await loadSettings()
}

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

const completion = computed(() => !aiPlan.value || steps.value.length === 0
  ? 0
  : Math.round((steps.value.filter((step) => step.done).length / steps.value.length) * 100))
const currentRole = computed(() => authUser.value?.role ?? session.value?.role ?? '')
const isWorker = computed(() => currentRole.value === 'worker')
const isOwner = computed(() => currentRole.value === 'owner')
const currentUserId = computed(() => authUser.value?.userId ?? session.value?.userId ?? '')
const publishedOrders = computed(() => orders.value.filter((order) => order.ownerId === currentUserId.value))
const takenOrders = computed(() => orders.value.filter((order) => order.workerId === currentUserId.value))
const visibleOrders = computed(() => orderTab.value === 'published' ? publishedOrders.value : takenOrders.value)
function canManageTask(task: TaskItem) {
  return isOwner.value && authUser.value?.userId === task.ownerId
}
function isOwnTask(task: TaskItem) {
  return Boolean(currentUserId.value) && task.ownerId === currentUserId.value
}
function taskStatusLabel(status: string) {
  return ({ ReadyToPublish: '待发布', Published: '已发布', Assigned: '已分配', Closed: '已完成', Expired: '已过期', Cancelled: '已取消' } as Record<string, string>)[status] ?? status
}

async function submitPrompt() {
  const userMessage = prompt.value.trim()
  if (!userMessage || planning.value) return
  if (!conversationId.value) {
    conversationError.value = '会话还没有准备好，请稍后重试。'
    return
  }

  const localId = `local-${Date.now()}`
  chatMessages.value.push({ id: localId, role: 'user', content: userMessage })
  prompt.value = ''
  planning.value = true
  planningStreamText.value = ''
  conversationError.value = ''
  try {
    const turn = await continueTaskConversation(conversationId.value, currentUserId.value, userMessage, (_delta, accumulated) => {
      planningStreamText.value = accumulated
    })
    chatMessages.value.push({ id: `${localId}-reply`, role: 'assistant', content: turn.assistantMessage })
    if (turn.readyToDraft && turn.plan) {
      applyDraft(turn.plan)
      applicationNotice.value = '任务草稿已生成，请检查后确认发布。'
    }
  } catch (error) {
    // 服务端只在回合完整成功后落库，所以这里回滚本地消息，并把刚才的输入还给用户重试。
    chatMessages.value = chatMessages.value.filter((message) => message.id !== localId)
    prompt.value = userMessage
    conversationError.value = error instanceof Error ? error.message : 'AI 暂时不可用'
  } finally {
    planningStreamText.value = ''
    planning.value = false
  }
}

function applyDraft(plan: AiTaskPlan) {
  aiPlan.value = plan
  reward.value = plan.suggestedReward || reward.value
  suggestionMin.value = Math.max(15, (plan.suggestedReward || reward.value) - 10)
  suggestionMax.value = (plan.suggestedReward || reward.value) + 20
  steps.value = plan.acceptanceCriteria.map(label => ({ label, done: true }))
  suggestionSource.value = '火山引擎 AI 建议'
}

function conversationStorageKey(userId: string) {
  return `aitohuman.conversationId.${userId}`
}

function applyConversation(conversation: Conversation) {
  conversationId.value = conversation.id
  const userId = currentUserId.value
  if (userId) window.localStorage.setItem(conversationStorageKey(userId), conversation.id)
  chatMessages.value = conversation.messages.map((message) => ({ id: message.id, role: message.role, content: message.content }))
  aiPlan.value = conversation.draft
  if (conversation.draft) applyDraft(conversation.draft)
  else steps.value = []
}

/** 刷新或换设备后按服务端历史恢复对话；本地只记住上一次的会话 ID。 */
async function restoreConversation() {
  const userId = currentUserId.value
  if (!userId) return
  conversationBusy.value = true
  conversationError.value = ''
  try {
    const stored = window.localStorage.getItem(conversationStorageKey(userId))
    if (stored) {
      try {
        applyConversation(await getConversation(stored, userId))
        return
      } catch {
        window.localStorage.removeItem(conversationStorageKey(userId))
      }
    }
    applyConversation(await createConversation(userId))
  } catch (error) {
    conversationError.value = error instanceof Error ? error.message : '会话加载失败'
  } finally {
    conversationBusy.value = false
  }
}

async function startNewConversation() {
  const userId = currentUserId.value
  if (!userId || conversationBusy.value) return
  conversationBusy.value = true
  conversationError.value = ''
  try {
    applyConversation(await createConversation(userId))
    applicationNotice.value = '已开始新的对话，草稿已重置。'
  } catch (error) {
    conversationError.value = error instanceof Error ? error.message : '新建对话失败'
  } finally {
    conversationBusy.value = false
  }
}

function increaseReward() {
  reward.value += 5
}

async function openPublishPreview() {
  const plan = aiPlan.value
  if (!plan) {
    applicationNotice.value = '请先继续回答 AI 的问题，需求明确后才能生成任务草稿。'
    return
  }
  if (authUser.value && !isOwner.value) {
    applicationNotice.value = '当前账户是服务者，不能发布需求方任务。请退出后登录或注册一个“需求方”账户。'
    return
  }
  if (!authUser.value && !session.value) {
    applicationNotice.value = '请先登录需求方账户，才能创建并发布任务。'
    authMode.value = 'login'
    authOpen.value = true
    return
  }
  publishing.value = true
  publishError.value = ''
  try {
    const task = await createTask({
      ownerId: authUser.value?.userId ?? session.value!.userId,
      title: plan.title,
      description: plan.description,
      district: plan.district,
      deadline: plan.deadline,
      reward: reward.value,
      acceptanceCriteria: plan.acceptanceCriteria,
      executionAddress: executionAddress.value.trim() || undefined,
    })
    publishPreview.value = task
  } catch (error) {
    publishError.value = error instanceof Error ? error.message : '任务草稿创建失败'
  } finally {
    publishing.value = false
  }
}

async function confirmPublish() {
  if (!publishPreview.value) return
  publishing.value = true
  publishError.value = ''
  try {
    await publishTask(publishPreview.value.id, authUser.value?.userId ?? publishPreview.value.ownerId)
    applicationNotice.value = `任务「${publishPreview.value.title}」已发布到任务大厅。`
    publishPreview.value = null
    await refreshHall()
  } catch (error) {
    publishError.value = error instanceof Error ? error.message : '任务发布失败'
  } finally {
    publishing.value = false
  }
}

async function loadTasks(reset = true) {
  hallLoading.value = true
  hallError.value = ''
  try {
    const page = await listPublishedTasks({
      district: hallFilter.value.district.trim() || undefined,
      minReward: hallFilter.value.minReward === '' ? undefined : Number(hallFilter.value.minReward),
      maxReward: hallFilter.value.maxReward === '' ? undefined : Number(hallFilter.value.maxReward),
      limit: 12,
      cursor: reset ? undefined : taskCursor.value ?? undefined,
    })
    tasks.value = reset ? page.items : [...tasks.value, ...page.items]
    taskCursor.value = page.nextCursor
    hasMoreTasks.value = page.hasMore
    const owners = [...new Set(tasks.value.map(task => task.ownerId))]
    const summaries = await Promise.all(owners.map(async userId => [userId, await getReviewSummary(userId)] as const).map(item => item.catch(() => null)))
    for (const item of summaries) if (item) reviewSummaries.value[item[0]] = item[1]
  } catch (error) {
    hallError.value = error instanceof Error ? error.message : '任务大厅加载失败'
  } finally {
    hallLoading.value = false
  }
}

async function loadMoreTasks() {
  if (!hasMoreTasks.value || hallLoading.value) return
  await loadTasks(false)
}

async function applyHallFilter() {
  taskCursor.value = null
  await loadTasks(true)
}

function resetHallFilter() {
  hallFilter.value = { district: '', minReward: '', maxReward: '' }
  void applyHallFilter()
}

/** 执行地址只在订单成立后向参与者披露，这里按需拉取并就地展示。 */
async function loadExecutionAddress(task: TaskItem) {
  const userId = currentUserId.value
  if (!userId) {
    applicationNotice.value = '请先登录后查看执行地址。'
    return
  }
  try {
    const result = await getExecutionAddress(task.id, userId)
    orderAddresses.value = { ...orderAddresses.value, [task.id]: result.executionAddress ?? '需求方未填写执行地址' }
  } catch (error) {
    applicationNotice.value = error instanceof Error ? error.message : '执行地址读取失败'
  }
}

async function loadOrders() {
  const userId = currentUserId.value
  if (!userId) return
  ordersLoading.value = true
  ordersError.value = ''
  try { orders.value = await listMyOrders(userId) }
  catch (error) { ordersError.value = error instanceof Error ? error.message : '订单加载失败' }
  finally { ordersLoading.value = false }
}

async function loadMyTasks() {
  const userId = currentUserId.value
  if (!userId) {
    myTasks.value = []
    myTasksError.value = ''
    return
  }
  myTasksLoading.value = true
  myTasksError.value = ''
  try { myTasks.value = await listMyTasks(userId) }
  catch (error) { myTasksError.value = error instanceof Error ? error.message : '我的任务加载失败' }
  finally { myTasksLoading.value = false }
}

async function refreshHall() {
  await Promise.all([loadTasks(), loadMyTasks()])
}

function showMyTasks() {
  hallTab.value = 'mine'
  void loadMyTasks()
}

async function publishMyTask(task: TaskItem) {
  if (!authUser.value || !isOwner.value) {
    applicationNotice.value = '请先以需求方身份登录。'
    return
  }
  publishingTaskId.value = task.id
  applicationNotice.value = ''
  try {
    await publishTask(task.id, authUser.value.userId)
    applicationNotice.value = `任务「${task.title}」已发布到任务大厅。`
    await refreshHall()
  } catch (error) {
    applicationNotice.value = error instanceof Error ? error.message : '任务发布失败'
  } finally {
    publishingTaskId.value = ''
  }
}

function openMyTaskApplications(task: TaskItem) {
  hallTab.value = 'public'
  void viewApplications(task)
}

function openOrders() {
  orderTab.value = 'published'
  document.getElementById('orders')?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

async function raiseTaskReward(task: TaskItem) {
  if (!authUser.value || !isOwner.value) {
    applicationNotice.value = '请先以需求方身份登录。'
    return
  }
  const input = window.prompt(`当前悬赏 ¥${task.reward}。请输入新的悬赏金额（分配前只能提高，不能降低）：`, String(task.reward + 5))
  if (input === null) return
  const amount = Number(input.trim())
  if (!Number.isFinite(amount) || amount <= task.reward) {
    applicationNotice.value = `新的悬赏需要是大于当前 ¥${task.reward} 的有效金额。`
    return
  }
  raisingTaskId.value = task.id
  applicationNotice.value = ''
  try {
    const updated = await increaseTaskReward(task.id, authUser.value.userId, amount)
    applicationNotice.value = `任务「${task.title}」的悬赏已从 ¥${task.reward} 提高到 ¥${updated.reward}；已提交的报名按新金额继续有效。`
    await refreshHall()
  } catch (error) {
    applicationNotice.value = error instanceof Error ? error.message : '加价失败'
  } finally {
    raisingTaskId.value = ''
  }
}

async function transitionOrder(order: OrderItem, action: 'start' | 'submit' | 'approve' | 'reject' | 'resume') {
  if (!authUser.value) {
    applicationNotice.value = '请先登录后操作订单。'
    return
  }
  try {
    const actorId = authUser.value.userId
    const note: string = action === 'submit' ? window.prompt('请填写执行凭证或完成说明') ?? '' : action === 'reject' ? window.prompt('请说明需要补充的内容') ?? '' : ''
    if ((action === 'submit' || action === 'reject') && !note.trim()) return
    const updated = action === 'start' ? await startOrder(order.id, actorId)
      : action === 'submit' ? await submitOrder(order.id, actorId, note)
      : action === 'approve' ? await approveOrder(order.id, actorId, note)
      : action === 'reject' ? await rejectOrder(order.id, actorId, note)
      : await resumeOrder(order.id, actorId)
    orders.value = orders.value.map((item) => item.id === updated.id ? updated : item)
    applicationNotice.value = action === 'resume'
      ? `订单「${order.title}」已重新进入执行中，请按驳回原因补充后再次提交。`
      : `订单「${order.title}」已更新为${orderStatusLabel(updated.status)}。`
    if (action === 'approve') await refreshHall()
  } catch (error) {
    applicationNotice.value = error instanceof Error ? error.message : '订单操作失败'
  }
}

async function loadReviews(order: OrderItem) {
  try { orderReviews.value[order.id] = await listOrderReviews(order.id) } catch (error) { applicationNotice.value = error instanceof Error ? error.message : '评价加载失败' }
}

function openReview(order: OrderItem) {
  reviewDraft.value = { orderId: order.id, rating: 5, comment: '' }
  void loadReviews(order)
}

async function submitReview() {
  if (!reviewDraft.value || !authUser.value) return
  reviewBusy.value = true
  try {
    const draft = reviewDraft.value
    await createOrderReview(draft.orderId, authUser.value.userId, draft.rating, draft.comment)
    applicationNotice.value = '评价已提交。双方都评价后会立即公开。'
    reviewDraft.value = null
    const order = orders.value.find(item => item.id === draft.orderId)
    if (order) await loadReviews(order)
  } catch (error) { applicationNotice.value = error instanceof Error ? error.message : '评价提交失败' }
  finally { reviewBusy.value = false }
}

function orderStatusLabel(status: string) {
  return ({ Accepted: '待执行', InProgress: '执行中', Submitted: '待验收', Approved: '已完成', Rejected: '需补充执行', Disputed: '争议中', Cancelled: '已取消' } as Record<string, string>)[status] ?? status
}

/** 事件只是刷新提示：收到信封后重新拉取事实状态，不直接依赖推送内容。 */
async function connectNotifications() {
  const userId = currentUserId.value
  if (!userId) return
  try {
    await connectNotificationHub(userId, (envelope) => { void handleNotification(envelope) })
    await loadNotifications()
  } catch { /* 手动刷新仍可使用 */ }
}

async function loadNotifications() {
  const userId = currentUserId.value
  if (!userId) return
  try {
    const result = await listNotifications(userId)
    notifications.value = result.items
    unreadCount.value = result.unreadCount
  } catch { /* 通知不是关键路径，失败时保持原样 */ }
}

async function handleNotification(envelope: NotificationEnvelope) {
  applicationNotice.value = notificationText(envelope.type, envelope.payload)
  await loadNotifications()
  await loadOrders()
  if (isOwner.value) await loadMyTasks()
}

function notificationText(type: string, payload: unknown) {
  const detail = payload as { title?: string; status?: string; preview?: string }
  const title = detail?.title ?? '订单'
  if (type === 'order.created') return `需求方已同意你的报名：「${title}」已进入接取的任务。`
  if (type === 'order.statusChanged') return `订单「${title}」已更新为${orderStatusLabel(detail?.status ?? '')}。`
  if (type === 'order.messageCreated') return `订单「${title}」有新消息：${detail?.preview ?? ''}`
  return `有新的通知：${title}`
}

async function openChat(order: OrderItem) {
  const userId = currentUserId.value
  if (!userId) {
    applicationNotice.value = '请先登录后查看订单会话。'
    return
  }
  chatOrder.value = order
  orderChatDraft.value = ''
  orderChatError.value = ''
  orderChatBusy.value = true
  try {
    const result = await listOrderMessages(order.id, userId)
    orderChatMessages.value = result.items
    // 打开会话即视为已读，并把列表里的未读徽标清零。
    orderChatUnread.value = await markOrderMessagesRead(order.id, userId)
    orders.value = orders.value.map((item) => item.id === order.id ? { ...item, unreadMessageCount: 0 } : item)
  } catch (error) {
    orderChatError.value = error instanceof Error ? error.message : '会话加载失败'
  } finally {
    orderChatBusy.value = false
  }
}

async function sendChat() {
  const order = chatOrder.value
  const userId = currentUserId.value
  const content = orderChatDraft.value.trim()
  if (!order || !userId || !content || orderChatBusy.value) return
  orderChatBusy.value = true
  orderChatError.value = ''
  try {
    await sendOrderMessage(order.id, userId, content)
    orderChatDraft.value = ''
    orderChatMessages.value = (await listOrderMessages(order.id, userId)).items
  } catch (error) {
    orderChatError.value = error instanceof Error ? error.message : '消息发送失败'
  } finally {
    orderChatBusy.value = false
  }
}

function isOwnMessage(message: OrderMessage) {
  return message.senderId === currentUserId.value
}

async function toggleNotifications() {
  notificationsOpen.value = !notificationsOpen.value
  if (!notificationsOpen.value) return
  await loadNotifications()
  const userId = currentUserId.value
  if (!userId || unreadCount.value === 0) return
  try {
    unreadCount.value = await markNotificationsRead(userId)
    notifications.value = notifications.value.map((item) => ({ ...item, readAt: item.readAt ?? new Date().toISOString() }))
  } catch { /* 标记失败不影响阅读 */ }
}

async function apply(task: TaskItem) {
  const workerId = authUser.value?.role === 'worker' ? authUser.value.userId : session.value?.userId
  if (!workerId) {
    applicationNotice.value = '请先以服务者身份登录。'
    return
  }
  applyingTaskId.value = task.id
  applicationNotice.value = ''
  try {
    await applyForTask(task.id, workerId, '我已查看任务要求，可以按固定悬赏完成。')
    applicationNotice.value = `已报名「${task.title}」`
    await loadTasks()
  } catch (error) {
    applicationNotice.value = error instanceof Error ? error.message : '报名失败'
  } finally {
    applyingTaskId.value = ''
  }
}

async function viewApplications(task: TaskItem) {
  if (!authUser.value || !isOwner.value) {
    applicationNotice.value = '请先以需求方身份登录。'
    return
  }
  viewingApplicationsTaskId.value = task.id
  taskApplications.value = []
  applicationsLoading.value = true
  applicationNotice.value = ''
  try {
    taskApplications.value = await listTaskApplications(task.id, authUser.value.userId)
  } catch (error) {
    applicationNotice.value = error instanceof Error ? error.message : '报名列表加载失败'
    viewingApplicationsTaskId.value = ''
  } finally {
    applicationsLoading.value = false
  }
}

async function selectApplication(task: TaskItem, application: TaskApplication) {
  if (!authUser.value || !isOwner.value) {
    applicationNotice.value = '请先以需求方身份登录。'
    return
  }
  selectingApplicationId.value = application.id
  applicationNotice.value = ''
  try {
    const result = await selectTaskApplication(task.id, application.id, authUser.value.userId)
    applicationNotice.value = `已选择报名者，任务「${task.title}」已创建订单（${result.order.status}）。`
    viewingApplicationsTaskId.value = ''
    await refreshHall()
    await loadOrders()
  } catch (error) {
    applicationNotice.value = error instanceof Error ? error.message : '选择报名者失败'
  } finally {
    selectingApplicationId.value = ''
  }
}

function formatDeadline(value: string) {
  return new Intl.DateTimeFormat('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }).format(new Date(value))
}

async function loadDevSession() {
  try {
    session.value = await getDevSession()
  } catch (error) {
    sessionError.value = error instanceof Error ? error.message : '开发会话加载失败'
  }
}

async function restoreAuth() {
  if (!getAccessToken()) return
  try {
    authUser.value = await getCurrentUser()
  } catch {
    authUser.value = null
  }
}

async function submitAuth() {
  authBusy.value = true
  authError.value = ''
  try {
    const result = authMode.value === 'login'
      ? await login({ email: authForm.value.email, password: authForm.value.password })
      : await register(authForm.value)
    // /auth/me 里带 isAdmin，重新拉一次才能决定是否展示运营配置入口。
    await restoreAuth()
    authOpen.value = false
    applicationNotice.value = `已登录：${result.displayName}`
    await restoreConversation()
    await refreshHall()
    await loadOrders()
    await connectNotifications()
  } catch (error) {
    authError.value = error instanceof Error ? error.message : '认证失败'
  } finally {
    authBusy.value = false
  }
}

async function changeRole(role: ActiveRole) {
  if (!authUser.value || authUser.value.role === role) {
    roleMenuOpen.value = false
    return
  }
  roleSwitchBusy.value = true
  roleSwitchError.value = ''
  try {
    await switchRole(role)
    await restoreAuth()
    roleMenuOpen.value = false
    viewingApplicationsTaskId.value = ''
    applicationNotice.value = `已切换为${role === 'owner' ? '需求方' : '服务者'}身份。`
    await refreshHall()
    await loadOrders()
    await connectNotifications()
  } catch (error) {
    roleSwitchError.value = error instanceof Error ? error.message : '身份切换失败'
  } finally {
    roleSwitchBusy.value = false
  }
}

function logout() {
  clearAccessToken()
  authUser.value = null
  orders.value = []
  myTasks.value = []
  // 运营配置只属于管理员会话，退出时一并清掉，避免下一个登录者看到上一个会话的数据。
  settingsOpen.value = false
  settingsItems.value = []
  settingsAudits.value = []
  settingsTests.value = {}
  settingsDrafts.value = {}
  void disconnectNotifications()
  roleMenuOpen.value = false
  applicationNotice.value = '已退出登录，当前为开发会话。'
  void restoreConversation()
}

onMounted(async () => {
  await restoreAuth()
  await loadDevSession()
  await restoreConversation()
  await loadTasks()
  await loadMyTasks()
  await loadOrders()
  await connectNotifications()
})
</script>

<template>
  <main class="shell">
    <nav class="topbar">
      <a class="brand" href="#" aria-label="AIToHuman 首页">
        <span class="brand-mark">A/H</span>
        <span>AIToHuman</span>
      </a>
      <div class="nav-links" aria-label="主导航">
        <a class="active" href="#workspace">任务工作台</a>
        <a href="#hall">任务大厅</a>
        <a href="#orders">我的订单</a>
      </div>
      <div class="profile-wrap">
        <button v-if="isAdmin" class="notify admin-entry" type="button" @click="openSettings">运营配置 <b>⚙</b></button>
        <div class="notify-wrap">
          <button class="notify" type="button" @click="toggleNotifications">通知 <b v-if="unreadCount > 0">{{ unreadCount > 99 ? '99+' : unreadCount }}</b></button>
          <div v-if="notificationsOpen" class="notify-menu">
            <p>最近通知 · 打开即标记已读</p>
            <span v-if="notifications.length === 0">还没有通知。</span>
            <div v-for="item in notifications" v-else :key="item.id" class="notify-item" :class="{ unread: !item.readAt }">
              <strong>{{ notificationText(item.type, item.payload) }}</strong>
              <small>{{ formatDeadline(item.createdAt) }}</small>
            </div>
          </div>
        </div>
        <button class="profile" type="button" @click="authUser ? (roleMenuOpen = !roleMenuOpen) : (authOpen = true)"><span>{{ (authUser?.displayName ?? session?.displayName ?? '开').slice(0, 1) }}</span> {{ authUser?.displayName ?? session?.displayName ?? '开发会话' }} · {{ authUser ? (authUser.role === 'worker' ? '服务者' : '需求方') : '开发会话 · 点击登录' }} <b v-if="authUser">⌄</b></button>
        <div v-if="authUser && roleMenuOpen" class="role-menu">
          <p>当前账户 · {{ authUser.email }}</p>
          <button type="button" :class="{ selected: authUser.role === 'owner' }" :disabled="roleSwitchBusy" @click="changeRole('owner')"><span>需求方</span><small>创建任务、查看报名、选择服务者</small></button>
          <button type="button" :class="{ selected: authUser.role === 'worker' }" :disabled="roleSwitchBusy" @click="changeRole('worker')"><span>服务者</span><small>浏览大厅、按固定悬赏报名</small></button>
          <p v-if="roleSwitchError" class="role-error">{{ roleSwitchError }}</p>
          <button type="button" class="logout-button" @click="logout">退出登录</button>
        </div>
      </div>
    </nav>

    <div v-if="authOpen" class="auth-backdrop" @click.self="authOpen = false">
      <form class="auth-dialog" @submit.prevent="submitAuth">
        <div class="auth-dialog-head"><div><p class="eyebrow">IDENTITY GATE / LOCAL</p><h2>{{ authMode === 'login' ? '登录你的账户' : '创建一个账户' }}</h2></div><button type="button" class="icon-button" @click="authOpen = false">×</button></div>
        <label>邮箱<input v-model.trim="authForm.email" type="email" required autocomplete="email" /></label>
        <label>密码<input v-model="authForm.password" type="password" required minlength="8" autocomplete="current-password" /></label>
        <label v-if="authMode === 'register'">显示名称<input v-model.trim="authForm.displayName" required maxlength="80" /></label>
        <label v-if="authMode === 'register'">角色<select v-model="authForm.role"><option value="worker">服务者</option><option value="owner">需求方</option></select></label>
        <p v-if="authError" class="auth-error">{{ authError }}</p>
        <button class="auth-submit" type="submit" :disabled="authBusy">{{ authBusy ? '处理中…' : (authMode === 'login' ? '登录' : '注册并登录') }}</button>
        <button class="auth-switch" type="button" @click="authMode = authMode === 'login' ? 'register' : 'login'; authError = ''">{{ authMode === 'login' ? '还没有账户？创建一个' : '已有账户？返回登录' }}</button>
      </form>
    </div>

    <section class="hero">
      <div>
        <p class="eyebrow">REAL-WORLD TASK DESK / 01</p>
        <h1>把一句想法，变成<br /><em>真正完成的事。</em></h1>
      </div>
      <div class="hero-note">
        <span class="pulse"></span>
        <p>AI 正在协助你澄清需求。任务发布与悬赏金额始终由你确认。</p>
      </div>
    </section>

    <section id="workspace" class="workspace">
      <article class="conversation panel">
        <header class="panel-head">
          <div><span class="index">01</span><h2>告诉我你想完成什么</h2></div>
          <div class="panel-head-actions">
            <span class="status">{{ conversationBusy ? '载入中' : (conversationId ? '已保存' : '未连接') }}</span>
            <button type="button" class="secondary-action" :disabled="conversationBusy || planning" @click="startNewConversation">新建对话</button>
          </div>
        </header>

        <div class="messages" aria-live="polite">
          <div v-for="message in chatMessages" :key="message.id" class="message reveal" :class="message.role === 'user' ? 'user' : 'ai'">
            <div v-if="message.role === 'assistant'" class="avatar">AI</div>
            <div><p>{{ message.content }}</p></div>
            <div v-if="message.role === 'user'" class="avatar">你</div>
          </div>
          <div v-if="planning" class="message ai reveal">
            <div class="avatar">AI</div>
            <div class="streaming-reply">
              <p v-if="planningStreamText">{{ planningStreamText }}</p>
              <span v-else class="typing-indicator"><i></i><i></i><i></i></span>
            </div>
          </div>
          <div v-if="conversationBusy && chatMessages.length === 0" class="message ai reveal">
            <div class="avatar">AI</div>
            <div><p>正在读取对话历史…</p></div>
          </div>
        </div>

        <p v-if="conversationError" class="notice warning">{{ conversationError }}</p>

        <form class="composer" @submit.prevent="submitPrompt">
          <label for="task-prompt">你的回答</label>
          <textarea id="task-prompt" v-model="prompt" rows="3" placeholder="说说你的想法…" :disabled="planning" />
          <div class="composer-actions">
            <span>不要填写身份证号、银行卡等敏感信息</span>
            <button type="submit" :disabled="planning || !prompt.trim()">{{ planning ? '回复中…' : '发送' }} <b>↗</b></button>
          </div>
        </form>
      </article>

      <aside class="draft panel">
        <header class="panel-head">
          <div><span class="index">02</span><h2>任务草稿</h2></div>
          <span class="progress">{{ aiPlan ? `${completion}%` : '—' }}</span>
        </header>

        <div v-if="!aiPlan" class="draft-waiting">
          <span class="draft-waiting-mark">···</span>
          <div><strong>需求澄清中</strong><p>请回答 AI 当前的问题。</p></div>
        </div>

        <div v-else class="draft-body">
          <div class="category">待发布任务 <span>已整理</span></div>
          <h3>{{ aiPlan.title }}</h3>
          <p class="location">{{ aiPlan.district }} · 截止 {{ formatDeadline(aiPlan.deadline) }}</p>

          <div class="checklist">
            <button v-for="step in steps" :key="step.label" type="button" @click="step.done = !step.done">
              <span :class="{ done: step.done }">{{ step.done ? '✓' : '' }}</span>{{ step.label }}
            </button>
          </div>

          <div class="reward-card">
            <div><span>AI 建议悬赏</span><strong>¥{{ suggestionMin }}–{{ suggestionMax }}</strong></div>
            <p>{{ suggestionSource }} · 最终金额由你确认</p>
            <div class="reward-control">
              <label for="reward">你的悬赏</label>
              <button type="button" @click="increaseReward">¥ {{ reward }} <span>＋5</span></button>
            </div>
          </div>

          <label class="address-field">
            <span>执行地址（仅接单后向被选中的服务者披露，大厅只显示区域）</span>
            <input v-model.trim="executionAddress" type="text" maxlength="200" placeholder="例如：世纪大道 100 号 A 座前台" />
          </label>

          <button class="publish" type="button" :disabled="!aiPlan || completion < 100 || publishing" @click="openPublishPreview">{{ publishing ? '正在创建草稿…' : '确认草稿并预览发布' }}</button>
          <p v-if="publishError" class="publish-error">{{ publishError }}</p>
          <p class="guardrail">服务者按固定悬赏报名，不进行竞价。发布后、分配前你仍可以加价。</p>
        </div>
      </aside>
    </section>

    <div v-if="publishPreview" class="auth-backdrop" @click.self="publishPreview = null">
      <section class="auth-dialog publish-dialog">
        <div class="auth-dialog-head"><div><p class="eyebrow">PUBLISH CHECK / 03</p><h2>确认发布任务</h2></div><button type="button" class="icon-button" @click="publishPreview = null">×</button></div>
        <div class="preview-card"><span>{{ publishPreview.district }} · 草稿已保存</span><h3>{{ publishPreview.title }}</h3><p>{{ publishPreview.description }}</p><div><strong>固定悬赏 ¥{{ publishPreview.reward }}</strong><small>截止 {{ formatDeadline(publishPreview.deadline) }} · {{ publishPreview.status }}</small></div></div>
        <p class="publish-copy">发布后服务者将按 ¥{{ publishPreview.reward }} 报名。你可以在分配前加价，但不能降价。</p>
        <button class="auth-submit" type="button" :disabled="publishing" @click="confirmPublish">{{ publishing ? '发布中…' : '确认并发布到任务大厅' }}</button>
      </section>
    </div>

    <section id="hall" class="task-hall">
      <header class="hall-head">
        <div>
          <p class="eyebrow">OPEN TASK BOARD / 02</p>
          <h2>任务大厅</h2>
          <p>价格由用户确认，服务者按固定悬赏报名。先看清任务，也看清彼此。</p>
        </div>
        <button type="button" @click="refreshHall">刷新任务 <span>↻</span></button>
      </header>

      <div class="order-tabs hall-tabs" role="tablist" aria-label="任务视图">
        <button type="button" role="tab" :aria-selected="hallTab === 'public'" :class="{ active: hallTab === 'public' }" @click="hallTab = 'public'">公开任务大厅 <span>{{ tasks.length }}</span></button>
        <button type="button" role="tab" :aria-selected="hallTab === 'mine'" :class="{ active: hallTab === 'mine' }" @click="showMyTasks">我发布的任务 <span>{{ myTasks.length }}</span></button>
      </div>

      <form class="hall-filter" @submit.prevent="applyHallFilter">
        <label>区域<input v-model="hallFilter.district" type="text" placeholder="例如：浦东新区" maxlength="40" /></label>
        <label>悬赏下限<input v-model="hallFilter.minReward" type="number" min="0" step="1" placeholder="0" /></label>
        <label>悬赏上限<input v-model="hallFilter.maxReward" type="number" min="0" step="1" placeholder="不限" /></label>
        <button type="submit">筛选</button>
        <button type="button" class="secondary-action" @click="resetHallFilter">重置</button>
      </form>

      <p v-if="applicationNotice" class="notice">{{ applicationNotice }}</p>
      <p v-if="sessionError" class="notice warning">{{ sessionError }}</p>

      <template v-if="hallTab === 'public'">
      <div v-if="hallLoading" class="hall-state">正在从 PostgreSQL 读取任务…</div>
      <div v-else-if="hallError" class="hall-state error">
        <strong>任务大厅暂时离线</strong><span>{{ hallError }}</span><button type="button" @click="applyHallFilter()">重新连接</button>
      </div>
      <div v-else-if="tasks.length === 0" class="hall-state">
        <strong>还没有可报名的任务</strong><span>发布第一条任务后，它会出现在这里。</span>
      </div>
      <div v-else class="task-grid">
        <article v-for="(task, index) in tasks" :key="task.id" class="task-card">
          <div class="task-meta"><span>NO. {{ String(index + 1).padStart(2, '0') }}</span><span>{{ task.district }}</span></div>
          <p v-if="isOwnTask(task)" class="own-flag">我发布的 · {{ taskStatusLabel(task.status) }}</p>
          <h3>{{ task.title }}</h3>
          <p>{{ task.description }}</p>
          <div class="task-facts">
            <div><small>固定悬赏</small><strong>¥{{ task.reward }}</strong></div>
            <div><small>截止</small><strong>{{ formatDeadline(task.deadline) }}</strong></div>
          </div>
          <div class="owner-trust">
            <span class="mini-avatar">需</span>
            <div><strong>{{ reviewSummaries[task.ownerId] ? `需求方 ${reviewSummaries[task.ownerId]!.averageRating.toFixed(1)} 分` : '需求方暂未有评价' }}</strong><small>{{ reviewSummaries[task.ownerId] ? `${reviewSummaries[task.ownerId]!.reviewCount} 条已公开评价` : '完成订单后可互相评价' }}</small></div>
          </div>
          <div class="task-actions">
            <span>{{ task.applicationCount }} 人已报名</span>
            <div class="task-action-buttons">
              <button v-if="canManageTask(task) && task.applicationCount > 0" type="button" class="secondary-action" @click="viewApplications(task)">{{ viewingApplicationsTaskId === task.id ? '收起报名' : '查看报名' }}</button>
              <button v-if="canManageTask(task) && task.status === 'Published'" type="button" class="secondary-action" :disabled="raisingTaskId === task.id" @click="raiseTaskReward(task)">{{ raisingTaskId === task.id ? '加价中…' : '提高悬赏' }}</button>
              <button v-if="isWorker" type="button" :disabled="applyingTaskId === task.id" @click="apply(task)">{{ applyingTaskId === task.id ? '提交中…' : '按此悬赏报名' }}</button>
              <button v-if="!authUser && !session" type="button" class="secondary-action" @click="authOpen = true">登录后操作</button>
            </div>
          </div>
          <div v-if="viewingApplicationsTaskId === task.id" class="applications-panel">
            <strong>报名者</strong>
            <span v-if="applicationsLoading">正在读取报名信息…</span>
            <span v-else-if="taskApplications.length === 0">暂无有效报名</span>
            <div v-for="application in taskApplications" v-else :key="application.id" class="application-row">
              <div><strong>服务者 {{ application.workerId.slice(0, 8) }}</strong><small>{{ application.note || '未附加备注' }}</small></div>
              <button type="button" :disabled="selectingApplicationId === application.id || application.status !== 'Pending'" @click="selectApplication(task, application)">{{ application.status === 'Pending' ? (selectingApplicationId === application.id ? '选择中…' : '选择') : application.status }}</button>
            </div>
          </div>
        </article>
      </div>
      <div v-if="hasMoreTasks" class="hall-more">
        <button type="button" :disabled="hallLoading" @click="loadMoreTasks">{{ hallLoading ? '加载中…' : '加载更多任务' }}</button>
      </div>
      </template>

      <template v-else>
        <div v-if="!currentUserId" class="hall-state">
          <strong>登录后查看你发布的任务</strong><span>任务状态、报名人数和后续操作都会显示在这里。</span>
          <button type="button" @click="authOpen = true">登录或注册</button>
        </div>
        <div v-else-if="myTasksLoading" class="hall-state">正在读取你发布的任务…</div>
        <div v-else-if="myTasksError" class="hall-state error">
          <strong>我的任务暂时离线</strong><span>{{ myTasksError }}</span><button type="button" @click="loadMyTasks">重新连接</button>
        </div>
        <div v-else-if="myTasks.length === 0" class="hall-state">
          <strong>还没有发布过任务</strong><span>在任务工作台描述需求并确认发布后，任务会出现在这里。</span>
        </div>
        <div v-else class="orders-list">
          <article v-for="task in myTasks" :key="task.id" class="order-row">
            <div>
              <small>{{ taskStatusLabel(task.status) }} · 截止 {{ formatDeadline(task.deadline) }} · {{ task.district }}</small>
              <h3>{{ task.title }}</h3>
              <span>任务号 {{ task.id.slice(0, 8) }} · {{ task.applicationCount }} 人已报名</span>
              <div class="order-actions">
                <button v-if="task.status === 'ReadyToPublish'" type="button" :disabled="publishingTaskId === task.id" @click="publishMyTask(task)">{{ publishingTaskId === task.id ? '发布中…' : '发布到任务大厅' }}</button>
                <button v-if="task.status === 'Published' && task.applicationCount > 0" type="button" class="secondary-action" @click="openMyTaskApplications(task)">查看报名</button>
                <button v-else-if="task.status === 'Published'" type="button" class="secondary-action" @click="hallTab = 'public'">去大厅查看</button>
                <button v-if="task.status === 'Published'" type="button" :disabled="raisingTaskId === task.id" @click="raiseTaskReward(task)">{{ raisingTaskId === task.id ? '加价中…' : '加价' }}</button>
                <button v-if="task.status === 'Assigned' || task.status === 'Closed'" type="button" class="secondary-action" @click="openOrders">查看订单</button>
              </div>
            </div>
            <strong>¥{{ task.reward }}</strong>
          </article>
        </div>
      </template>
    </section>

    <section id="orders" class="orders-section">
      <header class="hall-head"><div><p class="eyebrow">ORDER DESK / 03</p><h2>我的订单</h2><p>分别管理你发布的订单，以及你接取的任务。服务者在线时会实时收到订单通知。</p></div><button type="button" @click="loadOrders">刷新订单 <span>↻</span></button></header>
      <div class="order-tabs" role="tablist" aria-label="订单类型">
        <button type="button" role="tab" :aria-selected="orderTab === 'published'" :class="{ active: orderTab === 'published' }" @click="orderTab = 'published'">我发布的订单 <span>{{ publishedOrders.length }}</span></button>
        <button type="button" role="tab" :aria-selected="orderTab === 'taken'" :class="{ active: orderTab === 'taken' }" @click="orderTab = 'taken'">我接取的任务 <span>{{ takenOrders.length }}</span></button>
      </div>
      <div v-if="ordersLoading" class="hall-state">正在读取订单…</div>
      <div v-else-if="ordersError" class="hall-state error"><strong>订单暂时离线</strong><span>{{ ordersError }}</span><button type="button" @click="loadOrders">重新连接</button></div>
      <div v-else-if="visibleOrders.length === 0" class="hall-state"><strong>{{ orderTab === 'published' ? '还没有发布订单' : '还没有接取任务' }}</strong><span>{{ orderTab === 'published' ? '确认发布并选择服务者后，订单会显示在这里。' : '在任务大厅报名并被需求方选中后，任务会显示在这里。' }}</span></div>
      <div v-else class="orders-list">
        <article v-for="order in visibleOrders" :key="order.id" class="order-row"><div><small>{{ formatDeadline(order.createdAt) }} · {{ orderStatusLabel(order.status) }}</small><h3>{{ order.title }}</h3><span>订单号 {{ order.id.slice(0, 8) }} · {{ orderTab === 'published' ? '服务者待执行' : '需求方已确认' }}</span><p v-if="order.evidenceNote" class="order-note"><b>执行凭证：</b>{{ order.evidenceNote }}</p><p v-if="order.rejectionNote" class="order-note rejection"><b>驳回原因：</b>{{ order.rejectionNote }}</p><p v-else-if="order.reviewNote" class="order-note"><b>验收意见：</b>{{ order.reviewNote }}</p><p v-if="order.reworkCount > 0" class="order-note"><b>返工次数：</b>{{ order.reworkCount }} 次</p><p v-if="orderAddresses[order.taskId]" class="order-note"><b>执行地址：</b>{{ orderAddresses[order.taskId] }}</p><div v-if="evidenceOpenOrderId === order.id" class="evidence-panel">
              <strong>执行凭证</strong>
              <span v-if="evidenceBusy && !evidenceByOrder[order.id]">正在读取凭证…</span>
              <span v-else-if="!evidenceByOrder[order.id]?.length">还没有上传凭证。</span>
              <div v-for="item in evidenceByOrder[order.id]" v-else :key="item.id" class="evidence-row">
                <div>
                  <strong>{{ item.fileName }}</strong>
                  <small>{{ (item.sizeBytes / 1024).toFixed(1) }} KB · {{ item.contentType }} · {{ evidenceStatusLabel(item.scanStatus) }}<template v-if="item.scanAttempts > 0"> · 已检查 {{ item.scanAttempts }} 次</template></small>
                  <small v-if="item.metadataRemoved" class="evidence-note">已在上传时移除元数据：{{ item.metadataRemoved }}</small>
                  <small v-if="item.lastScanNote" class="evidence-note">{{ item.lastScanNote }}{{ item.scanExhausted ? '（不会再自动重试）' : '' }}</small>
                </div>
                <button type="button" :disabled="!item.isDownloadable" @click="saveEvidence(item)">{{ item.isDownloadable ? '下载' : '不可下载' }}</button>
              </div>
              <label v-if="orderTab === 'taken' && (order.status === 'InProgress' || order.status === 'Submitted')" class="evidence-upload">
                上传凭证（JPEG / PNG / WebP / PDF，单份上限以服务端配置为准）
                <input type="file" accept="image/jpeg,image/png,image/webp,application/pdf" :disabled="evidenceBusy" @change="uploadEvidence(order, $event)" />
              </label>
              <p v-if="evidenceError" class="notice warning">{{ evidenceError }}</p>
            </div><div class="order-actions"><button type="button" class="secondary-action" @click="openChat(order)">消息<span v-if="order.unreadMessageCount" class="unread-dot">{{ order.unreadMessageCount }}</span></button><button type="button" class="secondary-action" @click="toggleEvidence(order)">{{ evidenceOpenOrderId === order.id ? '收起凭证' : '凭证' }}</button><button type="button" class="secondary-action" @click="loadExecutionAddress({ id: order.taskId } as TaskItem)">执行地址</button><button v-if="orderTab === 'taken' && order.status === 'Accepted'" type="button" @click="transitionOrder(order, 'start')">开始执行</button><button v-if="orderTab === 'taken' && order.status === 'InProgress'" type="button" @click="transitionOrder(order, 'submit')">提交验收</button><button v-if="orderTab === 'taken' && order.status === 'Rejected'" type="button" @click="transitionOrder(order, 'resume')">继续返工</button><button v-if="orderTab === 'published' && order.status === 'Submitted'" type="button" @click="transitionOrder(order, 'approve')">确认完成</button><button v-if="orderTab === 'published' && order.status === 'Submitted'" type="button" class="secondary-action" @click="transitionOrder(order, 'reject')">需要补充</button><button v-if="order.status === 'Approved'" type="button" class="secondary-action" @click="openReview(order)">写评价</button></div><div v-if="orderReviews[order.id]?.length" class="review-list"><div v-for="review in orderReviews[order.id]" :key="review.id"><span class="review-stars">{{ '★'.repeat(review.rating) }}{{ '☆'.repeat(5 - review.rating) }}</span><span>{{ review.comment }}</span><small>{{ review.isVisible ? '已公开' : '盲期中' }}</small></div></div></div><strong>¥{{ order.reward }}</strong></article>
      </div>
    </section>

    <div v-if="reviewDraft" class="auth-backdrop" @click.self="reviewDraft = null"><form class="auth-dialog" @submit.prevent="submitReview"><div class="auth-dialog-head"><div><p class="eyebrow">DOUBLE-SIDED REVIEW / 04</p><h2>完成这次互相确认</h2></div><button type="button" class="icon-button" @click="reviewDraft = null">×</button></div><label>评分<select v-model.number="reviewDraft.rating"><option v-for="rating in 5" :key="rating" :value="rating">{{ '★'.repeat(rating) }}{{ '☆'.repeat(5 - rating) }}</option></select></label><label>评价内容<textarea v-model.trim="reviewDraft.comment" rows="4" maxlength="1000" placeholder="说说这次协作中值得被记住的细节" /></label><p class="review-hint">双方都提交评价后立即公开；如果只有一方评价，7 天后自动公开。</p><button class="auth-submit" type="submit" :disabled="reviewBusy">{{ reviewBusy ? '提交中…' : '提交评价' }}</button></form></div>

    <div v-if="chatOrder" class="auth-backdrop" @click.self="chatOrder = null">
      <section class="auth-dialog chat-dialog">
        <div class="auth-dialog-head"><div><p class="eyebrow">ORDER CHAT / 05</p><h2>订单会话</h2></div><button type="button" class="icon-button" @click="chatOrder = null">×</button></div>
        <p class="chat-hint">订单「{{ chatOrder.title }}」· 只有订单双方可见<span v-if="orderChatUnread > 0"> · 未读 {{ orderChatUnread }}</span></p>
        <div class="chat-body">
          <span v-if="orderChatBusy && orderChatMessages.length === 0" class="chat-empty">正在读取消息…</span>
          <span v-else-if="orderChatMessages.length === 0" class="chat-empty">还没有消息，先说明进度或约定时间。</span>
          <div v-for="message in orderChatMessages" v-else :key="message.id" class="chat-message" :class="{ mine: isOwnMessage(message) }">
            <p>{{ message.content }}</p>
            <small>{{ formatDeadline(message.createdAt) }}{{ isOwnMessage(message) ? ' · 我' : '' }}</small>
          </div>
        </div>
        <form class="chat-composer" @submit.prevent="sendChat">
          <textarea v-model.trim="orderChatDraft" rows="3" maxlength="2000" placeholder="说说进度、约定时间或补充说明…" />
          <button class="auth-submit" type="submit" :disabled="orderChatBusy || !orderChatDraft.trim()">{{ orderChatBusy ? '处理中…' : '发送' }}</button>
        </form>
        <p v-if="orderChatError" class="auth-error">{{ orderChatError }}</p>
      </section>
    </div>

    <div v-if="settingsOpen" class="auth-backdrop" @click.self="settingsOpen = false">
      <section class="auth-dialog settings-dialog">
        <div class="auth-dialog-head"><div><p class="eyebrow">OPERATIONS CONSOLE / 06</p><h2>运营配置</h2></div><button type="button" class="icon-button" @click="settingsOpen = false">×</button></div>
        <p class="settings-hint">生效顺序：后台覆盖 → 部署配置 → 代码默认值。机密只以掩码展示、审计也只留掩码；保存后立即生效，不需要重启服务。</p>
        <div class="settings-tabs">
          <button type="button" :class="{ selected: settingsTab === 'values' }" @click="settingsTab = 'values'">配置项 <b>{{ settingsItems.length }}</b></button>
          <button type="button" :class="{ selected: settingsTab === 'audits' }" @click="loadSettingAudits()">变更记录</button>
          <button type="button" class="settings-refresh" :disabled="settingsLoading" @click="settingsTab === 'audits' ? loadSettingAudits() : loadSettings()">{{ settingsLoading ? '读取中…' : '刷新 ↻' }}</button>
        </div>
        <p v-if="settingsError" class="auth-error">{{ settingsError }}</p>
        <p v-if="settingsNotice" class="settings-notice">{{ settingsNotice }}</p>

        <div v-if="settingsTab === 'audits'" class="settings-body">
          <span v-if="settingsAudits.length === 0" class="settings-empty">还没有变更记录。</span>
          <div v-for="audit in settingsAudits" v-else :key="audit.id" class="setting-audit">
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

    <footer class="trust-strip">
      <span>固定悬赏</span><span>双向评价</span><span>隐私分级披露</span><span>关键操作需确认</span>
    </footer>
  </main>
</template>
