<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { requestRewardSuggestion } from './api/rewards'
import { getDevSession, type DevSession } from './api/session'
import { clearAccessToken, getAccessToken, getCurrentUser, login, register, switchRole, type ActiveRole, type AuthResponse } from './api/auth'
import { applyForTask, createTask, listPublishedTasks, listTaskApplications, publishTask, selectTaskApplication, type TaskApplication, type TaskItem } from './api/tasks'

type Step = { label: string; done: boolean }

const prompt = ref('明天下午帮我去徐汇区取一份文件，再送到静安寺附近。')
const reward = ref(65)
const sent = ref(false)
const planning = ref(false)
const suggestionMin = ref(55)
const suggestionMax = ref(75)
const suggestionSource = ref('演示建议')
const session = ref<DevSession | null>(null)
const authUser = ref<Pick<AuthResponse, 'userId' | 'email' | 'displayName' | 'role'> | null>(null)
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
const ordersLoading = ref(false)
const ordersError = ref('')
const steps = ref<Step[]>([
  { label: '确认取件地址与联系人', done: true },
  { label: '核对送达时间窗口', done: true },
  { label: '明确文件交接凭证', done: false },
])

const completion = computed(() => Math.round((steps.value.filter((step) => step.done).length / steps.value.length) * 100))
const currentRole = computed(() => authUser.value?.role ?? session.value?.role ?? '')
const isWorker = computed(() => currentRole.value === 'worker')
const isOwner = computed(() => currentRole.value === 'owner')
function canManageTask(task: TaskItem) {
  return isOwner.value && authUser.value?.userId === task.ownerId
}

async function submitPrompt() {
  planning.value = true
  sent.value = true
  try {
    const suggestion = await requestRewardSuggestion()
    suggestionMin.value = suggestion.minimumReward
    suggestionMax.value = suggestion.maximumReward
    reward.value = suggestion.suggestedReward
    suggestionSource.value = suggestion.dataConfidence === 'cold-start' ? '冷启动规则建议' : '历史数据建议'
  } catch {
    suggestionSource.value = 'API 未连接 · 使用演示建议'
  } finally {
    steps.value[2]!.done = true
    planning.value = false
  }
}

function increaseReward() {
  reward.value += 5
}

function draftDeadline() {
  const deadline = new Date()
  deadline.setDate(deadline.getDate() + 1)
  deadline.setHours(18, 0, 0, 0)
  return deadline.toISOString()
}

async function openPublishPreview() {
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
      title: '明日下午代取并递送文件',
      description: prompt.value,
      district: '徐汇区 → 静安区',
      deadline: draftDeadline(),
      reward: reward.value,
      acceptanceCriteria: steps.value.map((step) => step.label),
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
    await loadTasks()
  } catch (error) {
    publishError.value = error instanceof Error ? error.message : '任务发布失败'
  } finally {
    publishing.value = false
  }
}

async function loadTasks() {
  hallLoading.value = true
  hallError.value = ''
  try {
    tasks.value = await listPublishedTasks()
  } catch (error) {
    hallError.value = error instanceof Error ? error.message : '任务大厅加载失败'
  } finally {
    hallLoading.value = false
  }
}

async function loadOrders() {
  const userId = authUser.value?.userId ?? session.value?.userId
  if (!userId) return
  ordersLoading.value = true
  ordersError.value = ''
  try { orders.value = await listMyOrders(userId) }
  catch (error) { ordersError.value = error instanceof Error ? error.message : '订单加载失败' }
  finally { ordersLoading.value = false }
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
    await loadOrders()
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
    await loadTasks()
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
    authUser.value = result
    authOpen.value = false
    applicationNotice.value = `已登录：${result.displayName}`
    await loadTasks()
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
    const result = await switchRole(role)
    authUser.value = result
    roleMenuOpen.value = false
    viewingApplicationsTaskId.value = ''
    applicationNotice.value = `已切换为${role === 'owner' ? '需求方' : '服务者'}身份。`
    await loadTasks()
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
  roleMenuOpen.value = false
  applicationNotice.value = '已退出登录，当前为开发会话。'
}

