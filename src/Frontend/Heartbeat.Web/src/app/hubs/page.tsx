import type { Metadata } from "next";
import { RequireSession } from "@/auth/session";
import { HubsPage } from "@/components/hubs/HubsPage";

export const metadata: Metadata = { title: "Hub 管理" };

export default function Page() {
  return (
    <RequireSession>
      <HubsPage />
    </RequireSession>
  );
}
