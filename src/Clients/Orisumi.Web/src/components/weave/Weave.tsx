import { useEffect, useRef, useState, type PointerEvent as ReactPointerEvent, type ReactNode } from "react";
import { useNavigate } from "@tanstack/react-router";
import { Loader2, Maximize, Minus, Plus, Quote } from "lucide-react";
import { timeLabel, useDescribe, useEntityInfo } from "@/hooks/useEntityInfo";
import { useVersion } from "@/hooks/queries";
import { useWeave } from "@/hooks/useWeave";
import { cn } from "@/lib/utils";
import { describeChanges } from "@/lib/versions";
import {
  bounds,
  edgeGeometry,
  heightOf,
  linked,
  NODE_W,
  nodeX,
  pathToRoot,
  type BundleNode,
  type ItemNode,
  type Weave,
  type WeaveNode,
} from "@/lib/weave";
import { InfoMark } from "../token/Token";
import { useHighlight, useStack } from "../token/context";
import { Message } from "../ui";

interface View {
  x: number;
  y: number;
  k: number;
}

const MIN_K = 0.3;
const MAX_K = 1.25;
/** 生长后画面对准新长出来的节点，最小缩到这里，字还看得清。 */
const FOCUS_K = 0.6;
const MARGIN = 72;
/** 底部留给状态和缩放按钮。 */
const FOOTER = 40;

/**
 * 织图的画布。来龙去脉从左往右：Observer 和依据在左，陈述在中间，内容和它关于的事物在右。
 * 点节点从两边长出它的邻居，再点收起；停在节点上，它长出来的那条路亮起。
 * 拖动背景平移，捏合或 ⌘ + 滚轮缩放；每次生长后画面对准新长出来的节点。
 */
