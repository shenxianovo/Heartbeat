export function getPlatformIcon(browser: { userAgent: string; maxTouchPoints: number }) {
  // iPad desktop mode also reports Macintosh, but supports multiple touch points.
  return /Macintosh/.test(browser.userAgent) && browser.maxTouchPoints <= 1 ? 'macos' : 'windows';
}
