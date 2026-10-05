import { formatDateTime, formatDurationMinutes } from "@/lib/dates";
import { accessType, type Visit } from "./model";

export function VisitDetails({ visits }: { visits: Visit[] }) {
  return (
    <ol className="vrc-visits">
      {[...visits]
        .sort((a, b) => b.from - a.from)
        .map((visit) => (
          <li key={visit.id}>
            <div>
              <time>{formatDateTime(new Date(visit.from).toISOString())}</time>
              <span> → {formatDateTime(new Date(visit.to).toISOString())}</span>
            </div>
            <strong>
              {formatDurationMinutes(visit.to - visit.from)} · {accessType(visit.value.instance_id)}
            </strong>
            <small>{visit.value.world_name || visit.value.world_id}</small>
            <small>实例 {visit.value.instance_id}</small>
          </li>
        ))}
    </ol>
  );
}
