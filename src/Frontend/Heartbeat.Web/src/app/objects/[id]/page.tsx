import { Suspense } from "react";
import { RequireSession } from "@/auth/session";
import { ObjectWorkspace } from "@/components/objects/ObjectWorkspace";
import { LoadingState } from "@/components/status/LoadingState";

export default async function ObjectPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return (
    <Suspense fallback={<LoadingState label="正在打开对象" fullPage />}>
      <RequireSession>
        <ObjectWorkspace id={id} />
      </RequireSession>
    </Suspense>
  );
}
