import { Link, createFileRoute } from "@tanstack/react-router";
import { ArrowRight, Clock3, Network, Boxes } from "lucide-react";
import { IdSearchBar } from "@/components/IdSearchBar";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { useEntityHistory } from "@/lib/storage";
import { CATEGORY_STYLE } from "@/lib/labels";
import { useAppearance } from "@/lib/storage";

export const Route = createFileRoute("/")({
  component: HomePage,
});

const FEATURES = [
  { icon: Network, title: "引用关系图", desc: "从任意实体沿 references 展开观测主体、内容与定义的关系网。" },
  { icon: Boxes, title: "实体检视", desc: "类别、属性、引用与原始 JSON 一卡呈现，省略与 null 严格区分。" },
  { icon: Clock3, title: "事实时间", desc: "观测按 startAt/endAt 投影到时间轴；结束未知以渐隐表达，绝不补白。" },
];

function HomePage() {
  const { history, clear } = useEntityHistory();
  const { mascot } = useAppearance();

  return (
    <div className="grid gap-10">
      <section className="animate-fade-in grid gap-6 pt-4 text-center md:pt-10">
        <div className="mx-auto flex justify-center">
          <Badge variant="soft" className="rounded-full px-3 py-1">
            {mascot === "windows" ? "Windows 娘当值 · 清醒模式" : "Mac 娘当值 · 安静模式"}
          </Badge>
        </div>
        <h1 className="mx-auto max-w-3xl text-4xl leading-tight font-extrabold tracking-tight md:text-6xl">
          从一次<span className="gradient-text">观测</span>开始，
          <br className="hidden md:block" />
          走进实体之间的关系。
        </h1>
        <p className="text-muted-foreground mx-auto max-w-xl text-base md:text-lg">
          Heartbeat 把主体、内容、定义与事实时间连成一张图。输入任意实体 ID，沿引用逐层展开。
        </p>
        <div className="mx-auto flex w-full max-w-2xl justify-center">
          <IdSearchBar large />
        </div>
        <div className="mx-auto flex items-center gap-6">
          {(["observer", "observation", "entity_schema", "entity"] as const).map((c) => (
            <span key={c} className="text-muted-foreground flex items-center gap-1.5 text-xs">
              <span className={`size-2 rounded-full ${CATEGORY_STYLE[c].dot}`} />
              {CATEGORY_STYLE[c].label}
            </span>
          ))}
        </div>
      </section>

      <section className="grid gap-4 md:grid-cols-3">
        {FEATURES.map((f) => (
          <Card key={f.title} className="transition-transform hover:-translate-y-0.5">
            <CardContent className="flex flex-col gap-3 p-6">
              <span className="bg-primary/10 flex size-10 items-center justify-center rounded-xl">
                <f.icon className="text-primary size-5" />
              </span>
              <h2 className="font-semibold">{f.title}</h2>
              <p className="text-muted-foreground text-sm leading-relaxed">{f.desc}</p>
            </CardContent>
          </Card>
        ))}
      </section>

      {history.length > 0 && (
        <section className="grid gap-3">
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-semibold">最近访问</h2>
            <button onClick={clear} className="text-muted-foreground hover:text-foreground text-xs">
              清空足迹
            </button>
          </div>
          <div className="grid gap-2 md:grid-cols-2">
            {history.map((h) => (
              <Link
                key={h.id}
                to="/entities/$id"
                params={{ id: h.id }}
                className="glass group hover:bg-accent/60 flex items-center gap-3 rounded-xl p-3 transition-colors"
              >
                <span
                  className={`size-2.5 shrink-0 rounded-full ${CATEGORY_STYLE[h.category as keyof typeof CATEGORY_STYLE]?.dot ?? "bg-muted-foreground"}`}
                />
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium">{h.label ?? "未命名实体"}</p>
                  <p className="text-muted-foreground truncate font-mono text-xs">{h.id}</p>
                </div>
                <ArrowRight className="text-muted-foreground size-4 shrink-0 transition-transform group-hover:translate-x-0.5" />
              </Link>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}
