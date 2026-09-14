"use client";

import { useQueryClient } from "@tanstack/react-query";
import { usePathname, useRouter } from "next/navigation";
import { useCallback, useEffect, useRef, type PropsWithChildren } from "react";
import { useAuth } from "react-oidc-context";

import { authConfiguration } from "@/auth/config";
import { safeReturnPath } from "@/auth/return-path";
import { LoadingState } from "@/components/status/LoadingState";

const RETURN_PATH_KEY = "heartbeat.oidc.return_path";

export function useSessionActions() {
  const auth = useAuth();
  const queryClient = useQueryClient();
  const router = useRouter();

  const signIn = useCallback(
    async (returnPath = "/") => {
      if (authConfiguration.error) return;
      window.sessionStorage.setItem(RETURN_PATH_KEY, safeReturnPath(returnPath));
      await auth.signinRedirect();
    },
    [auth],
  );

  const signOut = useCallback(async () => {
    queryClient.clear();
    window.sessionStorage.removeItem(RETURN_PATH_KEY);
    await auth.removeUser();
    router.replace("/login");
  }, [auth, queryClient, router]);

  return { signIn, signOut };
}

export function handleSigninCallback() {
  const returnPath = safeReturnPath(window.sessionStorage.getItem(RETURN_PATH_KEY));
  window.sessionStorage.removeItem(RETURN_PATH_KEY);
  window.location.replace(returnPath);
}

export function SessionCacheGuard({ children }: PropsWithChildren) {
  const auth = useAuth();
  const queryClient = useQueryClient();
  const previousSubject = useRef<string | null | undefined>(undefined);
  const subject = auth.user?.profile.sub ?? null;

  useEffect(() => {
    if (previousSubject.current !== undefined && previousSubject.current !== subject) {
      queryClient.clear();
    }
    previousSubject.current = subject;
  }, [queryClient, subject]);

  return children;
}

export function RequireSession({ children }: PropsWithChildren) {
  const auth = useAuth();
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    if (!auth.isLoading && !auth.isAuthenticated && !authConfiguration.error) {
      router.replace(`/login?returnTo=${encodeURIComponent(pathname)}`);
    }
  }, [auth.isAuthenticated, auth.isLoading, pathname, router]);

  if (authConfiguration.error) {
    return (
      <main className="centered-page">
        <section className="message-panel" role="alert">
          <span className="eyebrow">认证配置</span>
          <h1>Heartbeat 还不能连接登录服务</h1>
          <p>{authConfiguration.error}</p>
        </section>
      </main>
    );
  }

  if (auth.isLoading || !auth.isAuthenticated) {
    return <LoadingState label="正在确认登录状态" fullPage />;
  }

  return children;
}
