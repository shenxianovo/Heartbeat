import { createRootRoute, Link } from "@tanstack/react-router";
import { AppShell } from "@/components/AppShell";
import { Message } from "@/components/ui";

interface RootSearch {
  /** 右侧堆叠的面板，逗号分隔的 EntityId。 */
  stack?: string | undefined;
}

export const Route = createRootRoute({
  validateSearch: (search: Record<string, unknown>): RootSearch =>
    typeof search.stack === "string" && search.stack ? { stack: search.stack } : {},
  component: AppShell,
  notFoundComponent: () => (
    <Message title="没有这个页面" mascot>
      <Link to="/" className="text-fg-2 hover:text-fg text-[13px] underline underline-offset-4">
        回到今天
      </Link>
    </Message>
  ),
});
