import { describe, expect, it } from 'vitest'
import {
  base64UrlEncode,
  buildAuthorizeUrl,
  computeChallenge,
  createPkcePair,
  decodeJwtPayload,
} from './oidc'

describe('computeChallenge', () => {
  it('匹配 RFC 7636 附录 B 官方向量', async () => {
    const challenge = await computeChallenge('dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk')
    expect(challenge).toBe('E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM')
  })
})

describe('createPkcePair', () => {
  it('verifier 为 43 字符 base64url，challenge 与 verifier 对应', async () => {
    const { verifier, challenge } = await createPkcePair()
    expect(verifier).toMatch(/^[A-Za-z0-9_-]{43}$/)
    expect(challenge).toBe(await computeChallenge(verifier))
  })
})

describe('base64UrlEncode', () => {
  it('无填充且使用 -_ 替代 +/', () => {
    // 0xfb 0xef 0xbf → base64 "+++/" → base64url "---_"
    expect(base64UrlEncode(new Uint8Array([0xfb, 0xef, 0xbf]))).toBe('---_')
    expect(base64UrlEncode(new Uint8Array([0xff]))).not.toContain('=')
  })
})

describe('buildAuthorizeUrl', () => {
  it('包含授权码 + PKCE 流程全部必需参数', () => {
    const url = new URL(
      buildAuthorizeUrl({ challenge: 'CH', state: 'ST', redirectUri: 'http://localhost:3000/callback' }),
    )
    expect(url.pathname).toBe('/connect/authorize')
    expect(url.searchParams.get('client_id')).toBe('heartbeat-web')
    expect(url.searchParams.get('redirect_uri')).toBe('http://localhost:3000/callback')
    expect(url.searchParams.get('response_type')).toBe('code')
    expect(url.searchParams.get('scope')).toBe('openid profile offline_access')
    expect(url.searchParams.get('state')).toBe('ST')
    expect(url.searchParams.get('code_challenge')).toBe('CH')
    expect(url.searchParams.get('code_challenge_method')).toBe('S256')
  })
})

describe('decodeJwtPayload', () => {
  it('解出含 base64url 特殊字符的 payload', () => {
    const claims = { sub: 'a1b2', preferred_username: '甲乙?>~' }
    const payload = btoa(String.fromCharCode(...new TextEncoder().encode(JSON.stringify(claims))))
      .replace(/\+/g, '-')
      .replace(/\//g, '_')
      .replace(/=+$/, '')
    const jwt = `eyJhbGciOiJSUzI1NiJ9.${payload}.sig`
    const decoded = decodeJwtPayload(jwt)
    expect(decoded.sub).toBe('a1b2')
  })
})
