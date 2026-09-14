"use client";

import Link from "next/link";
import { useAuth } from "react-oidc-context";

import { LoadingState } from "@/components/status/LoadingState";

export default function AuthenticationCallbackPage() {
  const auth = useAuth();

  if (auth.error) {
    return (
      <main className="centered-page">
        <section className="message-panel" role="alert">
          <span className="eyebrow">登录未完成</span>
          <h1>无法确认这次登录</h1>
          <p>{auth.error.message}</p>
          <Link className="primary-button" href="/login">
            返回登录
          </Link>
        </section>
      </main>
    );
  }

  return <LoadingState label="正在完成登录" fullPage />;
}