export function WeaveCanvas({ rootId }: { rootId: string }) {
  const { weave, fresh, busy, message, error, toggle, toggleSource, open } = useWeave(rootId);
  const [box, setBox] = useState<HTMLDivElement | null>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [hover, setHover] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const highlight = useHighlight();

  useEffect(() => {
    if (!box) return;
    const observer = new ResizeObserver(([entry]) =>
      setSize({ width: Math.round(entry!.contentRect.width), height: Math.round(entry!.contentRect.height) }),
    );
    observer.observe(box);
    return () => observer.disconnect();
  }, [box]);

  // 画面默认对准刚长出来的节点；用户拖动或缩放后，直到图再变化之前都用用户的画面。
  const shape = weave?.nodes.map((node) => `${node.key}@${node.col},${node.y}`).join() ?? "";
  const [manual, setManual] = useState<{ shape: string; view: View } | null>(null);
  const fitted = fitView(weave ? focusOf(weave, fresh) : [], size.width, size.height, FOCUS_K);
  const view = manual && manual.shape === shape ? manual.view : fitted;
  const [smooth, setSmooth] = useState(true);
  const setView = (next: View) => setManual({ shape, view: next });

  function fit() {
    setSmooth(true);
    setView(fitView(weave?.nodes ?? [], size.width, size.height, MIN_K));
  }

  // 捏合（带 ctrlKey 的 wheel）或 ⌘ + 滚轮缩放，其余滚动平移。
  const viewRef = useRef(view);
  useEffect(() => {
    viewRef.current = view;
  });
  useEffect(() => {
    if (!box) return;
    function onWheel(event: WheelEvent) {
      event.preventDefault();
      setSmooth(false);
      const rect = box!.getBoundingClientRect();
      const current = viewRef.current;
      if (event.ctrlKey || event.metaKey) {
        const k = Math.min(MAX_K, Math.max(MIN_K, current.k * Math.exp(-event.deltaY * 0.01)));
        const px = event.clientX - rect.left;
        const py = event.clientY - rect.top;
        setManual({
          shape,
          view: { k, x: px - ((px - current.x) / current.k) * k, y: py - ((py - current.y) / current.k) * k },
        });
      } else setManual({ shape, view: { ...current, x: current.x - event.deltaX, y: current.y - event.deltaY } });
    }
    box.addEventListener("wheel", onWheel, { passive: false });
    return () => box.removeEventListener("wheel", onWheel);
  }, [box, shape]);

  const drag = useRef<{ x: number; y: number; view: View } | null>(null);
  function onDown(event: ReactPointerEvent<HTMLDivElement>) {
    if (event.button !== 0 || (event.target as Element).closest("[data-node]")) return;
    drag.current = { x: event.clientX, y: event.clientY, view };
    setSmooth(false);
    setSelected(null);
    event.currentTarget.setPointerCapture(event.pointerId);
  }
  function onMove(event: ReactPointerEvent<HTMLDivElement>) {
    const state = drag.current;
    if (!state) return;
    setView({ ...state.view, x: state.view.x + event.clientX - state.x, y: state.view.y + event.clientY - state.y });
  }
  function zoom(factor: number) {
    setSmooth(true);
    const k = Math.min(MAX_K, Math.max(MIN_K, view.k * factor));
    const cx = size.width / 2;
    const cy = size.height / 2;
    setView({ k, x: cx - ((cx - view.x) / view.k) * k, y: cy - ((cy - view.y) / view.k) * k });
  }

  if (error) return <Message title="织不起来" detail={error} mascot />;

  const trace = new Set(hover && weave ? pathToRoot(weave, hover) : []);
  const byKey = new Map((weave?.nodes ?? []).map((node) => [node.key, node]));
  const onTrace = (from: string, to: string) => trace.has(from) && trace.has(to) && byKey.get(to)?.parent === from;

  return (
    <div
      ref={setBox}
      className="bg-card border-line shadow-card relative h-[max(480px,calc(100vh-190px))] cursor-grab touch-none overflow-hidden rounded-2xl border select-none active:cursor-grabbing"
      onPointerDown={onDown}
      onPointerMove={onMove}
      onPointerUp={() => (drag.current = null)}
      onPointerCancel={() => (drag.current = null)}
    >
      <p aria-hidden="true" className="text-fg-3 pointer-events-none absolute top-3 left-4 text-[11px]">
        ← 来源与依据
      </p>
      <p aria-hidden="true" className="text-fg-3 pointer-events-none absolute top-3 right-4 text-[11px]">
        内容与它关于的 →
      </p>

      {!weave ? (
        <div className="absolute inset-0 grid place-items-center">
          <p className="text-fg-3 flex items-center gap-2 text-[13px]">
            <Loader2 className="size-4 animate-spin" />
            正在读取起点
          </p>
        </div>
      ) : (
        <div
          className={cn(
            "absolute top-0 left-0 origin-top-left",
            smooth && "transition-transform duration-300 ease-out",
          )}
          style={{ transform: `translate(${view.x}px, ${view.y}px) scale(${view.k})` }}
        >
          <svg className="absolute top-0 left-0 overflow-visible" width={1} height={1} aria-hidden="true">
            {weave.edges.map((edge) => {
              const from = byKey.get(edge.from);
              const to = byKey.get(edge.to);
              if (!from || !to) return null;
              const lit = onTrace(edge.from, edge.to);
              const tree = to.parent === from.key;
              const { d, label } = edgeGeometry(from, to);
              const faded = !!hover && !lit;
              return (
                <g
                  key={`${edge.from}>${edge.to}`}
                  className="transition-opacity duration-150"
                  opacity={faded ? 0.15 : 1}
                >
                  <path
                    d={d}
                    pathLength={1}
                    fill="none"
                    stroke={lit ? "var(--accent)" : "var(--line-3)"}
                    strokeWidth={lit ? 2 : 1.1}
                    strokeDasharray={tree ? undefined : "4 4"}
                    opacity={lit ? 1 : 0.5}
                    className={cn(fresh.has(edge.to) && tree && "weave-draw")}
                  />
                  <text
                    x={label.x}
                    y={label.y}
                    textAnchor={label.anchor}
                    style={{
                      fontSize: 11,
                      fill: lit ? "var(--fg)" : "var(--fg-3)",
                      fontWeight: lit ? 500 : 400,
                    }}
                  >
                    {edge.label}
                  </text>
                </g>
              );
            })}
          </svg>
          {weave.nodes.map((node) => {
            const common = {
              fresh: fresh.has(node.key),
              busy: busy === node.key,
              dim: !!hover && !trace.has(node.key),
              onHover: (on: boolean) => {
                setHover(on ? node.key : null);
                highlight.set(on ? node.key : null);
              },
            };
            if (node.kind === "bundle")
              return (
                <BundleCard
                  key={node.key}
                  node={node}
                  {...common}
                  onClick={() => {
                    setSmooth(true);
                    void open(node.key);
                  }}
                />
              );
            const props = {
              node,
              ...common,
              root: node.parent === null,
              selected: selected === node.key,
              onClick: () => {
                setSmooth(true);
                setSelected(node.key);
                void toggle(node.key);
              },
            };
            if (node.kind === "entity") return <EntityCard key={node.key} {...props} />;
            const observer = node.payload.kind === "knot" ? node.payload.observation.content.observerVersionId : null;
            const source = weave.nodes.find(
              (item) =>
                item.kind === "entity" && item.payload.kind === "entity" && item.payload.version.versionId === observer,
            );
            return (
              <KnotCard
                key={node.key}
                {...props}
                sourceShown={!!source && linked(weave, node.key, source.key)}
                onSource={() => {
                  setSmooth(true);
                  setSelected(node.key);
                  void toggleSource(node.key);
                }}
              />
            );
          })}
        </div>
      )}

      <div className="border-line bg-card shadow-card absolute right-3 bottom-3 flex items-center gap-0.5 rounded-lg border p-0.5">
        <ToolButton label="缩小" onClick={() => zoom(1 / 1.25)}>
          <Minus className="size-3.5" />
        </ToolButton>
        <ToolButton label="放大" onClick={() => zoom(1.25)}>
          <Plus className="size-3.5" />
        </ToolButton>
        <ToolButton label="适应画面" onClick={fit}>
          <Maximize className="size-3.5" />
        </ToolButton>
      </div>
      <p
        aria-live="polite"
        className="text-fg-2 pointer-events-none absolute bottom-3.5 left-4 max-w-[calc(100%-140px)] truncate text-xs"
      >
        {message}
      </p>
    </div>
  );
}

