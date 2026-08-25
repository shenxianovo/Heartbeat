const DEFAULT_CONFIG = { port: 24820 };
const CONFIG_KEY = "config";
async function loadConfig() {
  const got = await chrome.storage.local.get(CONFIG_KEY);
  const stored = got[CONFIG_KEY];
  const port = Number(stored?.port);
  return { port: Number.isInteger(port) && port > 0 && port <= 65535 ? port : DEFAULT_CONFIG.port };
}
async function saveConfig(config) {
  await chrome.storage.local.set({ [CONFIG_KEY]: config });
}
export {
  DEFAULT_CONFIG as D,
  loadConfig as l,
  saveConfig as s
};
