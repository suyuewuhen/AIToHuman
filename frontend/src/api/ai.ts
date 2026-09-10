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

export async function planTask(prompt: string): Promise<AiTaskPlan> {
  const response = await fetch('/api/v1/ai/plan', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ prompt }) })
  if (!response.ok) { const problem = await response.json().catch(() => null) as { detail?: string } | null; throw new Error(problem?.detail ?? `AI 规划失败（${response.status}）`) }
  return response.json() as Promise<AiTaskPlan>
}
