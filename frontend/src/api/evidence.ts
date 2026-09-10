import { getAccessToken } from './auth'

export interface EvidenceItem {
  id: string
  orderId: string
  uploadedBy: string
  fileName: string
  contentType: string
  sizeBytes: number
  contentHash: string
  scanStatus: string
  createdAt: string
  scannedAt: string | null
  isDownloadable: boolean
}

export const allowedEvidenceTypes = ['image/jpeg', 'image/png', 'image/webp', 'application/pdf']

/** 与服务端保持一致的 5 MB 上限，用于在选择文件时提前给出提示。 */
export const maxEvidenceBytes = 5 * 1024 * 1024

async function parseResponse<T>(response: Response): Promise<T> {
  if (response.ok) return response.json() as Promise<T>
  const problem = await response.json().catch(() => null) as { detail?: string; title?: string } | null
  throw new Error(problem?.detail ?? problem?.title ?? `请求失败（${response.status}）`)
}

function authHeaders(): HeadersInit {
  const token = getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

export async function listOrderEvidence(orderId: string, userId: string): Promise<EvidenceItem[]> {
  return parseResponse<EvidenceItem[]>(await fetch(`/api/v1/orders/${orderId}/evidence?userId=${encodeURIComponent(userId)}`, { headers: authHeaders() }))
}

export async function uploadOrderEvidence(orderId: string, userId: string, file: File): Promise<EvidenceItem> {
  const form = new FormData()
  form.append('file', file)
  return parseResponse<EvidenceItem>(await fetch(`/api/v1/orders/${orderId}/evidence?userId=${encodeURIComponent(userId)}`, {
    method: 'POST',
    headers: authHeaders(),
    body: form,
  }))
}

/** 下载走带鉴权头的请求，再用 Blob URL 触发保存，避免把令牌放进 URL。 */
export async function downloadEvidence(item: EvidenceItem, userId: string): Promise<void> {
  const response = await fetch(`/api/v1/evidence/${item.id}/content?userId=${encodeURIComponent(userId)}`, { headers: authHeaders() })
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { detail?: string } | null
    throw new Error(problem?.detail ?? `下载失败（${response.status}）`)
  }
  const url = URL.createObjectURL(await response.blob())
  const link = document.createElement('a')
  link.href = url
  link.download = item.fileName
  link.click()
  URL.revokeObjectURL(url)
}
