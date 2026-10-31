import { Link } from "@tanstack/react-router";
import { folderName, nestSessions, productName, sessionTitle, type SessionGroup } from "@/lib/conversations";
import { clock } from "@/lib/time";
import { cn } from "@/lib/utils";

/** 这一天的会话，最近活动的在前；子 Agent 的会话缩进在父会话下面。 */
export function SessionList({ groups, selected }: { groups: SessionGroup[]; selected: string | null }) {
  return (
    <nav aria-label="会话" className="flex flex-col gap-0.5">
      {nestSessions(groups).map(({ group, depth }) => {
        const session = group.session?.content;
        const folder = folderName(group.workingDirectory);
        const active = group.entityId === selected;
        return (
          <Link
            key={group.entityId}
            to="."
            search={((prev: Record<string, unknown>) => ({ ...prev, session: group.entityId })) as never}
            aria-current={active ? "true" : undefined}
            className={cn(
              "block rounded-xl px-3 py-2.5 transition-colors",
              active ? "bg-panel" : "hover:bg-accent-soft",
              depth > 0 && "border-line-2 ml-4 rounded-l-none border-l",
            )}
          >
            <span className="flex items-baseline gap-2">
              <span className="text-fg-3 shrink-0 text-[11px]">{productName(session?.product)}</span>
              <span className="text-fg-3 ml-auto shrink-0 text-[11px] tabular-nums">
                {clock(group.first)}–{clock(group.last)}
              </span>
            </span>
            <span className="text-fg mt-0.5 line-clamp-2 text-[13px] leading-snug font-medium">
              {sessionTitle(group)}
            </span>
            <span className="text-fg-2 mt-1 block truncate text-xs">
              {[
                folder,
                group.userMessages > 0 && `${group.userMessages} 句`,
                group.toolCalls > 0 && `${group.toolCalls} 次工具调用`,
              ]
                .filter(Boolean)
                .join(" · ")}
            </span>
          </Link>
        );
      })}
    </nav>
  );
}
