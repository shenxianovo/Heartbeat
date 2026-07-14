// OIDC 授权码 + PKCE 纯函数助手（无状态，便于单测）
// 上游为 OpenIddict OIDC Provider，见 AuthService ADR-016

export const AUTH_URL: string =
  (import.meta.env.VITE_AUTH_URL as string | undefined) ?? 'https://auth.shenxianovo.com'
export const AUTH_CLIENT_ID: string =
  (import.meta.env.VITE_AUTH_CLIENT_ID as string | undefined) ?? 'heartbeat-web'

export function base64UrlEncode(bytes: Uint8Array): string {
  let s = ''
  for (const b of bytes) s += String.fromCharCode(b)
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

export async function computeChallenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier))
  return base64UrlEncode(new Uint8Array(digest))
}

export async function createPkcePair(): Promise<{ verifier: string; challenge: string }> {
  const verifier = base64UrlEncode(crypto.getRandomValues(new Uint8Array(32)))
  return { verifier, challenge: await computeChallenge(verifier) }
}

export function buildAuthorizeUrl(p: {
  challenge: string
  state: string
  redirectUri: string
}): string {
  const q = new URLSearchParams({
    client_id: AUTH_CLIENT_ID,
    redirect_uri: p.redirectUri,
    response_type: 'code',
    // offline_access 换取 refresh token（客户端注册的 scopes 里不含它，属 grant 权限）
    scope: 'openid profile offline_access',
    state: p.state,
    code_challenge: p.challenge,
    code_challenge_method: 'S256',
  })
  return `${AUTH_URL}/connect/authorize?${q}`
}

// 免验签解 JWT payload——仅用于 token 端点经 TLS 直接返回的 id_token（OIDC §3.1.3.7）
export function decodeJwtPayload(jwt: string): Record<string, unknown> {
  const payload = jwt.split('.')[1]
  const binary = atob(payload.replace(/-/g, '+').replace(/_/g, '/'))
  // atob 产出 latin1 字符串，非 ASCII 声明（如中文用户名）需按 UTF-8 还原
  const bytes = Uint8Array.from(binary, (c) => c.charCodeAt(0))
  return JSON.parse(new TextDecoder().decode(bytes))
}
