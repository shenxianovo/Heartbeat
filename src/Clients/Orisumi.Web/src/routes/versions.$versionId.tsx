import { createFileRoute, Navigate } from "@tanstack/react-router";
import { ApiError } from "@/api/client";
import { Message } from "@/components/ui";
import { useVersion } from "@/hooks/queries";

export const Route = createFileRoute("/versions/$versionId")({
  component: VersionRedirect,
});

/** 读取版本后跳到所属实体，并选中这个版本。 */
function VersionRedirect() {
  const { versionId } = Route.useParams();
  const version = useVersion(versionId);
  if (version.data) {
    return (
      <Navigate
        to="/entities/$entityId"
        params={{ entityId: version.data.entityId }}
        search={{ version: versionId }}
        replace
      />
    );
  }
  if (version.isError) {
    const status = version.error instanceof ApiError ? version.error.status : 0;
    return (
      <Message
        title={status === 410 ? "这个版本已删除" : status === 404 ? "没有这个版本" : "读不到这个版本"}
        detail={versionId}
      />
    );
  }
  return <Message title="读取中" />;
}
