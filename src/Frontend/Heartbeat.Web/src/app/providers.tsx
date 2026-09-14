"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState, type PropsWithChildren } from "react";
import { AuthProvider } from "react-oidc-context";

import { authConfiguration, createUserManagerSettings } from "@/auth/config";
import { handleSigninCallback, SessionCacheGuard } from "@/auth/session";

export function Providers({ children }: PropsWithChildren) {
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            staleTime: 30_000,
            retry: (failureCount, error) => {
              if (error instanceof Error && "status" in error && error.status === 401) return false;
              return failureCount < 2;
            },
          },
        },
      }),
  );

  return (
    <QueryClientProvider client={queryClient}>
      <AuthProvider
        {...createUserManagerSettings(authConfiguration)}
        onSigninCallback={handleSigninCallback}
      >
        <SessionCacheGuard>{children}</SessionCacheGuard>
      </AuthProvider>
    </QueryClientProvider>
  );
}
