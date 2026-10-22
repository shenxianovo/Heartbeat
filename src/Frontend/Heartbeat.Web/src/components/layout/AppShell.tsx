import { Link, Outlet, useRouterState } from "@tanstack/react-router";
import { Activity } from "lucide-react";
import { AppearanceSwitcher } from "./AppearanceSwitcher";
import { IdSearchBar } from "@/components/IdSearchBar";
import { useAppearance } from "@/lib/storage";

/** 右下角的吉祥物环境锚点：不同主题换一只，静态装饰。 */
function MascotDock() {
  const { mascot } = useAppearance();
  return (
    <div className="pointer-events-none fixed right-4 bottom-4 z-0 opacity-30 dark:opacity-20" aria-hidden="true">
      <img
        src={mascot === "windows" ? "/mascots/windows-peek.png" : "/mascots/macos.png"}
        alt=""
        className="animate-float h-40 w-40 rounded-3xl object-cover object-top shadow-lg md:h-52 md:w-52"
      />
    </div>
  );
}

export function AppShell() {
  // 首页 hero 自带大搜索栏，顶栏不再重复；其余页面顶栏常驻（含移动端）。
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const showHeaderSearch = pathname !== "/";

  return (
    <div className="relative flex min-h-screen flex-col">
      <header className="border-border/60 bg-background/70 sticky top-0 z-30 border-b backdrop-blur-md">
        <div className="mx-auto flex h-16 w-full max-w-7xl items-center gap-3 px-4 md:gap-5 md:px-8">
          <Link to="/" className="flex shrink-0 items-center gap-2.5">
            <span className="bg-primary/12 relative flex size-8 items-center justify-center rounded-xl">
              <Activity className="text-primary size-5" strokeWidth={2.4} />
              <span className="ring-primary/20 absolute inset-0 rounded-xl ring-1" />
            </span>
            <span className="leading-tight">
              <span className="block text-[15px] font-bold tracking-tight">Heartbeat</span>
              <span className="eyebrow hidden text-[9px] md:block">Twin Observers</span>
            </span>
          </Link>

          {showHeaderSearch && (
            <div className="flex min-w-0 flex-1 justify-center">
              <IdSearchBar />
            </div>
          )}

          <nav className={`flex items-center gap-2 ${showHeaderSearch ? "" : "ml-auto"}`}>
            <Link
              to="/compose"
              className="text-muted-foreground hover:text-foreground rounded-md px-3 py-1.5 text-sm font-medium transition-colors"
            >
              写入
            </Link>
            <AppearanceSwitcher />
          </nav>
        </div>
      </header>

      <main className="relative z-10 mx-auto w-full max-w-7xl flex-1 px-4 py-6 md:px-8 md:py-9">
        <Outlet />
      </main>

      <footer className="border-border/50 relative z-10 border-t py-4">
        <p className="text-muted-foreground mx-auto max-w-7xl px-4 text-xs md:px-8">
          Heartbeat · Observer / Observation / EntitySchema / 业务实体 —— 事实以 UTC 微秒保存，按本地时区呈现。
        </p>
      </footer>

      <MascotDock />
    </div>
  );
}
