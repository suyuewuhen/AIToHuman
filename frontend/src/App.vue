<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { requestRewardSuggestion } from './api/rewards'
import { applyForTask, listPublishedTasks, listTaskApplications, selectTaskApplication, type TaskApplication, type TaskItem } from './api/tasks'

type Step = { label: string; done: boolean }

const prompt = ref('明天下午帮我去徐汇区取一份文件，再送到静安寺附近。')
const reward = ref(65)
const sent = ref(false)
const planning = ref(false)
const suggestionMin = ref(55)
const suggestionMax = ref(75)
const suggestionSource = ref('演示建议')
const tasks = ref<TaskItem[]>([])
const hallLoading = ref(true)
const hallError = ref('')
const applyingTaskId = ref('')
const applicationNotice = ref('')
const viewingApplicationsTaskId = ref('')
const taskApplications = ref<TaskApplication[]>([])
const applicationsLoading = ref(false)
const selectingApplicationId = ref('')
const demoWorkerId = '20000000-0000-0000-0000-000000000001'
const steps = ref<Step[]>([
  { label: '确认取件地址与联系人', done: true },
  { label: '核对送达时间窗口', done: true },
  { label: '明确文件交接凭证', done: false },
])

const completion = computed(() => Math.round((steps.value.filter((step) => step.done).length / steps.value.length) * 100))

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

async function apply(task: TaskItem) {
  applyingTaskId.value = task.id
  applicationNotice.value = ''
  try {
    await applyForTask(task.id, demoWorkerId, '我已查看任务要求，可以按固定悬赏完成。')
    applicationNotice.value = `已报名「${task.title}」`
    await loadTasks()
  } catch (error) {
    applicationNotice.value = error instanceof Error ? error.message : '报名失败'
  } finally {
    applyingTaskId.value = ''
  }
}

async function viewApplications(task: TaskItem) {
  viewingApplicationsTaskId.value = task.id
  taskApplications.value = []
  applicationsLoading.value = true
  applicationNotice.value = ''
  try {
    taskApplications.value = await listTaskApplications(task.id, task.ownerId)
  } catch (error) {
    applicationNotice.value = error instanceof Error ? error.message : '报名列表加载失败'
    viewingApplicationsTaskId.value = ''
  } finally {
    applicationsLoading.value = false
  }
}

async function selectApplication(task: TaskItem, application: TaskApplication) {
  selectingApplicationId.value = application.id
  applicationNotice.value = ''
  try {
    await selectTaskApplication(task.id, application.id, task.ownerId)
    applicationNotice.value = `已选择报名者，任务「${task.title}」进入已分配状态。`
    viewingApplicationsTaskId.value = ''
    await loadTasks()
  } catch (error) {
    applicationNotice.value = error instanceof Error ? error.message : '选择报名者失败'
  } finally {
    selectingApplicationId.value = ''
  }
}

function formatDeadline(value: string) {
  return new Intl.DateTimeFormat('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }).format(new Date(value))
}

onMounted(loadTasks)
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
      <button class="profile" type="button"><span>曹</span> 需求方 · 试用账户</button>
    </nav>

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

          <button class="publish" type="button" :disabled="completion < 100">确认草稿并预览发布</button>
          <p class="guardrail">服务者按固定悬赏报名，不进行竞价。发布后、分配前你仍可以加价。</p>
        </div>
      </aside>
    </section>

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
              <button v-if="task.applicationCount > 0" type="button" class="secondary-action" @click="viewApplications(task)">{{ viewingApplicationsTaskId === task.id ? '收起报名' : '查看报名' }}</button>
              <button type="button" :disabled="applyingTaskId === task.id" @click="apply(task)">{{ applyingTaskId === task.id ? '提交中…' : '按此悬赏报名' }}</button>
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

    <footer class="trust-strip">
      <span>固定悬赏</span><span>双向评价</span><span>隐私分级披露</span><span>关键操作需确认</span>
    </footer>
  </main>
</template>
