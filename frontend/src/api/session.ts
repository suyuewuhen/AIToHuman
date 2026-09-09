export interface DevSession {
  userId: string
  displayName: string
  role: string
  availableRoles: string[]
  isDevelopmentSession: boolean
}

export async function getDevSession(): Promise<DevSession> {
  try {
    const response = await fetch('/api/v1/session/dev')
    if (response.ok) return response.json() as Promise<DevSession>
    if (response.status === 404) throw new Error('开发会话仅在 ASP.NET Core Development 环境可用。')
    throw new Error(`开发会话接口返回 HTTP ${response.status}`)
  } catch (error) {
    if (error instanceof TypeError) {
      throw new Error('无法连接后端 API（127.0.0.1:5188）。请先启动 dotnet run --project backend/AIToHuman.Api。')
    }
    throw error
  }
}
