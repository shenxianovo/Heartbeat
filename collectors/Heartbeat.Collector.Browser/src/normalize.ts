// IdentityKey 规范化：origin + pathname，掐掉 query/fragment（utm、时间戳、锚点是假碎片主源）。
// 完整原始 URL 始终随段存入 Attributes——判据可有损，原始数据无损（ADR-012 原则）。
// per-domain 覆写表（"query 即身份"的站点，如 youtube.com/watch 需保留 v）见 issue 02，本片不含。

/** 规范化 URL 为续接判据。非法 URL 原样返回（判据退化但不丢数据）。 */
export function identityKeyOf(rawUrl: string): string {
  let u: URL
  try {
    u = new URL(rawUrl)
  } catch {
    return rawUrl
  }

  // 自定义 scheme（edge://、about: 等）origin 为 "null"：退化为掐 query/fragment 的原串。
  if (u.origin === 'null') {
    return u.href.split('#')[0].split('?')[0]
  }

  // 尾斜杠归一：/docs/ 与 /docs 同一活动；根路径 "/" 保留。
  const path =
    u.pathname !== '/' && u.pathname.endsWith('/')
      ? u.pathname.slice(0, -1)
      : u.pathname

  // URL.origin 已做 host 小写化与默认端口剔除。
  return u.origin + path
}

/** 提取 hostname 供 Attributes.domain（回放按域名聚合用）。 */
export function domainOf(rawUrl: string): string {
  try {
    return new URL(rawUrl).hostname
  } catch {
    return ''
  }
}

// 常见多段公共后缀（eTLD+1 近似,ADR-030 §5）:命中时 site 取末三段。
// 刻意不引入完整 PSL(数百 KB 名单换来的精度对个人采集器不值);漏网的多段后缀
// 会把 site 算粗一档(如 example.co.xx → co.xx),被咬再补名单——错向安全侧。
const MULTI_PART_SUFFIXES = new Set([
  'com.cn', 'net.cn', 'org.cn', 'gov.cn', 'edu.cn', 'ac.cn',
  'co.uk', 'org.uk', 'ac.uk', 'gov.uk',
  'co.jp', 'ne.jp', 'or.jp', 'ac.jp', 'go.jp',
  'com.tw', 'org.tw', 'edu.tw',
  'com.hk', 'org.hk', 'edu.hk',
  'com.au', 'net.au', 'org.au', 'edu.au',
  'co.kr', 'or.kr', 'ac.kr',
  'com.br', 'org.br',
  'co.in', 'org.in',
  'com.sg', 'edu.sg',
])

/**
 * 可注册域（site 读数,ADR-030 §5）:browser 深度表 v2 的 L1 值。
 * eTLD+1 近似——末两段,命中多段公共后缀名单时末三段;www 前缀折叠进主站
 * （www.example.com 与 example.com 同站）。IP / localhost / 单标签主机原样返回;
 * 非法 URL 返回空串（该读数缺席,段挂最深可用读数,服务端不造假值）。
 */
export function siteOf(rawUrl: string): string {
  let host: string
  try {
    host = new URL(rawUrl).hostname
  } catch {
    return ''
  }
  if (host.length === 0) return ''

  // IPv6 字面量([::1])、IPv4、单标签主机(localhost / 内网机名):无注册域概念,原样即站。
  if (host.startsWith('[')) return host
  if (/^\d{1,3}(\.\d{1,3}){3}$/.test(host)) return host

  const labels = host.split('.')
  if (labels.length === 1) return host

  const lastTwo = labels.slice(-2).join('.')
  if (labels.length >= 3 && MULTI_PART_SUFFIXES.has(lastTwo)) return labels.slice(-3).join('.')
  return lastTwo
}
