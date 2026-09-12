import { getAccessToken } from './auth'

export type SettingKind = 'String' | 'Bool' | 'Int' | 'Url' | 'Choice'
export type SettingSource = 'database' | 'configuration' | 'default'

/** 运营后台看到的一条配置。机密项的 value 是掩码（例如 ****1234），明文不会下发到浏览器。 */
export interface AdminSetting {
  key: string
  category: string
  displayName: string
  description: string
  kind: SettingKind
  isSecret: boolean
  value: string
  defaultValue: string
  configurationKey: string
  allowedValues: string[]
  source: SettingSource
  hasOverride: boolean
  overrideVersion: number | null
  updatedBy: string | null
  updatedAt: string | null
  fingerprint: string
}

export interface SettingTestResult {
  key: string
  ok: boolean
  message: string
}

export interface SettingAudit {
  id: string
  key: string
  action: string
  oldValue: string
  newValue: string
  actorId: string
  occurredAt: string
}

async function parseResponse<T>(response: Response): Promise<T> {
  if (response.ok) return response.json() as Promise<T>
  const problem = await response.json().catch(() => null) as { detail?: string; title?: string } | null
  throw new Error(problem?.detail ?? problem?.title ?? `请求失败（${response.status}）`)
}

function authHeaders(): HeadersInit {
  const token = getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

function jsonHeaders(): HeadersInit {
  return { 'Content-Type': 'application/json', ...authHeaders() }
}

export async function listSettings(): Promise<AdminSetting[]> {
  return parseResponse<AdminSetting[]>(await fetch('/api/v1/admin/settings', { headers: authHeaders() }))
}

/**
 * 写入覆盖值。expectedVersion 来自上一次读取（没有覆盖时是 null，服务端按新增处理）；
 * 版本对不上服务端会返回 409，提示“已被其他人修改”。
 */
export async function updateSetting(key: string, value: string, expectedVersion: number | null): Promise<AdminSetting> {
  return parseResponse<AdminSetting>(await fetch(`/api/v1/admin/settings/${encodeURIComponent(key)}`, {
    method: 'PUT',
    headers: jsonHeaders(),
    body: JSON.stringify({ value, expectedVersion }),
  }))
}

/** 删除覆盖，恢复环境变量或代码默认值。 */
export async function resetSetting(key: string): Promise<AdminSetting> {
  return parseResponse<AdminSetting>(await fetch(`/api/v1/admin/settings/${encodeURIComponent(key)}`, {
    method: 'DELETE',
    headers: authHeaders(),
  }))
}

/** 只读自检：本机目录是否可写、模型服务与扫描服务是否可达。 */
export async function testSetting(key: string): Promise<SettingTestResult> {
  return parseResponse<SettingTestResult>(await fetch(`/api/v1/admin/settings/${encodeURIComponent(key)}/test`, {
    method: 'POST',
    headers: authHeaders(),
  }))
}

export async function listSettingAudits(limit = 20): Promise<SettingAudit[]> {
  return parseResponse<SettingAudit[]>(await fetch(`/api/v1/admin/settings/audits?limit=${limit}`, { headers: authHeaders() }))
}

export function settingSourceLabel(source: SettingSource): string {
  if (source === 'database') return '后台已改'
  if (source === 'configuration') return '部署配置'
  return '默认值'
}

export function settingKindHint(kind: SettingKind): string {
  if (kind === 'Bool') return 'true / false'
  if (kind === 'Int') return '整数'
  if (kind === 'Url') return 'http(s) 地址，留空表示停用'
  if (kind === 'Choice') return '从下拉中选择'
  return '文本'
}

/** 常见取值的友好名称，让运营不用记枚举值。 */
export function settingChoiceLabel(value: string): string {
  const labels: Record<string, string> = {
    volcengine: '火山引擎 Ark',
    'openai-compatible': 'OpenAI 兼容服务',
    local: '本机目录（开发/试点）',
    s3: 'S3 兼容对象存储',
    none: '未接入（直接放行）',
    http: '调用外部扫描服务',
    closed: 'closed：不可用时保持待扫描、不丢文件',
    open: 'open：不可用时直接放行（仅开发）',
    true: 'true',
    false: 'false',
  }
  return labels[value] ?? value
}
