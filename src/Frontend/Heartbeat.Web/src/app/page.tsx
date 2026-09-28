import { RequireSession } from "@/auth/session";
import { OverviewDashboard } from "@/components/overview/OverviewDashboard";

export default function OverviewPage() {
  return (
    <RequireSession>
      <OverviewDashboard />
    </RequireSession>
  );
}
