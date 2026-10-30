import { useEffect, useState, type ComponentType, type FormEvent, type ReactNode } from "react";
import { Link, Outlet, useNavigate } from "@tanstack/react-router";
import {
  CalendarDays,
  Check,
  LayoutGrid,
  Monitor,
  Moon,
  Network,
  Radio,
  Search,
  Server,
  Sun,
  SunMoon,
} from "lucide-react";
import { isUnreadable } from "@/api/client";
import { useSource } from "@/api/source";
import { useCurrent } from "@/hooks/useCurrent";
import { useHosts } from "@/hooks/useManagement";
import { useNow } from "@/hooks/useNow";
import { readTheme, saveTheme, type Theme } from "@/lib/preferences";
import { clock, duration } from "@/lib/time";
import { isUuidv7 } from "@/lib/uuid";
import { cn } from "@/lib/utils";
import { Stack } from "./stack/Stack";
import { HighlightProvider } from "./token/context";
import { Token } from "./token/Token";
import { Anchored, Dialog, MenuItem, MenuLabel } from "./ui";

export function AppShell() {
  return (
    <HighlightProvider>
      <Shell />
      <Stack />
    </HighlightProvider>
  );
}

/**
 * 左边是侧栏，页面的内容在右边，宽度随窗口变化，最宽 1240px。窄屏时侧栏变成顶部的一栏。
 */
function Shell() {
  return (
    <div className="min-h-screen md:pl-[216px]">
      <Sidebar />
      <main className="mx-auto max-w-[1240px] px-4 pt-6 pb-20 sm:px-8 md:pt-9">
        <Outlet />
      </main>
    </div>
  );
}

/**
 * 侧栏只放每一页都用得到的东西：页面导航和它们的状态、查找、现在在记录什么，以及外观。
 * 页面分两组：上面一组看记录，下面一组看采集。
 */
function Sidebar() {
  const now = useNow(5000);
  const { current, writing } = useCurrent(now);
  const hosts = useHosts();
  const offline = (hosts.data ?? []).filter((host) => !host.revoked && !host.connected).length;

  return (
    <aside
      aria-label="导航"
      className={cn(
        "bg-side border-line flex gap-x-3 gap-y-2 border-b px-4 py-3",
        "max-md:flex-wrap max-md:items-center",
        "md:fixed md:inset-y-0 md:left-0 md:z-20 md:w-[216px] md:flex-col md:border-r md:border-b-0 md:px-3 md:py-5",
      )}
    >
      <Link to="/" className="flex items-center gap-2.5 md:mb-4 md:px-2">
        <img src="/mascots/macos-128.png" alt="" className="size-7 rounded-md" />
        <span className="text-[15px] font-semibold tracking-[-0.01em]">Orisumi</span>
      </Link>
      <nav
        aria-label="页面"
        className="flex [scrollbar-width:none] gap-0.5 max-md:order-last max-md:w-full max-md:overflow-x-auto md:flex-col"
      >
        <NavItem to="/" icon={CalendarDays} exact>
          日
        </NavItem>
        <NavItem to="/applications" icon={LayoutGrid} exact>
          应用
        </NavItem>
        <NavItem to="/entities" icon={Network}>
          实体
        </NavItem>
        <p className="text-fg-3 px-2.5 pt-4 pb-1 text-[11px] max-md:hidden">采集</p>
        <NavItem
          to="/observers"
          icon={Radio}
          exact
          badge={
            writing > 0 && (
              <span title={`${writing} 个 Observer 正在写入`} className="grid size-2 place-items-center">
                <span className="bg-accent beat size-1.5 rounded-full" />
                <span className="sr-only">{writing} 个正在写入</span>
              </span>
            )
          }
        >
          Observer
        </NavItem>
        <NavItem
          to="/hosts"
          icon={Server}
          exact
          badge={
            offline > 0 && (
              <span
                title={`${offline} 台机器离线`}
                className="bg-panel text-fg-2 rounded-full px-1.5 text-[11px] leading-4 tabular-nums"
              >
                {offline}
                <span className="sr-only"> 台离线</span>
              </span>
            )
          }
        >
          托管
        </NavItem>
      </nav>
      <div className="flex items-center gap-1 max-md:ml-auto md:mt-4 md:block">
        <Finder />
        <Appearance className="md:hidden" top={false} />
      </div>
      <div className="flex-1 max-md:hidden" />
      <div className="max-md:hidden">
        <Current current={current} writing={writing} now={now} />
        <div className="mt-2 flex justify-end">
          <Appearance />
        </div>
      </div>
    </aside>
  );
}

function NavItem({
  to,
  icon: Icon,
  exact = false,
  badge,
  children,
}: {
  to: "/" | "/applications" | "/entities" | "/observers" | "/hosts";
  icon: ComponentType<{ className?: string }>;
  exact?: boolean;
  badge?: ReactNode;
  children: ReactNode;
}) {
  return (
    <Link
      to={to}
      activeOptions={{ exact, includeSearch: false }}
      className="text-fg-2 hover:text-fg hover:bg-panel relative flex shrink-0 items-center gap-2.5 rounded-lg px-2.5 py-1.5 text-[13px] transition-colors"
      activeProps={{ className: "bg-card !text-fg font-medium shadow-card hover:!bg-card" }}
    >
      <Icon aria-hidden="true" className="size-4 opacity-80" />
      <span className="flex-1">{children}</span>
      {badge}
    </Link>
  );
}

