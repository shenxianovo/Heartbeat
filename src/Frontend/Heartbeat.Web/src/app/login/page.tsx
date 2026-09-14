import { Suspense } from "react";

import { LoginScreen } from "@/components/auth/LoginScreen";
import { LoadingState } from "@/components/status/LoadingState";

export default function LoginPage() {
  return (
    <Suspense fallback={<LoadingState label="正在准备登录" fullPage />}>
      <LoginScreen />
    </Suspense>
  );
}
