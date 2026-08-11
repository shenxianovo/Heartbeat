export type KeyDef = { code: number; label: string; w?: number }

/** heartbeat-key-position-v1 主键区布局。 */
export const KEYBOARD_ROWS: KeyDef[][] = [
  [
    { code: 53, label: '`' }, { code: 30, label: '1' }, { code: 31, label: '2' },
    { code: 32, label: '3' }, { code: 33, label: '4' }, { code: 34, label: '5' },
    { code: 35, label: '6' }, { code: 36, label: '7' }, { code: 37, label: '8' },
    { code: 38, label: '9' }, { code: 39, label: '0' }, { code: 45, label: '-' },
    { code: 46, label: '=' }, { code: 42, label: 'Bksp', w: 2 },
  ],
  [
    { code: 43, label: 'Tab', w: 1.5 }, { code: 20, label: 'Q' }, { code: 26, label: 'W' },
    { code: 8, label: 'E' }, { code: 21, label: 'R' }, { code: 23, label: 'T' },
    { code: 28, label: 'Y' }, { code: 24, label: 'U' }, { code: 12, label: 'I' },
    { code: 18, label: 'O' }, { code: 19, label: 'P' }, { code: 47, label: '[' },
    { code: 48, label: ']' }, { code: 49, label: '\\', w: 1.5 },
  ],
  [
    { code: 57, label: 'Caps', w: 1.75 }, { code: 4, label: 'A' }, { code: 22, label: 'S' },
    { code: 7, label: 'D' }, { code: 9, label: 'F' }, { code: 10, label: 'G' },
    { code: 11, label: 'H' }, { code: 13, label: 'J' }, { code: 14, label: 'K' },
    { code: 15, label: 'L' }, { code: 51, label: ';' }, { code: 52, label: "'" },
    { code: 40, label: 'Enter', w: 2.25 },
  ],
  [
    { code: 225, label: 'LShift', w: 2.25 }, { code: 29, label: 'Z' }, { code: 27, label: 'X' },
    { code: 6, label: 'C' }, { code: 25, label: 'V' }, { code: 5, label: 'B' },
    { code: 17, label: 'N' }, { code: 16, label: 'M' }, { code: 54, label: ',' },
    { code: 55, label: '.' }, { code: 56, label: '/' }, { code: 229, label: 'RShift', w: 2.75 },
  ],
  [
    { code: 224, label: 'LCtrl', w: 1.25 }, { code: 227, label: 'Meta', w: 1.25 },
    { code: 226, label: 'LAlt', w: 1.25 }, { code: 44, label: 'Space', w: 6.25 },
    { code: 230, label: 'RAlt', w: 1.25 }, { code: 101, label: 'Menu', w: 1.25 },
    { code: 228, label: 'RCtrl', w: 1.25 },
  ],
]
