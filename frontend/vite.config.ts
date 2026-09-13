import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

/**
 * 让运营后台在本地也能按“子路径”访问（`/ops/`）。
 * 线上把运营后台挂到 `/ops/` 时，通常由反向代理做这条改写；本地开发/预览服务器没有 nginx，
 * 所以这里自己加一层，保证“本地怎么试、线上怎么用”一致。
 */
function opsSubpath() {
  const rewrite = (req: { url?: string }) => {
    if (req.url === '/ops' || req.url === '/ops/' || req.url?.startsWith('/ops/?')) req.url = '/ops.html'
    else if (req.url?.startsWith('/ops/#')) req.url = '/ops.html' + req.url.slice(4)
  }

  return {
    name: 'aitohuman-ops-subpath',
    configureServer(server: { middlewares: { use: (handler: (req: { url?: string }, res: unknown, next: () => void) => void) => void } }) {
      server.middlewares.use((req, _res, next) => { rewrite(req); next() })
    },
    configurePreviewServer(server: { middlewares: { use: (handler: (req: { url?: string }, res: unknown, next: () => void) => void) => void } }) {
      server.middlewares.use((req, _res, next) => { rewrite(req); next() })
    },
  }
}

export default defineConfig({
  plugins: [vue(), opsSubpath()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  build: {
    rollupOptions: {
      // 两个入口：主应用（index.html）与运营后台（ops.html，独立页面、独立打包）。
      // 为什么运营后台单独成页，见 src/ops/OpsConsole.vue 顶部说明。
      input: {
        main: fileURLToPath(new URL('./index.html', import.meta.url)),
        ops: fileURLToPath(new URL('./ops.html', import.meta.url)),
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://127.0.0.1:5188',
      '/health': 'http://127.0.0.1:5188',
      '/hubs': { target: 'http://127.0.0.1:5188', ws: true },
    },
  },
})
