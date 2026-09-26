"use client";

import { Button, buttonVariants } from "@/components/ui/button";
import { useState } from "react";
import { useAuth } from "react-oidc-context";
import Link from "next/link";

import { useSessionActions } from "@/auth/session";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";

export function AppHeader() {
  const auth = useAuth();
  const { signOut } = useSessionActions();
  const [leaving, setLeaving] = useState(false);
  const displayName =
    [auth.user?.profile.preferred_username, auth.user?.profile.name]
      .find((value): value is string => typeof value === "string" && value.trim().length > 0)
      ?.trim() ?? "我的 Timeline";
  const initial = Array.from(displayName)[0]?.toLocaleUpperCase() ?? "我";

  async function leave() {
    setLeaving(true);
    await signOut();
  }

  return (
    <header className="app-header">
      <nav className="header-navigation" aria-label="主导航">
        <Link className="brand" href="/" aria-label="Heartbeat 回放首页">
          <span className="brand-pulse" aria-hidden="true">
            <i />
            <i />
            <i />
          </span>
          <span>
            <strong>Heartbeat</strong>
            <small>REPLAY</small>
          </span>
        </Link>
        <Link
          className={buttonVariants({ variant: "glass", size: "sm" })}
          data-slot="button"
          href="/hubs"
        >
          Hub 管理
        </Link>
      </nav>
      <div className="account-menu">
        <div className="account-identity" aria-label={`当前登录：${displayName}`}>
          <Avatar aria-hidden="true">
            <AvatarFallback className="bg-primary/15 text-primary text-[0.8rem] font-bold">
              {initial}
            </AvatarFallback>
          </Avatar>
          <span className="account-name">{displayName}</span>
        </div>
        <Button variant="glass" type="button" onClick={() => void leave()} disabled={leaving}>
          {leaving ? "正在退出…" : "退出登录"}
        </Button>
      </div>
    </header>
  );
}
