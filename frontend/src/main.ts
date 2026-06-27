import { createApp } from 'vue'
import App from './App.vue'
import router from './router'
import './style.css'

async function bootstrap() {
  if (import.meta.env.VITE_USE_MOCK === 'true') {
    const { worker } = await import('./mocks/browser')
    await worker.start({ onUnhandledRequest: 'bypass' })
  }
  createApp(App).use(router).mount('#app')
}

bootstrap()
