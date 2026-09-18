import { expect, test, type CDPSession, type Page } from "@playwright/test";
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";

import { identityRoutes, seedSession } from "../e2e/fixtures";
import { perfRecordingRoutes, productionDay, quietDay, type DayVolume } from "./dataset";

const STEPS = 20;
const STEP_PIXELS = 12;
const SAMPLING_INTERVAL_MICROSECONDS = 100;
/**
 * Sampling exists to name the stages, so it repeats only a short pan: twenty
 * profiled steps outlive the test budget in a development build.
 */
const PROFILE_STEPS = 6;

/**
 * The three gestures an Owner can actually make. `clamped` is a drag on the whole
 * day, which cannot move because the range already covers the bounds; the other
 * two zoom in first so the window really travels.
 */
const GESTURES = [
  { name: "clamped-pan", zoomIns: 0 },
  { name: "wide-pan", zoomIns: 1 },
  { name: "zoomed-pan", zoomIns: 2 },
] as const;

/** The heaviest moving gesture is the one worth a sampled profile. */
const PROFILED_GESTURE = "wide-pan";

interface StepReading {
  steps: number[];
  longTasks: number[];
}

interface GestureReading {
  steps: number;
  segments: number;
  totalMs: number;
  medianMs: number;
  p95Ms: number;
  maxMs: number;
  longTaskCount: number;
  longTaskTotalMs: number;
  longTaskMaxMs: number;
  movedMs: number;
}

interface StageReading {
  stage: string;
  selfMs: number;
}

interface VariantReading {
  variant: string;
  volume: DayVolume;
  expanded: boolean;
  /**
   * Set when the browser or React gave up during the run: the development build
   * feeds every commit to React's own performance track, which runs the tab out of
   * memory on the busiest days. Readings after that point mean nothing.
   */
  instability: string | null;
  gestures: Record<string, GestureReading>;
  profiledGesture: string | null;
  profileTotalMs: number;
  stages: StageReading[];
  topFunctions: { name: string; selfMs: number; source: string }[];
}

const readings: VariantReading[] = [];

/** Collects, for every pan step, the wall time until the browser is free again. */
async function installProbe(page: Page) {
  await page.evaluate(() => {
    const target = document.querySelector(".swimlane-timeline");
    if (!target) throw new Error("找不到活动泳道");
    const reading: StepReading = { steps: [], longTasks: [] };
    Object.assign(window, { __heartbeatPerf: reading, __heartbeatTimeline: target });
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) reading.longTasks.push(entry.duration);
    }).observe({ type: "longtask", buffered: false });
  });
}

async function settle(page: Page) {
  await page.evaluate(
    () =>
      new Promise<void>((resolve) => {
        requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
      }),
  );
}

/**
 * A cheap picture of where the lanes are drawn, so a gesture can prove whether
 * the window actually travelled. Tick labels are too coarse: a small pan keeps
 * the same round hour at the left edge.
 */
async function placement(page: Page): Promise<string> {
  return page.evaluate(() => {
    const segments = [...document.querySelectorAll<HTMLElement>(".timeline-range")];
    const edges = [segments.at(0), segments.at(-1)].map((segment) =>
      segment ? `${segment.style.left}|${segment.style.width}` : "-",
    );
    const ruler = document.querySelector(".timeline-ruler")?.textContent ?? "";
    return `${segments.length}/${edges.join("/")}/${ruler}`;
  });
}

/**
 * Presses with a real pointer so the component's own pointer capture works, then
 * drives the moves from inside the page so no debugging round trip lands in a step.
 */
async function pan(page: Page, steps = STEPS): Promise<StepReading> {
  const plot = page.locator("[data-time-plot]").first();
  const box = await plot.boundingBox();
  if (!box) throw new Error("泳道没有可测量的绘图区");
  const y = Math.round(box.y + box.height / 2);
  const from = Math.round(box.x + box.width * 0.7);
  await page.mouse.move(from, y);
  await page.mouse.down();
  await settle(page);
  const reading = await page.evaluate(
    async ({ from: startX, y: pointerY, steps, pixels }) => {
      const scope = window as unknown as {
        __heartbeatPerf: StepReading;
        __heartbeatTimeline: Element;
      };
      const target = scope.__heartbeatTimeline;
      scope.__heartbeatPerf.steps.length = 0;
      scope.__heartbeatPerf.longTasks.length = 0;
      const free = () =>
        new Promise<void>((resolve) => {
          requestIdleCallback(() => resolve(), { timeout: 2000 });
        });
      await free();
      for (let step = 1; step <= steps; step += 1) {
        const started = performance.now();
        target.dispatchEvent(
          new PointerEvent("pointermove", {
            bubbles: true,
            cancelable: true,
            pointerId: 1,
            pointerType: "mouse",
            isPrimary: true,
            buttons: 1,
            clientX: startX - step * pixels,
            clientY: pointerY,
          }),
        );
        await free();
        target.getBoundingClientRect();
        scope.__heartbeatPerf.steps.push(performance.now() - started);
      }
      return {
        steps: [...scope.__heartbeatPerf.steps],
        longTasks: [...scope.__heartbeatPerf.longTasks],
      };
    },
    { from, y, steps, pixels: STEP_PIXELS },
  );
  await page.mouse.up();
  await settle(page);
  return reading;
}

