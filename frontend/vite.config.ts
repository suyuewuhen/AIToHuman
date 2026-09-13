import { copyFileSync, existsSync, mkdirSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

/**
 * 运营后台的“子路径”支持，两件事：
 *
 * 1）开发/预览服务器：把 `/ops` 与 `/ops/` 改写到 `/ops.html`。
 *    线上把运营后台挂到 `/ops/` 时通常由反向代理做这条改写，本地没有 nginx，所以这里自己加一层，
 *    保证“本地怎么访问、线上就怎么用”。
 * 2）构建产物：除了 `dist/ops.html`，再拷一份 `dist/ops/index.html`。
 *    这样任何静态托管（会按目录找 index.html）都能直接用 `/ops/` 访问运营后台，
 *    不必为了子路径额外写一条 try_files；两个文件指向同一份 `/assets/ops-*.js`，不存在两份逻辑。
 */
function opsSubpath() {
  let outDir = 'dist'

  const rewrite = (req: { url?: string }) => {
    if (req.url === '/ops' || req.url === '/ops/' || req.url?.startsWith('/ops/?')) req.url = '/ops.html'
    else if (req.url?.startsWith('/ops/#')) req.url = '/ops.html' + req.url.slice(4)
  }

  const mount = (server: { middlewares: { use: (handler: (req: { url?: string }, res: unknown, next: () => void) => void) => void } }) => {
    server.middlewares.use((req, _res, next) => { rewrite(req); next() })
  }

  return {
    name: 'aitohuman-ops-subpath',
    configResolved(config: { build: { outDir: string } }) { outDir = config.build.outDir },
    configureServer: mount,
    configurePreviewServer: mount,
    closeBundle() {
      const source = resolve(outDir, 'ops.html')
      if (!existsSync(source)) return

      const target = resolve(outDir, 'ops', 'index.html')
      mkdirSync(dirname(target), { recursive: true })
      copyFileSync(source, target)
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
      // 开发时前端与 API 同源（都从 5173 出去），所以不需要 CORS、也不需要配 VITE_API_BASE_URL。
      '/api': 'http://127.0.0.1:5188',
      '/health': 'http://127.0.0.1:5188',
      '/hubs': { target: 'http://127.0.0.1:5188', ws: true },
    },
  },
})
