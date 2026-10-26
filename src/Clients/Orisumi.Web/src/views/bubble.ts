import { forceCollide, forceSimulation, forceX, forceY, type Simulation, type SimulationNodeDatum } from "d3-force";
import { isActive } from "@/lib/activity";
import { CATEGORIES, type CategoryKey } from "@/lib/categories";
import { SECOND } from "@/lib/time";
import { color, hit, label, n, type Renderer } from "./common";

const HEIGHT = 320;
const FIELD = 285;
const MIN_RADIUS = 9;
const LABEL_RADIUS = 24;
const RIPPLE = 1.2 * SECOND;

interface Node extends SimulationNodeDatum {
  key: string;
  category: CategoryKey;
  r: number;
}

export function targetRadius(writes: number): number {
  return MIN_RADIUS + Math.sqrt(writes) * 2.3;
}

/** 气泡：面积和 10 分钟内的写入次数成正比，同类别聚在一起。布局状态保存在渲染器内。 */
export function createBubbleRenderer(): Renderer {
  const nodes = new Map<string, Node>();
  let lastFrame = 0;
  let columns = new Map<CategoryKey, number>();
  let fieldWidth = 0;
  const simulation: Simulation<Node, undefined> = forceSimulation<Node>([])
    .force("x", forceX<Node>((d) => columns.get(d.category) ?? fieldWidth / 2).strength(0.05))
    .force("y", forceY<Node>(FIELD / 2).strength(0.07))
    .force("collide", forceCollide<Node>((d) => d.r + 3).iterations(2))
    .alphaTarget(0.04)
    .stop();

  return ({ width, observers, now, selected, reduced }) => {
    fieldWidth = width;
    const present = CATEGORIES.filter((c) => observers.some((o) => o.category === c.key));
    columns = new Map(present.map((c, i) => [c.key, ((i + 0.5) / present.length) * width]));

    const dt = lastFrame ? Math.min(1, (now - lastFrame) / SECOND) : 1;
    lastFrame = now;
    let added = false;
    for (const observer of observers) {
      const target = targetRadius(observer.writes.length);
      let node = nodes.get(observer.key);
      if (!node) {
        const x = columns.get(observer.category) ?? fieldWidth / 2;
        node = {
          key: observer.key,
          category: observer.category,
          r: target,
          x: x + (Math.random() - 0.5) * 40,
          y: FIELD / 2,
        };
        nodes.set(observer.key, node);
        added = true;
      }
      node.r = reduced ? target : node.r + (target - node.r) * Math.min(1, dt * 3);
    }
    for (const key of nodes.keys()) {
      if (!observers.some((o) => o.key === key)) {
        nodes.delete(key);
        added = true;
      }
    }
    if (added) simulation.nodes([...nodes.values()]);
    (simulation.force("x") as ReturnType<typeof forceX<Node>>).x((d) => columns.get(d.category) ?? fieldWidth / 2);
    simulation.tick();

    let markup = "";
    for (const observer of observers) {
      const node = nodes.get(observer.key)!;
      node.x = Math.max(node.r, Math.min(width - node.r, node.x ?? 0));
      node.y = Math.max(node.r, Math.min(FIELD - node.r, node.y ?? 0));
      const active = isActive(observer, now);
      const c = color(observer);
      let inner =
        `<circle cx="${n(node.x)}" cy="${n(node.y)}" r="${n(node.r)}" fill="${active ? c : "none"}" fill-opacity="0.16"` +
        ` stroke="${active ? c : "var(--line-2)"}" stroke-width="${selected === observer.key ? 2 : 1}"/>`;
      if (!reduced) {
        const age = now - observer.lastSeenAt;
        if (age >= 0 && age <= RIPPLE) {
          inner += `<circle cx="${n(node.x)}" cy="${n(node.y)}" r="${n(node.r + (age / SECOND) * 16)}" fill="none" stroke="${c}" stroke-opacity="${n(1 - age / RIPPLE)}"/>`;
        }
      }
      if (node.r >= LABEL_RADIUS) {
        inner += label(n(node.x), n(node.y), observer.name, "primary", { anchor: "middle" });
        inner += label(n(node.x), n(node.y + 15), String(observer.writes.length), "muted", { anchor: "middle" });
      }
      markup += hit(observer, selected, inner);
    }
    for (const category of present) {
      markup += label(n(columns.get(category.key)!), 312, category.label, "muted", { anchor: "middle" });
    }
    return { height: HEIGHT, markup };
  };
}
