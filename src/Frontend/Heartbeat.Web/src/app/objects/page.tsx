import { Suspense } from "react";
import { RequireSession } from "@/auth/session";
import { ObjectDirectory } from "@/components/objects/ObjectDirectory";
import { LoadingState } from "@/components/status/LoadingState";

export default function ObjectsPage() {
  return (
    <Suspense fallback={<LoadingState label="正在打开对象" fullPage />}>
      <RequireSession>
        <ObjectDirectory />
      </RequireSession>
    </Suspense>
  );
}
