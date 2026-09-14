"use client";

import { useState } from "react";
import { useAuth } from "react-oidc-context";
import Link from "next/link";

import { useSessionActions } from "@/auth/session";

export function AppHeader() {
  const auth = useAuth();
  const { signOut } = useSessionActions();
  const [leaving, setLeaving] = useState(false);
  const displayName =
    typeof auth.user?.profile.preferred_username === "string"
      ? auth.user.profile.preferred_username
      : typeof auth.user?.profile.name === "string"
        ? auth.user.profile.name
        : "我的 Timeline";

  async function leave() {
    setLeaving(true);
    await signOut();
  }

  return (
    <header className="app-header">
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
      <div className="account-menu">
        <span className="account-name">{displayName}</span>
        <button className="quiet-button" type="button" onClick={leave} disabled={leaving}>
          {leaving ? "正在退出…" : "退出登录"}
        </button>
      </div>
    </header>
  );
}
