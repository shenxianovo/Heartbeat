import type { TrackSummary } from "@/api/types";

interface TrackPickerProps {
  tracks: TrackSummary[];
  value: string | null;
  onChange: (trackId: string) => void;
}

export function trackLabel(track: TrackSummary): string {
  return `${track.type} · v${track.version}`;
}

export function TrackPicker({ tracks, value, onChange }: TrackPickerProps) {
  const groups = tracks.reduce<Map<string, TrackSummary[]>>((result, track) => {
    const group = result.get(track.collectorId);
    if (group) group.push(track);
    else result.set(track.collectorId, [track]);
    return result;
  }, new Map());

  return (
    <label className="field track-field">
      <span>采集来源与 Track</span>
      <select value={value ?? ""} onChange={(event) => onChange(event.target.value)}>
        {Array.from(groups.values()).map((group) => {
          const first = group[0];
          if (!first) return null;
          const collectorLabel = first.collectorDisplayName || first.collectorTarget;
          return (
            <optgroup key={first.collectorId} label={collectorLabel}>
              {group.map((track) => (
                <option key={track.id} value={track.id}>
                  {trackLabel(track)}
                </option>
              ))}
            </optgroup>
          );
        })}
      </select>
    </label>
  );
}