function ToolButton({ label, onClick, children }: { label: string; onClick: () => void; children: ReactNode }) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      onClick={onClick}
      className="text-fg-2 hover:text-fg hover:bg-panel grid size-7 cursor-pointer place-items-center rounded-md"
    >
      {children}
    </button>
  );
}

/** 刚长出来的节点和它们长出来的地方；没有新节点时是全部节点。 */
function focusOf(weave: Weave, fresh: ReadonlySet<string>): WeaveNode[] {
  const added = weave.nodes.filter((node) => fresh.has(node.key));
  if (!added.length) return weave.nodes;
  const parents = new Set(added.map((node) => node.parent));
  return weave.nodes.filter((node) => fresh.has(node.key) || parents.has(node.key));
}

/** 收拢到一组节点的画面：最大不超过原尺寸，最小到 minK。 */
function fitView(nodes: readonly WeaveNode[], width: number, height: number, minK: number): View {
  if (!nodes.length || !width) return { x: width / 2, y: height / 2, k: 1 };
  const b = bounds(nodes);
  const k = Math.max(
    minK,
    Math.min(1, (width - MARGIN * 2) / Math.max(b.width, 1), (height - FOOTER - MARGIN * 2) / Math.max(b.height, 1)),
  );
  return { k, x: width / 2 - (b.x + b.width / 2) * k, y: (height - FOOTER) / 2 - (b.y + b.height / 2) * k };
}

function place(node: WeaveNode) {
  const height = heightOf(node);
  return { left: nodeX(node) - NODE_W / 2, top: node.y - height / 2, width: NODE_W, height };
}

interface CardProps {
  node: ItemNode;
  root: boolean;
  fresh: boolean;
  busy: boolean;
  selected: boolean;
  dim: boolean;
  onHover: (on: boolean) => void;
  onClick: () => void;
}

