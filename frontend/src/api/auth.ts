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

export function getAccessToken(): string | null {
  return localStorage.getItem('aitohuman.accessToken')
}

export function clearAccessToken() {
  localStorage.removeItem('aitohuman.accessToken')
}
