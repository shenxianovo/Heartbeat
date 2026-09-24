"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { useEffect, useState } from "react";
import { useAuth } from "react-oidc-context";

import { authConfiguration } from "@/auth/config";
import { safeReturnPath } from "@/auth/return-path";
import { useSessionActions } from "@/auth/session";

export function LoginScreen() {
  const auth = useAuth();
  const router = useRouter();
  const searchParams = useSearchParams();
  const { signIn } = useSessionActions();
  const [starting, setStarting] = useState(false);
  const returnPath = safeReturnPath(searchParams.get("returnTo"));

  useEffect(() => {
    if (!auth.isLoading && auth.isAuthenticated) router.replace(returnPath);
  }, [auth.isAuthenticated, auth.isLoading, returnPath, router]);

  async function startLogin() {
    setStarting(true);
    try {
      await signIn(returnPath);
    } catch {
      setStarting(false);
    }
  }

  return (
    <main className="login-page">
      <div className="login-atmosphere" aria-hidden="true" />
      <section className="login-panel" aria-labelledby="login-title">
        <div className="brand-mark" aria-hidden="true">
          <span />
          <span />
          <span />
        </div>
        <span className="eyebrow">HEARTBEAT</span>
        <h1 id="login-title">回到你的数字轨迹</h1>
        <p>登录后查看不同来源留下的记录，并按时间回放你的数字轨迹。</p>

        {authConfiguration.error ? (
          <div className="inline-alert" role="alert">
            <strong>登录服务未配置</strong>
            <span>{authConfiguration.error}</span>
          </div>
        ) : auth.error ? (
          <div className="inline-alert" role="alert">
            <strong>登录没有完成</strong>
            <span>{auth.error.message}</span>
          </div>
        ) : null}

        <button
          className="primary-button login-button"
          type="button"
          disabled={starting || auth.isLoading || Boolean(authConfiguration.error)}
          onClick={() => void startLogin()}
        >
          {starting ? "正在前往登录…" : "使用 Heartbeat 账号登录"}
          <span aria-hidden="true">→</span>
        </button>
        <p className="login-note">登录后只会显示属于你的记录。</p>
      </section>
    </main>
  );
}