onMounted(async () => {
  await restoreAuth()
  await loadDevSession()
  await loadTasks()
  await loadOrders()
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
          <span class="status">对话中</span>
        </header>

        <div class="messages">
          <div class="message ai">
            <div class="avatar">AI</div>
            <div><strong>我来帮你把需求变清楚。</strong><p>先说目标就好。地点、时间、验收方式和风险，我会逐项和你确认。</p></div>
          </div>
          <div v-if="sent" class="message user">
            <div><strong>你的需求</strong><p>{{ prompt }}</p></div>
            <div class="avatar">你</div>
          </div>
          <div v-if="sent" class="message ai reveal">
            <div class="avatar">AI</div>
            <div><strong>已形成初步计划</strong><p>这是普通文件取送任务。我建议补充文件尺寸、取件凭证和最晚送达时间，然后再由你确认发布。</p></div>
          </div>
        </div>

        <form class="composer" @submit.prevent="submitPrompt">
          <label for="task-prompt">需求描述</label>
          <textarea id="task-prompt" v-model="prompt" rows="3" />
          <div class="composer-actions">
            <span>不要填写身份证号、银行卡等敏感信息</span>
            <button type="submit" :disabled="planning">{{ planning ? '规划中…' : '让 AI 规划' }} <b>↗</b></button>
          </div>
        </form>
      </article>

      <aside class="draft panel">
        <header class="panel-head">
          <div><span class="index">02</span><h2>任务草稿</h2></div>
          <span class="progress">{{ completion }}%</span>
        </header>

        <div class="draft-body">
          <div class="category">同城取送 <span>低风险</span></div>
          <h3>明日下午代取并递送文件</h3>
          <p class="location">上海 · 徐汇区 → 静安区</p>

          <div class="checklist">
            <button v-for="step in steps" :key="step.label" type="button" @click="step.done = !step.done">
              <span :class="{ done: step.done }">{{ step.done ? '✓' : '' }}</span>{{ step.label }}
            </button>
          </div>

          <div class="reward-card">
            <div><span>AI 建议悬赏</span><strong>¥{{ suggestionMin }}–{{ suggestionMax }}</strong></div>
            <p>约 7.2 km · 预计 65 分钟 · 工作日下午 · {{ suggestionSource }}</p>
            <div class="reward-control">
              <label for="reward">你的悬赏</label>
              <button type="button" @click="increaseReward">¥ {{ reward }} <span>＋5</span></button>
            </div>
          </div>

          <button class="publish" type="button" :disabled="completion < 100 || publishing" @click="openPublishPreview">{{ publishing ? '正在创建草稿…' : '确认草稿并预览发布' }}</button>
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
        <button type="button" @click="loadTasks">刷新任务 <span>↻</span></button>
      </header>

      <p v-if="applicationNotice" class="notice">{{ applicationNotice }}</p>
      <p v-if="sessionError" class="notice warning">{{ sessionError }}</p>
      <div v-if="hallLoading" class="hall-state">正在从 PostgreSQL 读取任务…</div>
      <div v-else-if="hallError" class="hall-state error">
        <strong>任务大厅暂时离线</strong><span>{{ hallError }}</span><button type="button" @click="loadTasks">重新连接</button>
      </div>
      <div v-else-if="tasks.length === 0" class="hall-state">
        <strong>还没有可报名的任务</strong><span>发布第一条任务后，它会出现在这里。</span>
      </div>
      <div v-else class="task-grid">
        <article v-for="(task, index) in tasks" :key="task.id" class="task-card">
          <div class="task-meta"><span>NO. {{ String(index + 1).padStart(2, '0') }}</span><span>{{ task.district }}</span></div>
          <h3>{{ task.title }}</h3>
          <p>{{ task.description }}</p>
          <div class="task-facts">
            <div><small>固定悬赏</small><strong>¥{{ task.reward }}</strong></div>
            <div><small>截止</small><strong>{{ formatDeadline(task.deadline) }}</strong></div>
          </div>
          <div class="owner-trust">
            <span class="mini-avatar">需</span>
            <div><strong>需求方信用待接入</strong><small>已公开评价将在这里展示</small></div>
          </div>
          <div class="task-actions">
            <span>{{ task.applicationCount }} 人已报名</span>
            <div class="task-action-buttons">
              <button v-if="canManageTask(task) && task.applicationCount > 0" type="button" class="secondary-action" @click="viewApplications(task)">{{ viewingApplicationsTaskId === task.id ? '收起报名' : '查看报名' }}</button>
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
    </section>

    <section id="orders" class="orders-section">
      <header class="hall-head"><div><p class="eyebrow">ORDER DESK / 03</p><h2>我的订单</h2><p>查看已选中的服务安排与固定悬赏。</p></div><button type="button" @click="loadOrders">刷新订单 <span>↻</span></button></header>
      <div v-if="ordersLoading" class="hall-state">正在读取订单…</div>
      <div v-else-if="ordersError" class="hall-state error"><strong>订单暂时离线</strong><span>{{ ordersError }}</span><button type="button" @click="loadOrders">重新连接</button></div>
      <div v-else-if="orders.length === 0" class="hall-state"><strong>还没有订单</strong><span>选择报名者后，订单会显示在这里。</span></div>
      <div v-else class="orders-list">
        <article v-for="order in orders" :key="order.id" class="order-row"><div><small>{{ formatDeadline(order.createdAt) }} · {{ order.status }}</small><h3>{{ order.title }}</h3><span>订单号 {{ order.id.slice(0, 8) }}</span></div><strong>¥{{ order.reward }}</strong></article>
      </div>
    </section>

    <footer class="trust-strip">
      <span>固定悬赏</span><span>双向评价</span><span>隐私分级披露</span><span>关键操作需确认</span>
    </footer>
  </main>
</template>
