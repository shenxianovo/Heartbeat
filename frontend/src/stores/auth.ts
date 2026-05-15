import { ref, readonly } from 'vue'

const AUTH_URL = 'https://auth.shenxianovo.com'
const TOKEN_KEY = 'access_token'
const USER_ID_KEY = 'user_id'

const token = ref<string | null>(localStorage.getItem(TOKEN_KEY))
const userId = ref<string | null>(localStorage.getItem(USER_ID_KEY))

function setAuth(accessToken: string, uid: string) {
  token.value = accessToken
  userId.value = uid
  localStorage.setItem(TOKEN_KEY, accessToken)
  localStorage.setItem(USER_ID_KEY, uid)
}

function clearAuth() {
  token.value = null
  userId.value = null
  localStorage.removeItem(TOKEN_KEY)
  localStorage.removeItem(USER_ID_KEY)
}

function redirectToLogin() {
  const redirectUrl = window.location.origin + window.location.pathname
  window.location.href = `${AUTH_URL}?redirect=${encodeURIComponent(redirectUrl)}`
}

function handleCallback(): boolean {
  const params = new URLSearchParams(window.location.search)
  const callbackToken = params.get('token')
  const callbackUserId = params.get('userId')

  if (callbackToken && callbackUserId) {
    setAuth(callbackToken, callbackUserId)
    window.history.replaceState({}, document.title, window.location.pathname)
    return true
  }
  return false
}

export const authStore = {
  token: readonly(token),
  userId: readonly(userId),
  get isAuthenticated() { return token.value !== null },
  setAuth,
  clearAuth,
  redirectToLogin,
  handleCallback,
}
