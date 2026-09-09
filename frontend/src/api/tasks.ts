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

async function parseResponse<T>(response: Response): Promise<T> {
  if (response.ok) return response.json() as Promise<T>
  const problem = await response.json().catch(() => null) as { detail?: string } | null
  throw new Error(problem?.detail ?? `请求失败（${response.status}）`)
}

export async function listPublishedTasks(): Promise<TaskItem[]> {
  return parseResponse<TaskItem[]>(await fetch('/api/v1/tasks'))
}

export async function applyForTask(taskId: string, workerId: string, note: string): Promise<TaskItem> {
  return parseResponse<TaskItem>(await fetch(`/api/v1/tasks/${taskId}/applications`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ workerId, note }),
  }))
}
