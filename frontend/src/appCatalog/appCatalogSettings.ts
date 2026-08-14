export interface ClassificationDraft {
  identityKey: string
  targetAppKey: string
  newAppDisplayName: string
}

export function classificationFingerprint(draft: ClassificationDraft): string {
  return JSON.stringify([
    draft.identityKey.trim(),
    draft.targetAppKey.trim().toLowerCase(),
    draft.newAppDisplayName.trim(),
  ])
}

export function previewMatches(
  previewFingerprint: string | null,
  draft: ClassificationDraft,
): boolean {
  return previewFingerprint === classificationFingerprint(draft)
}

export function createExportSelection(_activeOverrideIdentityKeys: readonly string[]): Set<string> {
  return new Set<string>()
}

export function candidateBytes(contentBase64: string): Uint8Array {
  const binary = atob(contentBase64)
  return Uint8Array.from(binary, char => char.charCodeAt(0))
}
