import { useState } from "react";
import { createFileRoute, useRouter } from "@tanstack/react-router";
import { useQueryClient } from "@tanstack/react-query";
import { ApiError } from "@/api/client";
import { useSource } from "@/api/source";
import { Content } from "@/components/entity/Content";
import { DeleteDialog, ReferrerList, VersionList } from "@/components/entity/Panels";
import { useVersionType } from "@/components/entity/VersionChip";
import { Button, Card, Message, Mono, SectionTitle } from "@/components/ui";
import { useEntity, useEntityVersions } from "@/hooks/queries";
import { dateTime, parseTime } from "@/lib/time";
import { versionNumber } from "@/lib/versions";

interface EntitySearch {
  version?: string | undefined;
}

export const Route = createFileRoute("/entities/$entityId")({
  validateSearch: (search: Record<string, unknown>): EntitySearch =>
    typeof search.version === "string" && search.version ? { version: search.version } : {},
  component: EntityPage,
});

function EntityPage() {
  const { entityId } = Route.useParams();
  const search = Route.useSearch();
  const navigate = Route.useNavigate();
  const router = useRouter();
  const source = useSource();
  const queryClient = useQueryClient();
  const entity = useEntity(entityId);
  const chain = useEntityVersions(entity.data ? entityId : null);
  const [deleting, setDeleting] = useState(false);
  const [busy, setBusy] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  const versions = chain.data ?? (entity.data ? [entity.data] : []);
  const selected = versions.find((v) => v.versionId === search.version) ?? entity.data;
  const type = useVersionType(selected);

  if (entity.isPending) return <Message title="读取中" />;
  if (entity.isError) {
    const status = entity.error instanceof ApiError ? entity.error.status : 0;
    return (
      <Message
        title={status === 410 ? "这个实体已删除" : status === 404 ? "没有这个实体" : "读不到这个实体"}
        detail={entityId}
      />
    );
  }
  if (!selected) return null;

  const number = versionNumber(versions, selected.versionId);
  const recordedAt = parseTime(selected.recordedAt);

  async function remove() {
    setBusy(true);
    setDeleteError(null);
    try {
      await source.deleteEntity(entityId);
      await queryClient.invalidateQueries();
      setDeleting(false);
      if (window.history.length > 1) router.history.back();
      else await navigate({ to: "/" });
    } catch (error) {
      setDeleteError(error instanceof Error ? error.message : String(error));
    } finally {
      setBusy(false);
    }
  }

  return (
    <article>
      <div className="mb-4 flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h1 className="text-xl font-medium">{type.name ?? "…"}</h1>
          <Mono className="text-fg-3 break-all">
            {entityId} · v{number ?? "?"}
            {versions.length > 1 && number !== versions.length ? ` / v${versions.length}` : ""} ·{" "}
            {recordedAt === null ? selected.recordedAt : dateTime(recordedAt)}
          </Mono>
        </div>
        <Button onClick={() => setDeleting(true)}>删除</Button>
      </div>

      <Card className="mb-4">
        <Content version={selected} />
      </Card>

      <div className="grid gap-4 md:grid-cols-2">
        <Card>
          <SectionTitle>版本{versions.length > 1 ? ` · ${versions.length}` : ""}</SectionTitle>
          <VersionList
            chain={versions}
            selected={selected.versionId}
            onSelect={(version) => navigate({ search: { version }, replace: true })}
          />
        </Card>
        <Card>
          <SectionTitle>被引用</SectionTitle>
          <ReferrerList entityId={entityId} />
        </Card>
      </div>

      <DeleteDialog
        open={deleting}
        busy={busy}
        error={deleteError}
        onClose={() => {
          setDeleting(false);
          setDeleteError(null);
        }}
        onConfirm={() => void remove()}
      />
    </article>
  );
}
