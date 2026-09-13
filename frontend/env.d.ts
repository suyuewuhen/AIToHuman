/// <reference types="vite/client" />

/**
 * 部署相关的构建期开关。默认留空表示同源部署（前端与 API 走同一个域名 + 反向代理）；
 * 把运营后台或整个前端放到别的域名/子域时，在构建前设置这些变量，详见 src/api/base.ts。
 */
interface ImportMetaEnv {
  /** API 基地址，例如 `https://api.example.com`；留空 = 同源相对路径。 */
  readonly VITE_API_BASE_URL?: string
  /** SignalR Hub 基地址；留空时跟随 VITE_API_BASE_URL。 */
  readonly VITE_HUB_BASE_URL?: string
  /** 运营后台里“返回任务工作台”的目标地址，例如 `https://app.example.com/`。 */
  readonly VITE_APP_HOME_URL?: string
  /** 主应用里“运营后台”入口的目标地址，例如 `https://ops.example.com/`。 */
  readonly VITE_OPS_HOME_URL?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
