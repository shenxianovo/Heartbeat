"use client";

import { useMemo } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { useAuth } from "react-oidc-context";
import type { ObservedObject } from "@/api/types";
import type { DateRange } from "@/lib/dates";
import { fetchObject, fetchObjects } from "@/api/client";
import { FloatingNavigation } from "@/components/layout/FloatingNavigation";
import { LoadingState } from "@/components/status/LoadingState";
import { ReplayWorkbench } from "@/components/replay/ReplayWorkbench";
import { VRChatDashboard } from "@/components/vrchat/VRChatDashboard";
import { buttonVariants } from "@/components/ui/button";
import { readViewRange } from "@/lib/viewRange";
import { ObjectScopeProvider } from "./ObjectScope";
import { objectHref, objectKind, objectName, relatedObjectHref } from "./model";

export function ObjectWorkspace({ id }: { id: string }) {
  const auth = useAuth();
  const token = auth.user?.access_token ?? "";
  const owner = auth.user?.profile.sub ?? "";
  const query = useQuery({
    queryKey: ["owner", owner, "object", id],
    queryFn: ({ signal }) => fetchObject(token, id, signal),
    enabled: Boolean(token),
  });
  const object = query.data;
  return (
    <div className="app-frame">
      <FloatingNavigation />
      {query.isPending ? (
        <LoadingState label="正在读取对象" fullPage />
      ) : !object ? (
        <main className="workspace">
          <p role="alert">{query.error?.message || "找不到这个对象"}</p>
          <Link href="/objects">浏览对象</Link>
        </main>
      ) : (
        <ObjectContents object={object} token={token} owner={owner} />
      )}
    </div>
  );
}

function ObjectContents({
  object,
  token,
  owner,
}: {
  object: ObservedObject;
  token: string;
  owner: string;
}) {
  const id = object.id;
  const params = useSearchParams();
  const contextKey = params
    .getAll("context")
    .filter((item) => item !== id)
    .join("/");
  const context = useMemo(
    () => (contextKey ? [...new Set(contextKey.split("/"))] : []),
    [contextKey],
  );
  const scope = useMemo(() => ({ objectId: id, contextObjectIds: context }), [id, context]);
  const range = readViewRange(params) ?? undefined;
  const habitat = object.namespace === "vrchat.account";
  const view = params.get("view") === "summary" && habitat ? "summary" : "timeline";
  function viewHref(next: string) {
    const search = new URLSearchParams(params.toString());
    search.set("view", next);
    return `/objects/${encodeURIComponent(id)}?${search}`;
  }
  return (
    <>
      <header className="workspace object-heading">
        <nav className="page-breadcrumb" aria-label="当前位置">
          <Link href="/">概览</Link>
          <span>/</span>
          <Link href="/objects">对象</Link>
        </nav>
        <div className="object-title">
          <h1>{objectName(object)}</h1>
          <span>{objectKind(object.namespace)}</span>
        </div>
        <div className="object-actions">
          {context.length ? (
            <>
              <Link href={objectHref(context[context.length - 1]!, range, context)}>
                返回上一级
              </Link>
              <Link href={objectHref(id, range)}>全部记录</Link>
            </>
          ) : null}
          {habitat ? (
            <nav className="object-tabs" aria-label="对象视图">
              {(
                [
                  ["timeline", "时间线"],
                  ["summary", "世界与相遇"],
                ] as const
              ).map(([name, label]) => (
                <Link
                  key={name}
                  className={buttonVariants({ variant: view === name ? "glassPrimary" : "glass" })}
                  href={viewHref(name)}
                >
                  {label}
                </Link>
              ))}
            </nav>
          ) : null}
        </div>
        <RelatedObjects id={id} context={context} range={range} token={token} owner={owner} />
      </header>
      <ObjectScopeProvider scope={scope} range={range} key={`${id}/${context ?? ""}`}>
        {view === "summary" ? <VRChatDashboard /> : <ReplayWorkbench />}
      </ObjectScopeProvider>
    </>
  );
}

function RelatedObjects({
  id,
  context,
  range,
  token,
  owner,
}: {
  id: string;
  context: string[];
  range?: DateRange;
  token: string;
  owner: string;
}) {
  const related = useQuery({
    queryKey: ["owner", owner, "objects", id, context],
    queryFn: ({ signal }) => fetchObjects(token, signal, context, id),
    enabled: Boolean(token),
  });
  return (
    <>
      <nav className="object-related" aria-label="相关对象">
        {(related.data?.objects ?? [])
          .filter((item) => item.id !== id)
          .map((item) => (
            <Link
              key={item.id}
              href={relatedObjectHref(item.id, range, { objectId: id, contextObjectIds: context })}
            >
              {objectName(item)}
            </Link>
          ))}
      </nav>
      {related.isError ? <p role="alert">相关对象读取失败。</p> : null}
    </>
  );
}
