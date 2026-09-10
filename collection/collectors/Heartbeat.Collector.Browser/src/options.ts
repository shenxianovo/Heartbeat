import { isDevelopment, loadDevelopmentBinding } from './connection'
// 选项页：读写唯一配置项（hub 端口）。

import { DEFAULT_CONFIG, loadConfig, saveConfig } from './config'

const portInput = document.getElementById('port') as HTMLInputElement
const saveButton = document.getElementById('save') as HTMLButtonElement
const status = document.getElementById('status') as HTMLDivElement

if (isDevelopment()) {
  portInput.disabled = true
  saveButton.hidden = true
  document.querySelector('.hint')!.textContent = '开发扩展只连接此工作区的 Desktop；构建更新后约 30 秒自动 Reload，请勿卸载。'
  void loadDevelopmentBinding().then(binding => {
    portInput.value = String(binding.port)
    status.textContent = `开发 Profile：${binding.profileId}`
  }).catch(() => { status.textContent = '开发绑定不可用或正在等待自动 Reload；持续等待时，请检查开发命令并手动 Reload 一次。' })
} else {
  void loadConfig().then(c => { portInput.value = String(c.port) })
}

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
