import { RequireSession } from "@/auth/session";
import { HubsPage } from "@/components/hubs/HubsPage";

export default function Page() {
  return (
    <RequireSession>
      <HubsPage />
    </RequireSession>
  );
}
