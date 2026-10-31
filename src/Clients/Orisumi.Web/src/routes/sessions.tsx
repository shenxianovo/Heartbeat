import { useMemo } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { PanelRight } from "lucide-react";
import { DayNav } from "@/components/day/DayNav";
import { Conversation } from "@/components/sessions/Conversation";
import { SessionList } from "@/components/sessions/SessionList";
import { useStack } from "@/components/token/context";
import { Card, Message, Segmented } from "@/components/ui";
import { useConversations } from "@/hooks/useConversations";
import { useNow } from "@/hooks/useNow";
import { buildItems, productName, sessionTitle, VIEWS, type SessionGroup, type View } from "@/lib/conversations";
import { dayOf, dayRange } from "@/lib/time";

interface SessionsSearch {
  date?: string | undefined;
  session?: string | undefined;
  view?: View | undefined;
}

export const Route = createFileRoute("/sessions")({
  validateSearch: (search: Record<string, unknown>): SessionsSearch => ({
    ...(typeof search.date === "string" && dayOf(search.date) ? { date: search.date } : {}),
    ...(typeof search.session === "string" ? { session: search.session } : {}),
    ...(VIEWS.some((v) => v.value === search.view) ? { view: search.view as View } : {}),
  }),
  component: SessionsPage,
});

/** 会话：这一天和 Claude Code、Codex 的会话。左边是会话，右边是选中会话这一天的交互。 */
function SessionsPage() {
  const search = Route.useSearch();
  const navigate = Route.useNavigate();
  const now = useNow(60_000);
  const conversations = useConversations(search.date ?? dayRange(now).key);
  const { range, today } = conversations;
  const groups = conversations.groups ?? [];
  const selected = groups.find((g) => g.entityId === search.session) ?? groups[0] ?? null;
  const view = search.view ?? "dialog";
  const userMessages = groups.reduce((sum, g) => sum + g.userMessages, 0);
  const toolCalls = groups.reduce((sum, g) => sum + g.toolCalls, 0);

  return (
    <section>
      <header className="mb-6">
        <DayNav start={range.start} today={today} />
        {groups.length > 0 && (
          <p className="text-fg-2 mt-1.5 text-[13px]">
            {groups.length} 个会话 · 你说了 {userMessages} 句 · {toolCalls} 次工具调用
          </p>
        )}
      </header>

      {conversations.error && !conversations.groups ? (
        <Message title="读不到这一天的会话" detail={(conversations.error as Error).message} mascot />
      ) : conversations.loading && !conversations.groups ? (
        <Message title="正在读取会话" />
      ) : !groups.length ? (
        <Message
          title={today ? "今天还没有助手会话" : "这一天没有助手会话"}
          detail="助手会话 Observer 读到 Claude Code 或 Codex 的会话后，它们出现在这里"
          mascot
        />
      ) : (
        <div className="grid items-start gap-4 lg:grid-cols-[300px_minmax(0,1fr)]">
          <Card className="px-2 py-2 lg:sticky lg:top-6 lg:max-h-[calc(100vh-48px)] lg:overflow-y-auto">
            <SessionList groups={groups} selected={selected?.entityId ?? null} />
          </Card>
          {selected && (
            <Selected
              group={selected}
              view={view}
              onView={(next) =>
                void navigate({ search: (prev) => ({ ...prev, view: next === "dialog" ? undefined : next }) })
              }
            />
          )}
        </div>
      )}
    </section>
  );
}

function Selected({ group, view, onView }: { group: SessionGroup; view: View; onView: (view: View) => void }) {
  const stack = useStack();
  const items = useMemo(() => buildItems(group.interactions, view), [group.interactions, view]);
  const session = group.session?.content;
  const models = [...new Set(group.interactions.map((i) => i.version.content.model).filter(Boolean))];
  return (
    <Card className="min-w-0 px-4 py-4 sm:px-5">
      <header className="border-line mb-3 flex flex-wrap items-start gap-x-4 gap-y-3 border-b pb-4">
        <div className="min-w-[min(100%,280px)] flex-1">
          <h2 className="text-[17px] leading-snug font-semibold">{sessionTitle(session)}</h2>
          <p className="text-fg-2 mt-1 text-xs break-all">
            {[productName(session?.product), session?.client, group.workingDirectory, models.join("、")]
              .filter(Boolean)
              .join(" · ")}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Segmented label="显示" options={VIEWS} value={view} onChange={onView} />
          <button
            type="button"
            aria-label="会话详情"
            title="会话详情"
            onClick={() => stack.open(group.entityId, -1)}
            className="text-fg-2 hover:text-fg hover:bg-panel cursor-pointer rounded-md p-1.5"
          >
            <PanelRight aria-hidden="true" className="size-4" />
          </button>
        </div>
      </header>
      {items.length ? (
        <Conversation items={items} product={session?.product} />
      ) : (
        <Message title="这个视图下没有交互" detail="切到“过程”或“全部”看工具调用、推理和上下文" />
      )}
    </Card>
  );
}
