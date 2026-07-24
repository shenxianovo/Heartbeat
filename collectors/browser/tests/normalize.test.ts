import { describe, expect, it } from 'vitest'
import { identityKeyOf, domainOf, siteOf } from '../src/normalize'

describe('identityKeyOf', () => {
  it('掐掉 query 与 fragment', () => {
    expect(identityKeyOf('https://github.com/foo/bar?tab=readme#install')).toBe(
      'https://github.com/foo/bar',
    )
  })

  it('utm 等追踪参数不产生新身份', () => {
    const a = identityKeyOf('https://example.com/post/1?utm_source=x&utm_medium=y')
    const b = identityKeyOf('https://example.com/post/1')
    expect(a).toBe(b)
  })

  it('host 小写化、默认端口剔除（URL.origin 行为），path 大小写保留', () => {
    expect(identityKeyOf('HTTPS://GitHub.COM:443/Foo')).toBe('https://github.com/Foo')
  })

  it('非默认端口保留', () => {
    expect(identityKeyOf('http://localhost:5173/app')).toBe('http://localhost:5173/app')
  })

  it('尾斜杠归一，根路径保留', () => {
    expect(identityKeyOf('https://a.com/docs/')).toBe('https://a.com/docs')
    expect(identityKeyOf('https://a.com/')).toBe('https://a.com/')
  })

  it('本片已知限制：youtube watch 的 v 参数被掐掉（覆写表见 issue 02）', () => {
    const a = identityKeyOf('https://www.youtube.com/watch?v=aaa')
    const b = identityKeyOf('https://www.youtube.com/watch?v=bbb')
    expect(a).toBe(b) // issue 02 落地后此断言应反转
  })

  it('自定义 scheme（origin 为 null）退化为掐 query/fragment 的原串', () => {
    expect(identityKeyOf('edge://newtab/?param=1')).toBe('edge://newtab/')
  })

  it('非法 URL 原样返回', () => {
    expect(identityKeyOf('not a url')).toBe('not a url')
  })
})

describe('domainOf', () => {
  it('提取 hostname', () => {
    expect(domainOf('https://www.youtube.com/watch?v=x')).toBe('www.youtube.com')
  })

  it('非法 URL 返回空串', () => {
    expect(domainOf('nope')).toBe('')
  })
})

describe('siteOf（可注册域,深度表 v2 的 site 读数,ADR-030 §5）', () => {
  it('www 折叠进主站', () => {
    expect(siteOf('https://www.youtube.com/watch')).toBe('youtube.com')
    expect(siteOf('https://youtube.com/watch')).toBe('youtube.com')
  })

  it('子域归可注册域（同站不同子域 = 一个 site）', () => {
    expect(siteOf('https://blog.shenxianovo.com/post')).toBe('shenxianovo.com')
    expect(siteOf('https://heartbeat.shenxianovo.com/dashboard')).toBe('shenxianovo.com')
  })

  it('多段公共后缀取末三段', () => {
    expect(siteOf('https://www.tsinghua.edu.cn/a')).toBe('tsinghua.edu.cn')
    expect(siteOf('https://news.bbc.co.uk/x')).toBe('bbc.co.uk')
  })

  it('IP 与 localhost 原样即站（无注册域概念）', () => {
    expect(siteOf('http://127.0.0.1:5173/app')).toBe('127.0.0.1')
    expect(siteOf('http://localhost:8080/dev')).toBe('localhost')
    expect(siteOf('http://[::1]:3000/')).toBe('[::1]')
  })

  it('非法 URL 返回空串（读数缺席,段挂最深可用读数）', () => {
    expect(siteOf('not a url')).toBe('')
  })
})
