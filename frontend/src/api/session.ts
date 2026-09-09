export interface DevSession {
  userId: string
  displayName: string
  role: string
  availableRoles: string[]
  isDevelopmentSession: boolean
}

export async function getDevSession(): Promise<DevSession> {
  const response = await fetch('/api/v1/session/dev')
  if (response.ok) return response.json() as Promise<DevSession>
  throw new Error(`开发会话不可用（${response.status}）`)
}