function summarise(reading: StepReading, segments: number, moved: boolean): GestureReading {
  const steps = [...reading.steps].sort((left, right) => left - right);
  const round = (value: number) => Math.round(value * 10) / 10;
  const at = (share: number) => steps[Math.min(steps.length - 1, Math.floor(steps.length * share))];
  const totalMs = round(steps.reduce((left, right) => left + right, 0));
  return {
    steps: steps.length,
    segments,
    totalMs,
    medianMs: round(at(0.5) ?? 0),
    p95Ms: round(at(0.95) ?? 0),
    maxMs: round(steps.at(-1) ?? 0),
    longTaskCount: reading.longTasks.length,
    longTaskTotalMs: round(reading.longTasks.reduce((left, right) => left + right, 0)),
    longTaskMaxMs: round(Math.max(0, ...reading.longTasks)),
    movedMs: moved ? totalMs : 0,
  };
}

interface ProfileNode {
  id: number;
  hitCount?: number;
  children?: number[];
  callFrame: { functionName: string; url: string };
}

/**
 * Names the stage a sampled frame belongs to, or null when the caller decides.
 * The development bundler merges `src/` into one chunk, so the function name is
 * the only reliable signal and the file name is just a fallback.
 */
function stageOf(frame: ProfileNode["callFrame"]): string | null {
  const name = frame.functionName || "(匿名)";
  const source = frame.url;
  if (/^\((program|idle|root|garbage collector)\)$/.test(name)) return `运行时 ${name}`;
  if (/^(projectTimeline|visibleLane|recordOverlaps|showsDensity)$/.test(name)) return "投影";
  if (name === "layoutRanges") return "区间布局";
  if (name === "ActivityOverview") return "概览绘制";
  if (/^(protocolRecordSummary|summarizeRecord|summarize[A-Z]|parseForeground)/.test(name))
    return "记录摘要";
  if (/^(DensityCurve|density|smoothCurve|curvePath)/.test(name)) return "密度曲线";
  if (/^(formatTime|percent|timeTicks|overlaps|clampRange|dragRange|zoomRange)$/.test(name))
    return "时间换算";
  if (
    /^(RangeSegment|RecordPlot|TimelineLane|TimelineViewport|LaneName|RecordsPanel|RecordCard|ReplayWorkbench|Tooltip)/.test(
      name,
    )
  )
    return "泳道组件";
  if (/timelineProjection/.test(source)) return "投影";
  if (/rangeLayout/.test(source)) return "区间布局";
  if (/ActivityOverview/.test(source)) return "概览绘制";
  if (/react-dom|react-jsx|\/react\/|scheduler/.test(source)) return "React 渲染";
  return null;
}

/**
 * Attributes each sample to the nearest enclosing stage, so helpers land on the
 * stage that called them instead of a nameless bucket.
 */
async function profile(session: CDPSession, act: () => Promise<void>) {
  await session.send("Profiler.enable");
  await session.send("Profiler.setSamplingInterval", {
    interval: SAMPLING_INTERVAL_MICROSECONDS,
  });
  await session.send("Profiler.start");
  await act();
  const stopped = (await session.send("Profiler.stop")) as unknown as {
    profile: { nodes: ProfileNode[] };
  };
  await session.send("Profiler.disable");
  const nodes = stopped.profile.nodes;
  const byId = new Map(nodes.map((node) => [node.id, node]));
  const parents = new Map<number, number>();
  for (const node of nodes) for (const child of node.children ?? []) parents.set(child, node.id);
  const stageFor = (node: ProfileNode) => {
    let current: ProfileNode | undefined = node;
    for (let depth = 0; current && depth < 128; depth += 1) {
      const stage = stageOf(current.callFrame);
      if (stage) return stage;
      const parent = parents.get(current.id);
      current = parent === undefined ? undefined : byId.get(parent);
    }
    return "其他";
  };
  const stages = new Map<string, number>();
  const functions = new Map<string, { name: string; selfMs: number; source: string }>();
  for (const node of nodes) {
    const selfMs = ((node.hitCount ?? 0) * SAMPLING_INTERVAL_MICROSECONDS) / 1000;
    if (!selfMs) continue;
    const stage = stageFor(node);
    stages.set(stage, (stages.get(stage) ?? 0) + selfMs);
    const name = node.callFrame.functionName || "(匿名)";
    const source = node.callFrame.url.replace(/^.*\/(?=[^/]+$)/, "").replace(/\?.*$/, "");
    const key = `${name}@${source}`;
    const current = functions.get(key) ?? { name, selfMs: 0, source };
    current.selfMs += selfMs;
    functions.set(key, current);
  }
  const round = (value: number) => Math.round(value * 10) / 10;
  return {
    totalMs: round([...stages.values()].reduce((left, right) => left + right, 0)),
    stages: [...stages.entries()]
      .map(([stage, selfMs]) => ({ stage, selfMs: round(selfMs) }))
      .sort((left, right) => right.selfMs - left.selfMs),
    topFunctions: [...functions.values()]
      .sort((left, right) => right.selfMs - left.selfMs)
      .slice(0, 12)
      .map((entry) => ({ ...entry, selfMs: round(entry.selfMs) })),
  };
}