/** 节点外面的一层：位置、选中后下面的“详情”和“从这里开始”。 */
function Frame({
  node,
  root,
  selected,
  panelId,
  children,
}: {
  node: ItemNode;
  root: boolean;
  selected: boolean;
  panelId: string;
  children: ReactNode;
}) {
  const stack = useStack();
  const navigate = useNavigate();
  return (
    <div data-node="" className="absolute" style={place(node)}>
      {children}
      {selected && (
        <div className="pop absolute top-full left-1/2 z-10 mt-1.5 flex -translate-x-1/2 gap-1 whitespace-nowrap">
          <button
            type="button"
            onClick={() => stack.open(panelId, -1)}
            className="bg-card border-line text-fg-2 hover:text-fg shadow-card cursor-pointer rounded-md border px-2 py-0.5 text-xs"
          >
            详情
          </button>
          {!root && (
            <button
              type="button"
              onClick={() => void navigate({ to: "/entities", search: { root: panelId } })}
              className="bg-card border-line text-fg-2 hover:text-fg shadow-card cursor-pointer rounded-md border px-2 py-0.5 text-xs"
            >
              从这里开始
            </button>
          )}
        </div>
      )}
    </div>
  );
}

function cardClass({ node, root, fresh, selected, dim }: CardProps) {
  return cn(
    "flex size-full cursor-pointer items-center gap-2.5 border px-2.5 text-left transition-[opacity,border-color,box-shadow] duration-150",
    node.expanded ? "border-line-3" : "border-line-2 hover:border-line-3",
    root && "ring-accent/30 ring-2",
    selected && "shadow-float",
    dim && "opacity-35",
    fresh && "weave-bloom",
  );
}

function Toggle({ node, busy }: { node: ItemNode; busy: boolean }) {
  return busy ? (
    <Loader2 aria-hidden="true" className="text-fg-3 size-3.5 shrink-0 animate-spin" />
  ) : (
    <span aria-hidden="true" className="text-fg-3 w-3 shrink-0 text-center text-xs">
      {node.expanded ? "−" : "+"}
    </span>
  );
}

/** 一个实体：记号、名字；经历的下面写事实时间和记下它的 Observer，其余写类型名。 */
function EntityCard(props: CardProps) {
  const { node, onHover, onClick } = props;
  const payload = node.payload.kind === "entity" ? node.payload : null;
  const { info } = useDescribe(payload?.version);
  const observation = payload?.observation ?? null;
  const observer = useEntityInfo(observation?.content.observerVersionId);
  const sub = !info
    ? ""
    : observation
      ? `${timeLabel(observation.content.startAt, observation.content.endAt)} · ${observer.info?.title ?? "…"}`
      : info.title === info.typeName
        ? info.roleName
        : info.typeName;
  return (
    <Frame node={node} root={props.root} selected={props.selected} panelId={node.key}>
      <button
        type="button"
        aria-expanded={node.expanded}
        aria-label={info ? `${info.typeName} ${info.title}` : "实体"}
        onClick={onClick}
        onPointerEnter={() => onHover(true)}
        onPointerLeave={() => onHover(false)}
        onFocus={() => onHover(true)}
        onBlur={() => onHover(false)}
        className={cn("bg-card rounded-xl", cardClass(props))}
      >
        {info ? (
          <InfoMark info={info} size="lg" className="!size-8 !rounded-lg" />
        ) : (
          <span className="bg-panel size-8 shrink-0 rounded-lg" />
        )}
        <span className="min-w-0 flex-1 leading-tight">
          <span className="block truncate text-[13px] font-medium">{info?.title ?? "…"}</span>
          <span className="text-fg-3 block truncate text-[11px] tabular-nums">{sub}</span>
        </span>
        <Toggle node={node} busy={props.busy} />
      </button>
    </Frame>
  );
}

/**
 * 一个结：一份 Observation，写着谁说的、这次改了什么、什么时候，以及依据了几个输入。
 * 点结展开依据和其他邻居；Observer 的名字是另一个按钮，点它长出或收起来源。
 */
