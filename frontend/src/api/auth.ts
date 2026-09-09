export interface AuthResponse {
  userId: string
  email: string
  displayName: string
  role: string
  accessToken: string
  expiresInSeconds: number
}

export interface LoginRequest {
  email: string
  password: string
}

export interface RegisterRequest extends LoginRequest {
  displayName: string
  role: 'owner' | 'worker'
}

export type ActiveRole = 'owner' | 'worker'

async function parseAuth(response: Response): Promise<AuthResponse> {
  if (response.ok) return response.json() as Promise<AuthResponse>
  const problem = await response.json().catch(() => null) as { detail?: string } | null
  throw new Error(problem?.detail ?? `认证请求失败（${response.status}）`)
}

export async function login(request: LoginRequest): Promise<AuthResponse> {
  const response = await fetch('/api/v1/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  })
  const auth = await parseAuth(response)
  localStorage.setItem('aitohuman.accessToken', auth.accessToken)
  return auth
}

export async function register(request: RegisterRequest): Promise<AuthResponse> {
  const response = await fetch('/api/v1/auth/register', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  })
  const auth = await parseAuth(response)
  localStorage.setItem('aitohuman.accessToken', auth.accessToken)
  return auth
}

export async function switchRole(role: ActiveRole): Promise<AuthResponse> {
  const token = getAccessToken()
  if (!token) throw new Error('未登录')
  const response = await fetch('/api/v1/auth/switch-role', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: JSON.stringify({ role }),
  })
  const auth = await parseAuth(response)
  localStorage.setItem('aitohuman.accessToken', auth.accessToken)
  return auth
}

export async function getCurrentUser(): Promise<Pick<AuthResponse, 'userId' | 'email' | 'displayName' | 'role'>> {
  const token = getAccessToken()
  if (!token) throw new Error('未登录')
  const response = await fetch('/api/v1/auth/me', { headers: { Authorization: `Bearer ${token}` } })
  if (!response.ok) {
    clearAccessToken()
    throw new Error('登录已过期，请重新登录。')
  }
  return response.json() as Promise<Pick<AuthResponse, 'userId' | 'email' | 'displayName' | 'role'>>
}

export function getAccessToken(): string | null {
  return localStorage.getItem('aitohuman.accessToken')
}

export function clearAccessToken() {
  localStorage.removeItem('aitohuman.accessToken')
}
