import { createRootRoute, Link } from "@tanstack/react-router";
import { AppShell } from "@/components/layout/AppShell";
import { Button } from "@/components/ui/button";

function DefaultNotFound() {
  return (
    <div className="mx-auto grid max-w-md place-items-center gap-5 py-24 text-center">
      <img src="/mascots/macos.png" alt="" className="h-36 w-36 rounded-3xl object-cover object-top opacity-90" />
      <div>
        <h1 className="text-2xl font-bold">这里什么也没有</h1>
        <p className="text-muted-foreground mt-2 text-sm">
          Mac 娘朝你眨了眨眼。页面不存在，或者这条引用的实体还没被观测到。
        </p>
      </div>
      <Button asChild>
        <Link to="/">回到观测台</Link>
      </Button>
    </div>
  );
}

export const Route = createRootRoute({
  component: AppShell,
  notFoundComponent: DefaultNotFound,
});
