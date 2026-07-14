import { ref, readonly } from 'vue'
import {
  AUTH_CLIENT_ID,
  AUTH_URL,
  base64UrlEncode,
  buildAuthorizeUrl,
  createPkcePair,
  decodeJwtPayload,
} from './oidc'

const TOKEN_KEY = 'access_token'
const REFRESH_TOKEN_KEY = 'refresh_token'
const USER_ID_KEY = 'user_id'
const USERNAME_KEY = 'username'

// PKCE 流程中间态（跨跳转存活即可，用 sessionStorage）
const VERIFIER_KEY = 'oidc_verifier'
const STATE_KEY = 'oidc_state'
const RETURN_KEY = 'oidc_return_to'

const token = ref<string | null>(localStorage.getItem(TOKEN_KEY))
const refreshToken = ref<string | null>(localStorage.getItem(REFRESH_TOKEN_KEY))
const userId = ref<string | null>(localStorage.getItem(USER_ID_KEY))
const username = ref<string | null>(localStorage.getItem(USERNAME_KEY))

let refreshPromise: Promise<boolean> | null = null

function setAuth(accessToken: string, uid: string, uname?: string, refresh?: string) {
  token.value = accessToken
  userId.value = uid
  localStorage.setItem(TOKEN_KEY, accessToken)
  localStorage.setItem(USER_ID_KEY, uid)
  if (uname) {
    username.value = uname
    localStorage.setItem(USERNAME_KEY, uname)
  }
  if (refresh) {
    refreshToken.value = refresh
    localStorage.setItem(REFRESH_TOKEN_KEY, refresh)
  }
}

function clearAuth() {
  token.value = null
  refreshToken.value = null
  userId.value = null
  username.value = null
  localStorage.removeItem(TOKEN_KEY)
  localStorage.removeItem(REFRESH_TOKEN_KEY)
  localStorage.removeItem(USER_ID_KEY)
  localStorage.removeItem(USERNAME_KEY)
}

function redirectUri(): string {
  return `${window.location.origin}/callback`
}

async function redirectToLogin() {
  const { verifier, challenge } = await createPkcePair()
  const state = base64UrlEncode(crypto.getRandomValues(new Uint8Array(16)))
  sessionStorage.setItem(VERIFIER_KEY, verifier)
  sessionStorage.setItem(STATE_KEY, state)
  sessionStorage.setItem(RETURN_KEY, window.location.pathname + window.location.search)
  window.location.href = buildAuthorizeUrl({ challenge, state, redirectUri: redirectUri() })
}

async function fetchToken(body: URLSearchParams): Promise<Record<string, unknown> | null> {
  try {
    const res = await fetch(`${AUTH_URL}/connect/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body,
    })
    if (!res.ok) return null
    return await res.json()
  } catch {
    return null
  }
}

/** 用回调 ?code 换令牌。成功返回登录前的路径（供跳回），失败返回 null。 */
async function handleCallback(): Promise<string | null> {
  const params = new URLSearchParams(window.location.search)
  const code = params.get('code')
  const state = params.get('state')
  const verifier = sessionStorage.getItem(VERIFIER_KEY)
  const expectedState = sessionStorage.getItem(STATE_KEY)
  const returnTo = sessionStorage.getItem(RETURN_KEY)
  sessionStorage.removeItem(VERIFIER_KEY)
  sessionStorage.removeItem(STATE_KEY)
  sessionStorage.removeItem(RETURN_KEY)

  if (!code || !state || !verifier || state !== expectedState) return null

  const data = await fetchToken(
    new URLSearchParams({
      grant_type: 'authorization_code',
      code,
      redirect_uri: redirectUri(),
      client_id: AUTH_CLIENT_ID,
      code_verifier: verifier,
    }),
  )
  if (!data) return null

  const claims = decodeJwtPayload(data.id_token as string)
  setAuth(
    data.access_token as string,
    claims.sub as string,
    claims.preferred_username as string | undefined,
    data.refresh_token as string | undefined,
  )
  window.history.replaceState({}, document.title, window.location.pathname)
  return returnTo
}

async function tryRefresh(): Promise<boolean> {
  if (!refreshToken.value) return false

  if (refreshPromise) return refreshPromise

  refreshPromise = (async () => {
    try {
      const data = await fetchToken(
        new URLSearchParams({
          grant_type: 'refresh_token',
          refresh_token: refreshToken.value!,
          client_id: AUTH_CLIENT_ID,
        }),
      )
      if (!data) return false
      // 响应可能轮换 refresh token；缺省时 setAuth 保留旧值
      setAuth(
        data.access_token as string,
        userId.value!,
        undefined,
        (data.refresh_token as string | undefined) ?? undefined,
      )
      return true
    } finally {
      refreshPromise = null
    }
  })()

  return refreshPromise
}

function logout() {
  // 上游无 end-session 端点（grant 有意比 session 长寿，AuthService ADR-020），本地清除即登出
  clearAuth()
  window.location.href = '/'
}

export const authStore = {
  token: readonly(token),
  userId: readonly(userId),
  username: readonly(username),
  get isAuthenticated() { return token.value !== null },
  setAuth,
  clearAuth,
  redirectToLogin,
  handleCallback,
  tryRefresh,
  logout,
}
