"use client";

import { useState } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useAuth } from "react-oidc-context";
import { useQuery } from "@tanstack/react-query";
import type { DateRange } from "@/lib/dates";
import type { ListedObject } from "@/api/types";
import { fetchObjects } from "@/api/client";
import { FloatingNavigation } from "@/components/layout/FloatingNavigation";
import { LoadingState } from "@/components/status/LoadingState";
import { readViewRange } from "@/lib/viewRange";
import { objectHref, objectKind, objectName } from "./model";

export function ObjectDirectory() {
  const auth = useAuth();
  const token = auth.user?.access_token ?? "";
  const owner = auth.user?.profile.sub ?? "";
  const params = useSearchParams();
  const [search, setSearch] = useState("");
  const query = useQuery({
    queryKey: ["owner", owner, "objects"],
    queryFn: ({ signal }) => fetchObjects(token, signal),
    enabled: Boolean(token),
    refetchInterval: 60000,
  });
  const space = params.get("namespace");
  const objects = filterObjects(query.data?.objects ?? [], space, params.get("role"), search);
  return (
    <div className="app-frame">
      <FloatingNavigation />
      <main className="workspace object-directory">
        <nav className="page-breadcrumb" aria-label="当前位置">
          <Link href="/">概览</Link>
          <span>/</span>
          <span>{space ? objectKind(space) : "对象"}</span>
        </nav>
        <input
          aria-label="搜索对象"
          placeholder="搜索"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
        />
        {space ? <Link href="/objects">全部对象</Link> : null}
        <ObjectResults
          pending={query.isPending}
          failed={query.isError}
          objects={objects}
          range={readViewRange(params) ?? undefined}
        />
      </main>
    </div>
  );
}

function filterObjects(
  objects: ListedObject[],
  space: string | null,
  role: string | null,
  search: string,
) {
  const term = search.toLocaleLowerCase();
  return objects.filter((item) => {
    if (space && item.namespace !== space) return false;
    if (role && !item.roles.includes(role)) return false;
    return `${item.name ?? ""} ${item.key ?? ""} ${item.id}`.toLocaleLowerCase().includes(term);
  });
}

function ObjectResults({
  pending,
  failed,
  objects,
  range,
}: {
  pending: boolean;
  failed: boolean;
  objects: ListedObject[];
  range?: DateRange;
}) {
  if (pending) return <LoadingState label="正在读取对象" />;
  if (failed) return <p role="alert">对象读取失败。</p>;
  if (!objects.length) return <p>暂无对象。</p>;
  return (
    <div className="object-grid">
      {objects.map((item) => (
        <Link className="glass-panel object-card" key={item.id} href={objectHref(item.id, range)}>
          <span>{objectKind(item.namespace)}</span>
          <strong>{objectName(item)}</strong>
        </Link>
      ))}
    </div>
  );
}
