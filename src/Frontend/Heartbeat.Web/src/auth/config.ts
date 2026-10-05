import { WebStorageStateStore, type UserManagerSettings } from "oidc-client-ts";

const DEFAULT_AUTHORITY = "https://auth.shenxianovo.com";
const DEFAULT_CLIENT_ID = "heartbeat-web";
const DEFAULT_SCOPE = "openid profile offline_access";

export interface PublicAuthConfiguration {
  authority: string;
  clientId: string;
  scope: string;
  error: string | null;
}

export function getPublicAuthConfiguration(): PublicAuthConfiguration {
  const authority = process.env.NEXT_PUBLIC_OIDC_AUTHORITY ?? DEFAULT_AUTHORITY;
  const clientId = process.env.NEXT_PUBLIC_OIDC_CLIENT_ID ?? DEFAULT_CLIENT_ID;
  const scope = process.env.NEXT_PUBLIC_OIDC_SCOPE ?? DEFAULT_SCOPE;

  const missing = !authority.trim()
    ? "NEXT_PUBLIC_OIDC_AUTHORITY"
    : !clientId.trim()
      ? "NEXT_PUBLIC_OIDC_CLIENT_ID"
      : null;

  return {
    authority: authority.trim(),
    clientId: clientId.trim(),
    scope: scope.trim() || DEFAULT_SCOPE,
    error: missing ? `缺少 ${missing}，暂时无法登录。` : null,
  };
}

export function createUserManagerSettings(
  configuration: PublicAuthConfiguration,
): UserManagerSettings {
  const origin = typeof window === "undefined" ? "http://localhost:3000" : window.location.origin;
  const storage = typeof window === "undefined" ? undefined : window.sessionStorage;

  return {
    authority: configuration.authority || DEFAULT_AUTHORITY,
    client_id: configuration.clientId || DEFAULT_CLIENT_ID,
    redirect_uri: `${origin}/auth/callback`,
    post_logout_redirect_uri: `${origin}/login`,
    response_type: "code",
    scope: configuration.scope,
    automaticSilentRenew: true,
    loadUserInfo: false,
    monitorSession: false,
    ...(storage
      ? {
          stateStore: new WebStorageStateStore({ store: storage }),
          userStore: new WebStorageStateStore({ store: storage }),
        }
      : {}),
  };
}

export const authConfiguration = getPublicAuthConfiguration();
