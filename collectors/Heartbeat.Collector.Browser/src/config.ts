// 扩展配置：仅一项——hub 端口。存 chrome.storage.local，与 Agent 的 AgentConfig.IngestPort 对应。

export interface CollectorConfig {
  port: number
}

/** 与 Agent 侧 AgentConfig.IngestPort 默认值一致（选址理由见彼处注释）。 */
export const DEFAULT_CONFIG: CollectorConfig = { port: 24820 }

const CONFIG_KEY = 'config'

export async function loadConfig(): Promise<CollectorConfig> {
  const got = await chrome.storage.local.get(CONFIG_KEY)
  const stored = got[CONFIG_KEY] as Partial<CollectorConfig> | undefined
  const port = Number(stored?.port)
  return { port: Number.isInteger(port) && port > 0 && port <= 65535 ? port : DEFAULT_CONFIG.port }
}

export async function saveConfig(config: CollectorConfig): Promise<void> {
  await chrome.storage.local.set({ [CONFIG_KEY]: config })
}