function KnotCard(props: CardProps & { sourceShown: boolean; onSource: () => void }) {
  const { node, onHover, onClick, sourceShown, onSource } = props;
  const payload = node.payload.kind === "knot" ? node.payload : null;
  const observer = useEntityInfo(payload?.observation.content.observerVersionId);
  const who = observer.info?.title ?? "…";
  const when = payload ? timeLabel(payload.observation.content.startAt, payload.observation.content.endAt) : "";
  const inputs = payload?.inputSet?.content.members.length ?? 0;
  const content = useVersion(payload?.observation.content.contentVersionId);
  const previous = useVersion(content.data?.previousVersionId);
  const changes =
    content.data && (!content.data.previousVersionId || previous.data)
      ? describeChanges(previous.data ?? null, content.data).join("；")
      : "…";
  return (
    <Frame node={node} root={props.root} selected={props.selected} panelId={node.key}>
      <div
        onPointerEnter={() => onHover(true)}
        onPointerLeave={() => onHover(false)}
        className={cn("bg-panel relative rounded-[28px] pl-1.5", cardClass(props))}
      >
        <button
          type="button"
          aria-expanded={node.expanded}
          aria-label={`陈述 ${who} ${when}`}
          onClick={onClick}
          onFocus={() => onHover(true)}
          onBlur={() => onHover(false)}
          className="focus-visible:ring-accent/50 absolute inset-0 cursor-pointer rounded-[28px] focus-visible:ring-2 focus-visible:outline-none"
        />
        <span className="bg-card text-fg-2 pointer-events-none relative grid size-8 shrink-0 place-items-center rounded-full">
          <Quote aria-hidden="true" className="size-3.5" />
        </span>
        <span className="pointer-events-none relative min-w-0 flex-1 leading-tight">
          <button
            type="button"
            aria-pressed={sourceShown}
            aria-label={`来源 ${who}`}
            title={sourceShown ? "收起来源" : "长出来源"}
            onClick={onSource}
            onFocus={() => onHover(true)}
            onBlur={() => onHover(false)}
            className={cn(
              "pointer-events-auto inline-block max-w-full cursor-pointer truncate rounded text-left align-top text-[13px] font-medium underline-offset-2 hover:underline",
              sourceShown && "text-accent",
            )}
          >
            {who}
          </button>
          <span title={changes} className="text-fg-2 line-clamp-2 text-xs wrap-anywhere">
            {changes}
          </span>
          <span className="text-fg-3 block truncate text-[11px] tabular-nums">
            陈述 · {when}
            {inputs > 0 && ` · 依据 ${inputs} 个`}
          </span>
        </span>
        <span className="pointer-events-none relative">
          <Toggle node={node} busy={props.busy} />
        </span>
      </div>
    </Frame>
  );
}

/** 一束同类的邻居：写着几个、还剩几个和它们的类型；点一次再放出几个。 */
function BundleCard({
  node,
  fresh,
  busy,
  dim,
  onHover,
  onClick,
}: {
  node: BundleNode;
  fresh: boolean;
  busy: boolean;
  dim: boolean;
  onHover: (on: boolean) => void;
  onClick: () => void;
}) {
  return (
    <div data-node="" className="absolute" style={place(node)}>
      <button
        type="button"
        onClick={onClick}
        onPointerEnter={() => onHover(true)}
        onPointerLeave={() => onHover(false)}
        title={`经由 ${node.label}`}
        className={cn(
          "bg-card text-fg-2 hover:text-fg border-line-3 flex size-full cursor-pointer items-center gap-2 rounded-xl border border-dashed px-3 text-left leading-tight transition-opacity duration-150",
          "shadow-[4px_4px_0_-1px_var(--card),4px_4px_0_0_var(--line-2),8px_8px_0_-1px_var(--card),8px_8px_0_0_var(--line)]",
          dim && "opacity-35",
          fresh && "weave-bloom",
        )}
      >
        <span className="min-w-0 flex-1">
          <span className="block text-[13px] font-medium tabular-nums">
            {node.more ? "还有 " : ""}
            {node.members.length} 个
          </span>
          <span className="text-fg-3 block truncate text-[11px]">{node.typeName}</span>
        </span>
        {busy && <Loader2 aria-hidden="true" className="text-fg-3 size-3.5 shrink-0 animate-spin" />}
      </button>
    </div>
  );
}
