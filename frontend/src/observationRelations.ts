/** Only explicit observation object evidence can associate facts across views. */
export interface RelatedObservation<T extends { id?: string; kind?: string }> {
  foi?: T | null
  relations?: { kind?: string; members?: { role?: string; object?: T }[] }[]
}

export function relatedObject<T extends { id?: string; kind?: string }>(fact: RelatedObservation<T>, role: string): T | undefined {
  const kind = role === 'device' ? 'machine' : role
  return fact.foi?.kind === kind ? fact.foi : fact.relations
    ?.filter(r => r.kind === 'observed-on' || r.kind === 'application-account-use')
    .flatMap(r => r.members ?? []).find(m => m.role === role)?.object
}
