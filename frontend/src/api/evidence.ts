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
  /** 已经尝试过几次扫描（含后台自动重试）。 */
  scanAttempts: number
  /** 最近一次扫描的说明：通过、拒绝原因，或“扫描服务不可用”。 */
  lastScanNote: string | null
  /** 一直是待扫描但自动重试已用尽：需要人工处理。 */
  scanExhausted: boolean
  /** 当前存储是否支持短时直连下载地址；false 时走鉴权后的 /content 流式下载。 */
  presignedDownloadAvailable: boolean
  /** 上传时被剥离的元数据（例如 EXIF/XMP、PNG 文本块）；为空表示没剥或不需要剥。 */
  metadataRemoved: string | null
}

/** 短时直连下载地址；有效期由服务端运营配置决定，过期后需要重新申请。 */
export interface EvidenceDownloadUrl {
  url: string
  expiresAt: string
}

export const allowedEvidenceTypes = ['image/jpeg', 'image/png', 'image/webp', 'application/pdf']

/**
 * 选择文件时的提示上限。服务端实际上限来自运营配置（默认 5 MB，可在硬上限内调整），
 * 这里只是提前给用户一个合理提示，真正的判断以服务端返回为准。
 */
export const maxEvidenceBytes = 5 * 1024 * 1024

/** 服务端硬上限：无论运营怎么配都不会超过，浏览器侧用它挡掉明显过大的文件。 */
export const absoluteMaxEvidenceBytes = 25 * 1024 * 1024

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

/** 申请短时直连下载地址；只有对象存储支持，本机目录会返回可读错误。 */
export async function getEvidenceDownloadUrl(item: EvidenceItem, userId: string): Promise<EvidenceDownloadUrl> {
  return parseResponse<EvidenceDownloadUrl>(await fetch(`/api/v1/evidence/${item.id}/download-url?userId=${encodeURIComponent(userId)}`, { headers: authHeaders() }))
}

/**
 * 下载凭证：对象存储可用时直接用服务端签发的短时地址（浏览器自己去取字节），
 * 否则回退到带鉴权头的流式下载再用 Blob URL 保存。
 */
export async function downloadEvidence(item: EvidenceItem, userId: string): Promise<void> {
  if (item.presignedDownloadAvailable) {
    const { url } = await getEvidenceDownloadUrl(item, userId)
    const link = document.createElement('a')
    link.href = url
    link.rel = 'noopener'
    // 下载名由地址里的 response-content-disposition 决定（该参数也参与签名）。
    link.click()
    return
  }

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
