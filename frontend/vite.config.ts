import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
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
