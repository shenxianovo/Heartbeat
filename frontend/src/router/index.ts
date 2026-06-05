import { createRouter, createWebHistory } from 'vue-router'
import { authStore } from '../stores/auth'

const RESERVED_ROUTES = ['settings', 'callback']

const router = createRouter({
  history: createWebHistory('/'),
  routes: [
    {
      path: '/',
      redirect: '/shenxianovo',
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
      path: '/:username',
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

export { RESERVED_ROUTES }
export default router
