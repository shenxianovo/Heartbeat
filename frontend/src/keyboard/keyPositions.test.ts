import { describe, expect, it } from 'vitest'
import { KEYBOARD_ROWS } from './keyPositions'

describe('heartbeat-key-position-v1 keyboard layout', () => {
  const byLabel = new Map(KEYBOARD_ROWS.flat().map(key => [key.label, key.code]))

  it('uses canonical physical positions rather than Windows virtual-key codes', () => {
    expect(byLabel.get('A')).toBe(4)
    expect(byLabel.get('1')).toBe(30)
    expect(byLabel.get('Enter')).toBe(40)
    expect(byLabel.get('Meta')).toBe(227)
  })

  it('gives every displayed physical key one unique position', () => {
    const codes = KEYBOARD_ROWS.flat().map(key => key.code)
    expect(new Set(codes).size).toBe(codes.length)
  })
})
