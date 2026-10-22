import { useMemo, useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { CheckCircle2, Send, Sparkles } from "lucide-react";
import { useSaveEntity } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { Badge } from "@/components/ui/badge";
import { uuidv7, isUuidv7 } from "@/lib/uuid";
import { cn } from "@/lib/utils";

export const Route = createFileRoute("/compose")({
  component: ComposePage,
});

type Kind = "observer" | "observation" | "schema" | "business";

const KINDS: { id: Kind; name: string; segment: string; hint: string }[] = [
  { id: "observer", name: "观测主体 Observer", segment: "observers", hint: "properties 只需 name" },
  { id: "observation", name: "观测 Observation", segment: "observations", hint: "三个引用 + startAt/endAt/timeZone" },
  { id: "schema", name: "实体定义 EntitySchema", segment: "schemas", hint: "name / resourceName / fields" },
  { id: "business", name: "业务实体", segment: "business", hint: "路径段填 resourceName" },
];

function ComposePage() {
  const navigate = useNavigate();
  const save = useSaveEntity();
  const [kind, setKind] = useState<Kind>("observer");
  const [id, setId] = useState("");
  const [resourceName, setResourceName] = useState("");
  const [referencesText, setReferencesText] = useState("{}");
  const [propertiesText, setPropertiesText] = useState('{\n  "name": ""\n}');

  const parsedRefs = useMemo(() => tryParse(referencesText), [referencesText]);
  const parsedProps = useMemo(() => tryParse(propertiesText), [propertiesText]);

  const validation = useMemo(() => {
    const errors: string[] = [];
    if (!isUuidv7(id)) errors.push("ID 不是合法 UUIDv7");
    if (!parsedRefs.ok) errors.push(`references 不是合法 JSON 对象：${parsedRefs.error}`);
    if (!parsedProps.ok) errors.push(`properties 不是合法 JSON 对象：${parsedProps.error}`);
    if (kind === "business" && !/^[a-z][a-z0-9-]{1,62}[a-z0-9]$/.test(resourceName))
      errors.push("resourceName 需为小写字母开头、含小写字母/数字/连字符的标识");
    if (kind === "observer" && parsedProps.ok && typeof parsedProps.value.name !== "string")
      errors.push("Observer 的 properties.name 必须是字符串");
    return errors;
  }, [id, parsedRefs, parsedProps, kind, resourceName]);

  const segment = kind === "business" ? resourceName : KINDS.find((k) => k.id === kind)!.segment;

  const submit = async () => {
    if (validation.length > 0 || !parsedRefs.ok || !parsedProps.ok) return;
    await save.mutateAsync({
      segment,
      id,
      references: parsedRefs.value as Record<string, string | null>,
      properties: parsedProps.value as Record<string, unknown>,
    });
    void navigate({ to: "/entities/$id", params: { id }, search: { view: "inspect" } });
  };

  const preview = {
    method: "PUT",
    path: `/api/entities/${segment || "<segment>"}/${id || "<uuidv7>"}`,
    body: { references: parsedRefs.ok ? parsedRefs.value : {}, properties: parsedProps.ok ? parsedProps.value : {} },
  };

  return (
    <div className="mx-auto grid max-w-5xl gap-6">
      <header className="grid gap-2">
        <h1 className="flex items-center gap-2 text-2xl font-bold tracking-tight">
          <Sparkles className="text-primary size-6" /> 写入实体
        </h1>
        <p className="text-muted-foreground text-sm">
          所有实体用同一套保存语义：客户端生成 UUIDv7，请求体外层只有 references 与 properties。首次创建返回
          201，重复/替换返回 204。
        </p>
      </header>

      <div className="grid gap-2">
        <Label>实体类型</Label>
        <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-4">
          {KINDS.map((k) => (
            <button
              key={k.id}
              type="button"
              onClick={() => setKind(k.id)}
              className={cn(
                "rounded-xl border p-3 text-left transition-all",
                kind === k.id
                  ? "border-primary/50 bg-primary/8 ring-primary/30 ring-1"
                  : "hover:border-primary/30 hover:bg-accent/50",
              )}
            >
              <p className="text-sm font-semibold">{k.name}</p>
              <p className="text-muted-foreground mt-0.5 text-xs">{k.hint}</p>
            </button>
          ))}
        </div>
      </div>

      <div className="grid gap-5 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle className="text-base">请求</CardTitle>
          </CardHeader>
          <CardContent className="grid gap-4">
            <div className="grid gap-2">
              <div className="flex items-center justify-between">
                <Label htmlFor="entity-id">实体 ID（UUIDv7）</Label>
                <Button type="button" variant="ghost" size="sm" className="h-7 text-xs" onClick={() => setId(uuidv7())}>
                  生成新 ID
                </Button>
              </div>
              <Input
                id="entity-id"
                value={id}
                onChange={(e) => setId(e.target.value.trim())}
                placeholder="0192b056-7300-7000-8000-000000000001"
                className="font-mono"
              />
            </div>

            {kind === "business" && (
              <div className="grid gap-2">
                <Label htmlFor="resource">resourceName（已登记的业务资源）</Label>
                <Input
                  id="resource"
                  value={resourceName}
                  onChange={(e) => setResourceName(e.target.value.trim())}
                  placeholder="example-records"
                  className="font-mono"
                />
              </div>
            )}

            <div className="grid gap-2">
              <Label htmlFor="refs">references</Label>
              <Textarea
                id="refs"
                value={referencesText}
                onChange={(e) => setReferencesText(e.target.value)}
                rows={6}
                className="font-mono text-xs"
                spellCheck={false}
              />
            </div>

            <div className="grid gap-2">
              <Label htmlFor="props">properties</Label>
              <Textarea
                id="props"
                value={propertiesText}
                onChange={(e) => setPropertiesText(e.target.value)}
                rows={9}
                className="font-mono text-xs"
                spellCheck={false}
              />
            </div>

            {validation.length > 0 ? (
              <ul className="border-destructive/30 bg-destructive/5 text-destructive grid gap-1 rounded-lg border p-3 text-xs">
                {validation.map((v) => (
                  <li key={v}>· {v}</li>
                ))}
              </ul>
            ) : (
              <p className="text-cat-entity flex items-center gap-1.5 text-xs">
                <CheckCircle2 className="size-3.5" /> 契约检查通过，可以提交
              </p>
            )}

            <Button onClick={submit} disabled={validation.length > 0 || save.isPending} className="w-full">
              <Send className="size-4" />
              {save.isPending ? "提交中…" : `PUT 保存${KINDS.find((k) => k.id === kind)?.name}`}
            </Button>
            {save.isError && (
              <p className="bg-destructive/8 text-destructive rounded-lg p-2 text-xs">
                {save.error instanceof Error ? save.error.message : "保存失败"}
              </p>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="text-base">请求预览</CardTitle>
          </CardHeader>
          <CardContent className="grid gap-3">
            <Badge variant="soft" className="font-mono text-xs">
              {preview.method} {preview.path}
            </Badge>
            <pre className="bg-background/60 min-h-64 flex-1 overflow-auto rounded-lg border p-4 font-mono text-xs leading-relaxed">
              {JSON.stringify(preview.body, null, 2)}
            </pre>
            <p className="text-muted-foreground text-xs">
              保存语义：实体不存在 → 201 Created；实体存在且类别一致 → 204 完整替换；类别/Schema 冲突 → 409；资源未登记
              → 404。
            </p>
          </CardContent>
        </Card>
      </div>
    </div>
  );
}

function tryParse(text: string): { ok: true; value: unknown } | { ok: false; error: string } {
  try {
    const value = JSON.parse(text) as unknown;
    if (typeof value !== "object" || value === null || Array.isArray(value)) {
      return { ok: false, error: "必须是 JSON 对象" };
    }
    return { ok: true, value };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : String(e) };
  }
}
