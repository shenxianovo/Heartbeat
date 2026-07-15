import { createRouter, createWebHistory } from 'vue-router'
import { authStore } from '../stores/auth'

// 用户看板挂在 /u/ 前缀下（ADR-025）：顶层系统路由（/、/settings、/callback、
// 未来的 /pricing 等）与用户名空间永不冲突，无需维护保留字名单。
const router = createRouter({
  history: createWebHistory('/'),
  routes: [
    {
      path: '/',
      component: () => import('../views/HomeView.vue'),
    },
    {
      path: '/callback',
      component: () => import('../views/LoginCallback.vue'),
    },
    {
      path: '/settings',
      component: () => import('../views/SettingsView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/u/:username',
      component: () => import('../views/ProfileView.vue'),
    },
  ],
})

router.beforeEach((to) => {
  if (to.meta.requiresAuth && !authStore.isAuthenticated) {
    authStore.redirectToLogin()
    return false
  }
})

export default router