/** 现在在记录什么：当前时段的事物、从几点起、持续多久。点它推出它的面板。 */
function Current({
  current,
  writing,
  now,
}: {
  current: ReturnType<typeof useCurrent>["current"];
  writing: number;
  now: number;
}) {
  return (
    <section aria-label="现在" className="bg-card border-line shadow-card rounded-xl border px-3 py-2.5">
      <p className="text-fg-3 mb-1 flex items-center gap-1.5 text-[11px]">
        <span className="grid size-2 place-items-center">
          <span className={cn("size-1.5 rounded-full", writing ? "bg-accent beat" : "bg-fg-3")} />
        </span>
        {writing ? "现在" : "没有 Observer 在写入"}
      </p>
      {current ? (
        <>
          <Token version={current.row.thing!} className="-ml-1.5 max-w-full" />
          <p className="text-fg-3 mt-0.5 text-[11px] tabular-nums">
            {clock(current.since)} 起 · {duration(now - current.since)}
          </p>
        </>
      ) : (
        <p className="text-fg-2 text-xs">没有正在进行的记录</p>
      )}
    </section>
  );
}

/** 查找：一行入口，点它或按 ⌘K、/ 弹出 ID 输入框。 */
function Finder() {
  const [open, setOpen] = useState(false);
  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      const typing = event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement;
      if ((event.key === "k" && (event.metaKey || event.ctrlKey)) || (event.key === "/" && !typing)) {
        event.preventDefault();
        setOpen(true);
      }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        className="text-fg-2 hover:text-fg hover:bg-panel flex w-full cursor-pointer items-center gap-2.5 rounded-lg px-2.5 py-1.5 text-[13px] transition-colors"
      >
        <Search aria-hidden="true" className="size-4 opacity-80" />
        <span className="flex-1 text-left max-md:sr-only">查找</span>
        <kbd className="text-fg-3 border-line rounded border px-1 font-sans text-[10px] max-md:hidden">⌘K</kbd>
      </button>
      {open && (
        <Dialog title="查找" onClose={() => setOpen(false)}>
          <IdSearch onFound={() => setOpen(false)} />
        </Dialog>
      )}
    </>
  );
}

const THEMES: readonly { value: Theme; label: string; icon: ComponentType<{ className?: string }> }[] = [
  { value: "system", label: "系统", icon: Monitor },
  { value: "light", label: "浅", icon: Sun },
  { value: "dark", label: "深", icon: Moon },
];

/** 外观：一个图标按钮，点开选择主题。侧栏底部的向上弹出，窄屏顶栏里的向下弹出。 */
function Appearance({ className, top = true }: { className?: string; top?: boolean }) {
  const [theme, setTheme] = useState<Theme>(readTheme);
  const [open, setOpen] = useState(false);
  return (
    <span className={className}>
      <Anchored
        side={top ? "top" : "bottom"}
        align={top ? "start" : "end"}
        width="w-44"
        open={open}
        onClose={() => setOpen(false)}
        trigger={
          <button
            type="button"
            aria-label="外观"
            aria-expanded={open}
            onClick={() => setOpen((value) => !value)}
            className="text-fg-3 hover:text-fg hover:bg-panel cursor-pointer rounded-lg p-1.5"
          >
            <SunMoon className="size-4" />
          </button>
        }
      >
        <MenuLabel>主题</MenuLabel>
        {THEMES.map(({ value, label, icon: Icon }) => (
          <MenuItem
            key={value}
            onClick={() => {
              setTheme(value);
              saveTheme(value);
            }}
          >
            <Icon aria-hidden="true" className="text-fg-3 size-4" />
            <span className="flex-1">{label}</span>
            {theme === value && <Check aria-hidden="true" className="size-3.5" />}
          </MenuItem>
        ))}
      </Anchored>
    </span>
  );
}

/** 接受 EntityId 和 VersionId：先按 EntityId 读取，读不到再按 VersionId 读取。找到后跳到实体页，再调用 onFound。 */
export function IdSearch({ onFound }: { onFound: () => void }) {
  const source = useSource();
  const navigate = useNavigate();
  const [value, setValue] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    const id = value.trim().toLowerCase();
    if (!isUuidv7(id)) {
      setError("不是 UUIDv7");
      return;
    }
    setBusy(true);
    setError(null);
    try {
      try {
        await source.getEntity(id);
        await navigate({ to: "/entities/$entityId", params: { entityId: id } });
      } catch (error) {
        if (!isUnreadable(error)) throw error;
        try {
          const version = await source.getVersion(id);
          await navigate({
            to: "/entities/$entityId",
            params: { entityId: version.entityId },
            search: { version: id },
          });
        } catch (inner) {
          if (!isUnreadable(inner)) throw inner;
          setError("找不到这个 ID");
          return;
        }
      }
      setValue("");
      onFound();
    } catch (error) {
      setError(error instanceof Error ? error.message : String(error));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} role="search">
      <div className="relative">
        <Search
          aria-hidden="true"
          className="text-fg-3 pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2"
        />
        <input
          autoFocus
          aria-label="EntityId 或 VersionId"
          placeholder="EntityId 或 VersionId"
          value={value}
          disabled={busy}
          onChange={(event) => {
            setValue(event.target.value);
            setError(null);
          }}
          className={cn(
            "bg-card text-fg placeholder:text-fg-3 h-10 w-full rounded-lg border pr-3 pl-9 font-mono text-[13px] outline-none",
            error ? "border-danger" : "border-line focus:border-line-3",
          )}
        />
      </div>
      {error && (
        <p role="alert" className="text-danger mt-2 text-xs">
          {error}
        </p>
      )}
    </form>
  );
}
