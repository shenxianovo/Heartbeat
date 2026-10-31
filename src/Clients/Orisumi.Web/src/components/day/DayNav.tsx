import { useEffect } from "react";
import { useNavigate } from "@tanstack/react-router";
import { ChevronLeft, ChevronRight } from "lucide-react";
import { dayRange, dayTitle, MINUTE } from "@/lib/time";

/** 页头的日期和前后翻页。地址参数是 date=YYYY-MM-DD，今天省略；← 和 → 也翻日期。 */
export function DayNav({ start, today }: { start: number; today: boolean }) {
  const navigate = useNavigate();

  function go(offset: number) {
    const next = dayRange(start + offset * 24 * 60 * MINUTE + 12 * 60 * MINUTE).key;
    const todayKey = dayRange(Date.now()).key;
    void navigate({
      to: ".",
      search: ((prev: Record<string, unknown>) => ({ ...prev, date: next === todayKey ? undefined : next })) as never,
    });
  }

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.target instanceof HTMLInputElement || event.metaKey || event.ctrlKey || event.altKey) return;
      if (event.key === "ArrowLeft") go(-1);
      if (event.key === "ArrowRight" && !today) go(1);
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  return (
    <div className="-ml-1.5 flex items-center gap-1">
      <button
        type="button"
        aria-label="前一天"
        onClick={() => go(-1)}
        className="text-fg-2 hover:text-fg hover:bg-panel cursor-pointer rounded-md p-1"
      >
        <ChevronLeft className="size-5" />
      </button>
      <h1 className="text-[26px] leading-tight font-semibold tracking-[-0.01em] tabular-nums">{dayTitle(start)}</h1>
      <button
        type="button"
        aria-label="后一天"
        disabled={today}
        onClick={() => go(1)}
        className="text-fg-2 hover:text-fg hover:bg-panel cursor-pointer rounded-md p-1 disabled:cursor-default disabled:opacity-30"
      >
        <ChevronRight className="size-5" />
      </button>
      {!today && (
        <button
          type="button"
          onClick={() =>
            void navigate({
              to: ".",
              search: ((prev: Record<string, unknown>) => ({ ...prev, date: undefined })) as never,
            })
          }
          className="border-line-2 text-fg-2 hover:text-fg ml-2 cursor-pointer rounded-md border px-2 py-0.5 text-xs"
        >
          回到今天
        </button>
      )}
    </div>
  );
}
