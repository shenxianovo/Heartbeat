import { describe, expect, it } from 'vitest'
import { FactResponse, ObservationSnapshot, ObservationUploadRequest } from './client'

describe('generated observation contract', () => {
  it.each([false, 0, 'opaque value', [1, { nested: [false, 2] }], { future: { values: [1, 2] } }])(
    'preserves opaque JSON Result and Payload through reading and serialization: %j',
    (result) => {
      const fact = FactResponse.fromJS({ id: 'native-fact', kind: 'event', result, payload: result, source: null, streamId: null })
      expect(JSON.parse(JSON.stringify(fact.payload))).toEqual(result)
      expect(JSON.parse(JSON.stringify(fact)).result).toEqual(result)
      expect(fact.source).toBeNull()
      expect(fact.streamId).toBeNull()
    },
  )
})

describe('generated observation upload contract', () => {
  it.each([false, 0, 'opaque value', [1, { nested: false }], { future: [1, 2] }])(
    'preserves Result when uploading and retrying: %j', (result) => {
      const snapshot = ObservationSnapshot.fromJS({ id: 'native-fact', kind: 'event', result })
      expect(JSON.parse(JSON.stringify(snapshot)).result).toEqual(result)
      const request = new ObservationUploadRequest({ facts: [snapshot] })
      expect(JSON.parse(JSON.stringify(request)).facts[0].result).toEqual(result)
    },
  )
})
