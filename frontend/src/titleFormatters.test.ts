import { describe, expect, it } from 'vitest'
import { formatTitle } from './titleFormatters'

describe('formatTitle: product key', () => {
  it('按跨平台产品 Key 选择 VS Code formatter', () => {
    expect(formatTitle('vscode', 'main.cs - heartbeat - Visual Studio Code')).toEqual({
      primary: 'main.cs',
      secondary: 'heartbeat',
    })
  })
})

describe('formatTitle: msedge', () => {
  it('削掉 " 和另外 N 个页面" 后缀，与无后缀标题合并为同一 primary', () => {
    const a = formatTitle('msedge', '补药视奸我ヽ（≧□≦）ノ 和另外 2 个页面')
    const b = formatTitle('msedge', '补药视奸我ヽ（≧□≦）ノ')
    expect(a.primary).toBe('补药视奸我ヽ（≧□≦）ノ')
    expect(a.primary).toBe(b.primary)
  })

  it('英文后缀 "and N more pages" 同样削掉', () => {
    expect(formatTitle('msedge', 'GitHub and 3 more pages').primary).toBe('GitHub')
    expect(formatTitle('msedge', 'GitHub and 1 more page').primary).toBe('GitHub')
  })

  it('削掉尾段 "- Microsoft Edge" 与个人账户段', () => {
    expect(formatTitle('msedge', 'GitHub - Microsoft​ Edge'.replace('​', '')).primary).toBe('GitHub')
  })

  it('后缀数字出现在页面名中间不误伤', () => {
    expect(formatTitle('msedge', 'Top 10 个页面设计').primary).toBe('Top 10 个页面设计')
  })

  it('整个标题只有后缀时回退 Edge', () => {
    expect(formatTitle('msedge', '和另外 5 个页面').primary).toBe('Edge')
  })
})
