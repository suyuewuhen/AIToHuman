/**
 * 前端与后端通信的地址口子，两个入口（主应用与运营后台）共用。
 *
 * 默认全部走**相对路径**：开发时 Vite 把 `/api` 与 `/hubs` 代理到本机后端，部署时由同一个域名的
 * 反向代理转发，因此默认不需要任何配置。
 *
 * 把运营后台（或整个前端）放到自己的域名 / 子域、与 API 不同源时，再通过构建期环境变量指定绝对地址：
 *
 *   VITE_API_BASE_URL=https://api.example.com     API 基地址（留空 = 同源相对路径）
 *   VITE_HUB_BASE_URL=https://api.example.com     SignalR Hub 基地址（默认跟 API 一致）
 *   VITE_APP_HOME_URL=https://app.example.com/    运营后台里“返回任务工作台”的目标
 *   VITE_OPS_HOME_URL=https://ops.example.com/    主应用里“运营后台”入口的目标
 *
 * 跨域时后端必须在部署配置里放行来源（否则浏览器会拦掉请求）：
 *   Cors__AllowedOrigins=https://ops.example.com,https://app.example.com
 */
const trimTrailingSlash = (value: string) => value.replace(/\/+$/, '')

/** API 基地址；空字符串表示同源（相对路径）。 */
export const API_BASE_URL = trimTrailingSlash(import.meta.env.VITE_API_BASE_URL ?? '')

/** SignalR Hub 基地址；默认与 API 同源。 */
export const HUB_BASE_URL = trimTrailingSlash(import.meta.env.VITE_HUB_BASE_URL ?? API_BASE_URL)

/** 把 `/api/v1/...` 之类的路径拼成完整地址。 */
export function apiUrl(path: string): string {
  return `${API_BASE_URL}${path}`
}

/** 把 `/hubs/...` 之类的路径拼成完整地址。 */
export function hubUrl(path: string): string {
  return `${HUB_BASE_URL}${path}`
}

/**
 * 所有 API 请求都从这里出去：跨域部署时自动补上基地址，同源部署时行为与直接 fetch 完全一致
 * （都是相对路径）。集中在一处是为了避免每个接口各拼一次、漏掉一处就变成“本地能用、线上 404”。
 */
export function apiFetch(path: string, init?: RequestInit): Promise<Response> {
  return fetch(apiUrl(path), init)
}

/** 运营后台里“返回任务工作台”的目标地址。 */
export const APP_HOME_URL = import.meta.env.VITE_APP_HOME_URL ?? '/'

/** 主应用里“运营后台”入口的目标地址。 */
export const OPS_HOME_URL = import.meta.env.VITE_OPS_HOME_URL ?? '/ops.html'
