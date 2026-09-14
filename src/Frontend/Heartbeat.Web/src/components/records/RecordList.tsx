import type { TimelineRecord, TrackReference } from "@/api/types";
import { RecordCard } from "@/components/records/RecordCard";

interface RecordListProps {
  records: TimelineRecord[];
  track: TrackReference;
}

export function RecordList({ records, track }: RecordListProps) {
  return (
    <div className="record-list" aria-label="记录列表">
      {records.map((record) => (
        <RecordCard key={record.id} record={record} track={track} />
      ))}
    </div>
  );
}
