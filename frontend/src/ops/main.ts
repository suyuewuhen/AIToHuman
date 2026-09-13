import { createApp } from 'vue'
import OpsConsole from './OpsConsole.vue'
import '../styles.css'

// 运营后台是独立入口（ops.html）：与主应用共用样式与 API 客户端，但分开打包、分开访问。
createApp(OpsConsole).mount('#app')
