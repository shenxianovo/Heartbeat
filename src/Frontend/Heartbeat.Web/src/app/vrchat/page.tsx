import type { Metadata } from "next";
import { Suspense } from "react";
import { LoadingState } from "@/components/status/LoadingState";
import { RequireSession } from "@/auth/session";
import { VRChatDashboard } from "@/components/vrchat/VRChatDashboard";

export const metadata: Metadata = { title: "VRChat 世界与相遇" };

export default function VRChatPage() {
  return (
    <Suspense fallback={<LoadingState label="正在打开 VRChat" fullPage />}>
      <RequireSession>
        <VRChatDashboard />
      </RequireSession>
    </Suspense>
  );
}
