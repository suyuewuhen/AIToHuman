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

export async function listPublishedTasks(): Promise<TaskItem[]> {
  return parseResponse<TaskItem[]>(await fetch('/api/v1/tasks', { headers: authHeaders() }))
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

export async function listMyOrders(userId: string): Promise<OrderItem[]> {
  return parseResponse<OrderItem[]>(await fetch(`/api/v1/orders?userId=${encodeURIComponent(userId)}`, { headers: authHeaders() }))
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
