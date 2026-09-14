const DEFAULT_RETURN_PATH = "/";

export function safeReturnPath(value: string | null | undefined): string {
  if (!value || /[\\\u0000-\u001f\u007f]/.test(value)) {
    return DEFAULT_RETURN_PATH;
  }

  try {
    const base = new URL("https://heartbeat.local/");
    const target = new URL(value, base);
    if (target.origin !== base.origin || !value.startsWith("/")) return DEFAULT_RETURN_PATH;
    return `${target.pathname}${target.search}${target.hash}`;
  } catch {
    return DEFAULT_RETURN_PATH;
  }
}
