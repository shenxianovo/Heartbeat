// Keep ISO text from JSON; converting to Date would discard microseconds.
export type JsonWire<T> = T extends Date ? string
  : T extends undefined ? null
  : T extends Array<infer Item> ? JsonWire<Item>[]
  : T extends object ? {
    [Key in keyof T as string extends Key ? never : number extends Key ? never
      : T[Key] extends (...args: never[]) => unknown ? never : Key]: JsonWire<T[Key]>
  } : T
