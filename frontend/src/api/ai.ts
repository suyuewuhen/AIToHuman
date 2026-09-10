import { getAccessToken } from './auth'

export interface AiTaskPlan {
  title: string
  description: string
  district: string
  deadline: string
  acceptanceCriteria: string[]
  suggestedReward: number
  clarifications: string[]
  provider: string
}

export interface AiConversationMessage {
  role: 'user' | 'assistant'
  content: string
}

export interface AiConversationTurn {
  assistantMessage: string
  readyToDraft: boolean
  plan: AiTaskPlan | null
}

type StreamEvent = { event: string; data: string }

export async function continueTaskConversation(
  conversationId: string,
  userId: string,
  message: string,
  onDelta?: (delta: string, accumulated: string) => void,
): Promise<AiConversationTurn> {
  const token = getAccessToken()
  const response = await fetch('/api/v1/ai/plan/stream', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'text/event-stream',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: JSON.stringify({ conversationId, userId, message }),
  })
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { detail?: string } | null
    throw new Error(problem?.detail ?? `AI 对话失败（${response.status}）`)
  }
  if (!response.body) throw new Error('浏览器无法读取 AI 流式响应。')

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  let accumulated = ''
  let completedTurn: AiConversationTurn | null = null

  const consumeEvent = (block: string) => {
    const parsed = parseServerSentEvent(block)
    if (!parsed.data) return
    const payload = JSON.parse(parsed.data) as { text?: string; turn?: AiConversationTurn; detail?: string }
    if (parsed.event === 'delta' && payload.text) {
      accumulated += payload.text
      onDelta?.(payload.text, accumulated)
    } else if (parsed.event === 'restart') {
      // 服务端要重试这一轮：先清空已经显示的半截回复，避免两段内容拼在一起。
      accumulated = ''
      onDelta?.('', '')
    } else if (parsed.event === 'complete' && payload.turn) {
      completedTurn = payload.turn
    } else if (parsed.event === 'error') {
      throw new Error(payload.detail ?? 'AI 对话请求失败。')
    }
  }

  while (true) {
    const { done, value } = await reader.read()
    buffer += decoder.decode(value, { stream: !done })
    const blocks = buffer.split(/\r?\n\r?\n/)
    buffer = blocks.pop() ?? ''
    blocks.forEach(consumeEvent)
    if (done) break
  }
  if (buffer.trim()) consumeEvent(buffer)
  if (!completedTurn) throw new Error('AI 对话意外结束，请重试。')
  return completedTurn
}

function parseServerSentEvent(block: string): StreamEvent {
  let event = 'message'
  const data: string[] = []
  for (const line of block.split(/\r?\n/)) {
    if (line.startsWith('event:')) event = line.slice(6).trim()
    if (line.startsWith('data:')) data.push(line.slice(5).trimStart())
  }
  return { event, data: data.join('\n') }
}