async function openDay(page: Page, volume: DayVolume, expanded: boolean) {
  const faults: string[] = [];
  page.on("pageerror", (error) => faults.push(error.message.split("\n")[0] ?? "未知页面错误"));
  await identityRoutes(page);
  await perfRecordingRoutes(page, volume);
  await seedSession(page);
  await page.goto("/");
  await expect(page.getByRole("region", { name: /活动泳道/ })).toBeVisible();
  await expect(page.locator(".timeline-range").first()).toBeVisible();
  if (expanded) {
    await page.getByRole("button", { name: /^展开 .* 的应用$/ }).click();
    await expect(page.locator(".application-sublane").first()).toBeVisible();
  }
  await page.waitForLoadState("networkidle");
  await installProbe(page);
  return faults;
}

async function zoomIn(page: Page) {
  await page.getByRole("button", { name: "放大时间轴" }).click();
  await page.waitForLoadState("networkidle");
  await settle(page);
}

function measure(name: string, volume: DayVolume, expanded: boolean) {
  test(name, async ({ page }) => {
    test.setTimeout(300_000);
    const faults = await openDay(page, volume, expanded);
    /**
     * Sampling doubles the gesture, which the expanded variant cannot afford in a
     * development build: it already spends seconds per step. Its stage mix is the
     * same as the collapsed variant, so only the collapsed one is profiled.
     */
    const sampling = !expanded;

    const gestures: Record<string, GestureReading> = {};
    let sampled: Awaited<ReturnType<typeof profile>> | null = null;
    let instability: string | null = null;
    for (const gesture of GESTURES) {
      try {
        for (let zoom = 0; zoom < gesture.zoomIns; zoom += 1) await zoomIn(page);
        const segments = await page.locator(".timeline-range").count();
        const before = await placement(page);
        const reading = await pan(page);
        const moved = (await placement(page)) !== before;
        gestures[gesture.name] = summarise(reading, segments, moved);
        if (sampling && gesture.name === PROFILED_GESTURE) {
          const session = await page.context().newCDPSession(page);
          sampled = await profile(session, async () => {
            await pan(page, PROFILE_STEPS);
          });
          await session.detach();
        }
        expect(reading.steps.length).toBe(STEPS);
        expect(segments).toBeGreaterThan(0);
      } catch (error) {
        // A tab the browser killed is a reading in its own right, so it is recorded
        // instead of hidden behind a failed run.
        const message = error instanceof Error ? error.message : String(error);
        if (!/crash/i.test(message)) throw error;
        instability = `${gesture.name}: ${message.split("\n")[0]}`;
        break;
      }
      if (faults.length) instability ??= `${gesture.name}: ${faults[0]}`;
    }
    if (!instability) {
      expect(gestures["clamped-pan"]?.movedMs).toBe(0);
      expect(gestures["wide-pan"]?.movedMs).toBeGreaterThan(0);
      expect(gestures["zoomed-pan"]?.movedMs).toBeGreaterThan(0);
      if (sampling && !sampled) throw new Error("没有采样到剖析数据");
    }

    readings.push({
      variant: name,
      volume,
      expanded,
      instability,
      gestures,
      profiledGesture: sampling ? PROFILED_GESTURE : null,
      profileTotalMs: sampled?.totalMs ?? 0,
      stages: sampled?.stages ?? [],
      topFunctions: sampled?.topFunctions ?? [],
    });
  });
}

test.describe("活动泳道拖动性能", () => {
  measure("quiet-day", quietDay, false);
  measure("production-day", productionDay, false);
  if (process.env.HEARTBEAT_PERF_MODE === "production") {
    measure("production-day-expanded", productionDay, true);
  }
});

test.afterAll(async () => {
  // Reports live outside Playwright's output directory, which is wiped per run.
  const mode = process.env.HEARTBEAT_PERF_MODE === "production" ? "production" : "development";
  const recordedAt = new Date().toISOString();
  const target =
    process.env.HEARTBEAT_PERF_REPORT ??
    path.join(".artifacts", "perf", `${mode}-${recordedAt.replace(/[:.]/g, "-")}.json`);
  await mkdir(path.dirname(target), { recursive: true });
  await writeFile(
    target,
    `${JSON.stringify(
      {
        mode,
        label: process.env.HEARTBEAT_PERF_LABEL ?? null,
        recordedAt,
        node: process.version,
        steps: STEPS,
        profileSteps: PROFILE_STEPS,
        stepPixels: STEP_PIXELS,
        variants: readings,
      },
      null,
      2,
    )}\n`,
    "utf8",
  );
  console.log(`Performance report: ${path.resolve(target)}`);
});
