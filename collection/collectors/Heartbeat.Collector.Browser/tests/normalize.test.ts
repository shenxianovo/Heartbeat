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

  it('YouTube watch 的 v 参数区分不同视频', () => {
    const a = identityKeyOf('https://www.youtube.com/watch?v=aaa')
    const b = identityKeyOf('https://www.youtube.com/watch?v=bbb')
    expect(a).toBe('https://www.youtube.com/watch?v=aaa')
    expect(b).toBe('https://www.youtube.com/watch?v=bbb')
  })

  it.each(['youtube.com', 'www.youtube.com', 'm.youtube.com'])('保留 %s 视频身份，忽略追踪参数、播放位置与锚点', host => {
    expect(identityKeyOf(`https://${host}/watch?utm_source=x&t=30&v=AbC_123#details`))
      .toBe(`https://${host}/watch?v=AbC_123`)
  })

  it('规则匹配沿用 host 和尾斜杠规范化，视频 ID 大小写保留', () => {
    expect(identityKeyOf('HTTPS://WWW.YouTube.COM:443/watch/?v=AbC')).toBe('https://www.youtube.com/watch?v=AbC')
    expect(identityKeyOf('https://www.youtube.com/watch?v=abc')).not.toBe(identityKeyOf('https://www.youtube.com/watch?v=AbC'))
  })

  it.each([
    'https://notyoutube.com/watch?v=a',
    'https://youtube.com.example.org/watch?v=a',
    'https://www.youtube.com/results?v=a',
    'https://www.youtube.com/Watch?v=a',
  ])('覆写只匹配指定域名与路径：%s', url => {
    expect(identityKeyOf(url)).toBe(url.split('?')[0])
  })

  it('缺少保留参数不添加 query，参数名大小写不混淆', () => {
    expect(identityKeyOf('https://www.youtube.com/watch?V=a&utm_source=x')).toBe('https://www.youtube.com/watch')
  })

  it('保留参数值经 URL 编解码规范化，重复值不丢失', () => {
    expect(identityKeyOf('https://www.youtube.com/watch?v=%41bc&v=Def&utm_source=x'))
      .toBe('https://www.youtube.com/watch?v=Abc&v=Def')
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
