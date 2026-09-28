import { Suspense } from "react";

import { RequireSession } from "@/auth/session";
import { ReplayWorkbench } from "@/components/replay/ReplayWorkbench";
import { LoadingState } from "@/components/status/LoadingState";

export default function ReplayPage() {
  return (
    <Suspense fallback={<LoadingState label="正在打开回放" fullPage />}>
      <RequireSession>
        <ReplayWorkbench />
      </RequireSession>
    </Suspense>
  );
}
