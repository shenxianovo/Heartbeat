import { useState } from "react";
import { createFileRoute, Link, useRouter } from "@tanstack/react-router";
import { useQueryClient } from "@tanstack/react-query";
import { Check, ChevronRight, Copy } from "lucide-react";
import { ApiError } from "@/api/client";
import { useSource } from "@/api/source";
import { isEntitySchema, type EntitySchema, type Version } from "@/api/types";
import { Content } from "@/components/entity/Content";
import { coreKind } from "@/components/entity/kinds";
import { DeleteDialog, VersionList } from "@/components/entity/Panels";
import { ReferenceMap } from "@/components/entity/ReferenceMap";
import { iconOf, Mark } from "@/components/token/Token";
import { Button, Card, Message, Mono, SectionTitle } from "@/components/ui";
import { useEntity, useEntityVersions } from "@/hooks/queries";
import { useDescribe } from "@/hooks/useEntityInfo";
import { useNow } from "@/hooks/useNow";
import { ago, dateTime, parseTime } from "@/lib/time";
import { versionLabel, versionNumber } from "@/lib/versions";

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
  const now = useNow(30_000);
  const entity = useEntity(entityId);
  const chain = useEntityVersions(entity.data ? entityId : null);
  const [deleting, setDeleting] = useState(false);
  const [busy, setBusy] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  const versions = chain.data ?? (entity.data ? [entity.data] : []);
  const selected = versions.find((v) => v.versionId === search.version) ?? entity.data;
  const { info } = useDescribe(selected);

  if (entity.isPending) return <Message title="读取中" mascot />;
  if (entity.isError) {
    const status = entity.error instanceof ApiError ? entity.error.status : 0;
    return (
      <Message
        title={status === 410 ? "这个实体已删除" : status === 404 ? "没有这个实体" : "读不到这个实体"}
        detail={entityId}
        mascot
      />
    );
  }
  if (!selected) return null;

  const number = versionNumber(versions, selected.versionId);
  const recordedAt = parseTime(selected.recordedAt);
  const schema = isEntitySchema(selected) ? (selected as Version<EntitySchema>) : null;
  const title = info?.title ?? "…";
  const typeKey =
    selected.category === "business"
      ? info?.schema?.content.resourceName
      : selected.category === "entity_schema"
        ? undefined
        : selected.category;
  const typeName = info?.typeName ?? "…";
  const mark = (size: "lg" | "xl") =>
    info ? (
      <Mark role={info.role} color={info.color} icon={iconOf(info)} size={size} framed />
    ) : (
      <span
        className={size === "xl" ? "bg-panel size-14 shrink-0 rounded-2xl" : "bg-panel size-10 shrink-0 rounded-[10px]"}
      />
    );

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
      <nav aria-label="位置" className="text-fg-3 mb-3 flex items-center gap-1 text-xs">
        <Link to="/entities" className="hover:text-fg">
          实体
        </Link>
        <ChevronRight className="size-3" />
        {typeKey ? (
          <Link to="/entities" search={{ type: typeKey }} className="hover:text-fg">
            {typeName}
          </Link>
        ) : (
          <span>{typeName}</span>
        )}
        <ChevronRight className="size-3" />
        <span className="text-fg-2">{number === null ? "?" : versionLabel(number, versions.length)}</span>
      </nav>

      <header className="bg-card border-line shadow-card mb-4 rounded-2xl border p-4 sm:p-5">
        <div className="flex items-start gap-3.5">
          {mark("xl")}
          <div className="min-w-0 flex-1">
            <p className="text-fg-3 text-xs">{info ? `${info.roleName} · ${typeName}` : "读取中"}</p>
            <h1 className="text-[26px] leading-tight font-semibold tracking-[-0.01em] break-all">{title}</h1>
            <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1">
              <CopyId id={entityId} />
              <span className="text-fg-2 text-xs">
                {number === null ? "v?" : versionLabel(number, versions.length)}
                {versions.length > 1 && number === versions.length && ` · 共 ${versions.length} 版`}
              </span>
              <span className="text-fg-3 text-xs" title={recordedAt === null ? undefined : dateTime(recordedAt)}>
                {recordedAt === null ? selected.recordedAt : `${ago(now - recordedAt)}记录`}
              </span>
            </div>
          </div>
          <Button onClick={() => setDeleting(true)}>删除</Button>
        </div>
        {!schema && coreKind(selected.category) && (
          <p className="text-fg-2 border-line mt-3 border-t pt-2.5 text-[13px]">{coreKind(selected.category)!.hint}</p>
        )}
      </header>

      <Card className="mb-4">
        <SectionTitle>引用</SectionTitle>
        <ReferenceMap
          version={selected}
          fields={info?.schema?.content.fields}
          title={`这个${title}`}
          center={
            <>
              {mark("lg")}
              <span className="min-w-0">
                <span className="block truncate text-[13px] font-medium">{title}</span>
                <span className="text-fg-3 block text-[11px]">这个实体 · {number === null ? "v?" : `v${number}`}</span>
              </span>
            </>
          }
        />
      </Card>

      <div className="grid gap-4 md:grid-cols-[minmax(0,1fr)_280px]">
        <Card>
          <SectionTitle>{schema ? "字段" : "内容"}</SectionTitle>
          <Content version={selected} />
        </Card>
        <Card className="md:self-start">
          <SectionTitle>版本{versions.length > 1 ? ` · ${versions.length}` : ""}</SectionTitle>
          <VersionList
            chain={versions}
            selected={selected.versionId}
            onSelect={(version) => navigate({ search: { version }, replace: true })}
          />
        </Card>
      </div>

      {schema && <Instances schema={schema} />}

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

/** Schema 的实体页底部：去实体页看这个类型的实体。 */
function Instances({ schema }: { schema: Version<EntitySchema> }) {
  return (
    <Card className="mt-4">
      <Link
        to="/entities"
        search={{ type: schema.content.resourceName }}
        className="text-fg-2 hover:text-fg text-[13px]"
      >
        在实体页查看这个类型的实体 →
      </Link>
    </Card>
  );
}

function CopyId({ id }: { id: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      title="复制 EntityId"
      onClick={() => {
        void navigator.clipboard?.writeText(id).then(() => {
          setCopied(true);
          window.setTimeout(() => setCopied(false), 1500);
        });
      }}
      className="bg-panel/70 hover:bg-panel text-fg-2 inline-flex max-w-full cursor-pointer items-center gap-1.5 rounded-full px-2 py-0.5"
    >
      <Mono className="truncate text-[11px]">{id}</Mono>
      {copied ? <Check className="size-3 shrink-0" /> : <Copy className="size-3 shrink-0" />}
    </button>
  );
}
