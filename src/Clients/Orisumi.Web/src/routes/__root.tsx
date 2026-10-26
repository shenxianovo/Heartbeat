import { createRootRoute, Link } from "@tanstack/react-router";
import { AppShell } from "@/components/AppShell";
import { Message } from "@/components/ui";

export const Route = createRootRoute({
  component: AppShell,
  notFoundComponent: () => (
    <Message title="没有这个页面">
      <Link to="/" className="text-fg-2 hover:text-fg text-[13px] underline underline-offset-4">
        回到现在
      </Link>
    </Message>
  ),
});
