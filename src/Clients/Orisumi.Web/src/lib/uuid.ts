/**
 * UUIDv7：48 位 Unix 毫秒时间戳 + 版本/变体位 + 随机位。
 * 后端要求 ID 为 UUIDv7（pattern 8-4-7-[89ab]-4-12），保存实体时由客户端生成。
 */
const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

function randomBytes16(): Uint8Array {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return bytes;
}

export function uuidv7(now: number = Date.now()): string {
  const bytes = randomBytes16();
  // 前 6 字节：48 位 big-endian 毫秒时间戳
  bytes[0] = Number((BigInt(now) >> 40n) & 0xffn);
  bytes[1] = Number((BigInt(now) >> 32n) & 0xffn);
  bytes[2] = Number((BigInt(now) >> 24n) & 0xffn);
  bytes[3] = Number((BigInt(now) >> 16n) & 0xffn);
  bytes[4] = Number((BigInt(now) >> 8n) & 0xffn);
  bytes[5] = Number(BigInt(now) & 0xffn);
  // version 7 / variant 10xx
  bytes[6] = (bytes[6]! & 0x0f) | 0x70;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;

  const hex = [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export function isUuidv7(value: string): boolean {
  return UUID_RE.test(value);
}
