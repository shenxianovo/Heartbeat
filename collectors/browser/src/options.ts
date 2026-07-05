// 选项页：读写唯一配置项（hub 端口）。

import { DEFAULT_CONFIG, loadConfig, saveConfig } from './config'

const portInput = document.getElementById('port') as HTMLInputElement
const saveButton = document.getElementById('save') as HTMLButtonElement
const status = document.getElementById('status') as HTMLDivElement

void loadConfig().then((c) => {
  portInput.value = String(c.port)
})

saveButton.addEventListener('click', () => {
  const port = Number(portInput.value)
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    status.textContent = '端口无效（1–65535）'
    return
  }
  void saveConfig({ port }).then(() => {
    status.textContent = `已保存（默认 ${DEFAULT_CONFIG.port}）`
  })
})
