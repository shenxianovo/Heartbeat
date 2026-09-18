import type { Page } from "@playwright/test";

import { accessToken } from "../e2e/fixtures";

/** One Collector, so a benchmark day looks like a single Mac reporting three Tracks. */
const collector = {
  id: "019e0100-0000-7000-8000-0000000000a0",
  key: "heartbeat.collector.desktop.macos",
  target: "perf-mac",
  displayName: "基准 Mac",
};

function track(suffix: string, type: string, timeMode: "range" | "point") {
  return {
    id: `019e0100-0000-7000-8000-0000000000${suffix}`,
    collectorId: collector.id,
    collectorKey: collector.key,
    collectorTarget: collector.target,
    collectorDisplayName: collector.displayName,
    type,
    version: 1,
    timeMode,
    endMode: timeMode === "range" ? "explicit" : null,
    createdAt: "2026-09-01T00:00:00Z",
  };
}

export const applicationTrack = track("b1", "desktop.application.foreground", "range");
export const windowTrack = track("b2", "desktop.window.foreground", "range");
export const inputTrack = track("b3", "desktop.input.event", "point");

export interface DayVolume {
  /** Foreground application Record count for the day. */
  applications: number;
  /** Foreground window Record count for the day. */
  windows: number;
  /** Input events the day counts, delivered as server-side buckets. */
  inputEvents: number;
  /** Distinct applications, which decides how many sub-lanes an expanded lane draws. */
  distinctApplications: number;
}

/** Matches a measured local day: 1,376 application, 1,738 window and 175,492 input observations. */
export const productionDay: DayVolume = {
  applications: 1376,
  windows: 1738,
  inputEvents: 175492,
  distinctApplications: 14,
};

/** The same day shape at a hundredth of the volume, as the control measurement. */
export const quietDay: DayVolume = {
  applications: 100,
  windows: 126,
  inputEvents: 12756,
  distinctApplications: 8,
};

/** Deterministic so two runs measure the same day. */
function pseudoRandom(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state = (state * 1664525 + 1013904223) >>> 0;
    return state / 0x100000000;
  };
}

function recordId(lane: number, index: number): string {
  return `019e0100-0000-7000-8000-${lane.toString().padStart(2, "0")}${index
    .toString()
    .padStart(10, "0")}`;
}

/** Anonymous application identities, so no real bundle id or window title reaches evidence. */
function application(index: number) {
  const ordinal = (index + 1).toString().padStart(2, "0");
  return {
    platform: "macos",
    id_kind: "bundle_id",
    id: `test.heartbeat.app${ordinal}`,
    display_name: `应用 ${ordinal}`,
  };
}

function segments(from: number, to: number, count: number, seed: number) {
  const random = pseudoRandom(seed);
  const slot = (to - from) / count;
  return Array.from({ length: count }, (_, index) => {
    const start = Math.round(from + index * slot + random() * slot * 0.2);
    const end = Math.round(Math.min(to, start + slot * (0.5 + random() * 0.45)));
    return { start, end: Math.max(start + 1000, end), pick: random() };
  });
}

function applicationRecords(from: number, to: number, volume: DayVolume) {
  return segments(from, to, volume.applications, 20260917).map((segment, index) => ({
    id: recordId(1, index),
    startedAt: new Date(segment.start).toISOString(),
    endedAt: new Date(segment.end).toISOString(),
    observedAt: null,
    receivedAt: new Date(segment.end).toISOString(),
    value: {
      device_id: collector.target,
      application: application(Math.floor(segment.pick * volume.distinctApplications)),
    },
  }));
}

function windowRecords(from: number, to: number, volume: DayVolume) {
  return segments(from, to, volume.windows, 20260918).map((segment, index) => ({
    id: recordId(2, index),
    startedAt: new Date(segment.start).toISOString(),
    endedAt: new Date(segment.end).toISOString(),
    observedAt: null,
    receivedAt: new Date(segment.end).toISOString(),
    value: {
      device_id: collector.target,
      window: { title: `窗口 ${(index % 97) + 1}` },
    },
  }));
}

/** A working-hours shaped curve, normalised to the day's input event count. */
function inputBuckets(from: number, to: number, bucketSeconds: number, total: number) {
  const size = bucketSeconds * 1000;
  const count = Math.max(1, Math.ceil((to - from) / size));
  const weights = Array.from({ length: count }, (_, index) => {
    const hour = ((index * size) / 3_600_000) % 24;
    return Math.max(0.02, Math.sin(((hour - 6) / 18) * Math.PI)) ** 2;
  });
  const sum = weights.reduce((left, right) => left + right, 0);
  return weights.map((weight, index) => ({
    index,
    startedAt: new Date(from + index * size).toISOString(),
    endedAt: new Date(Math.min(to, from + (index + 1) * size)).toISOString(),
    count: Math.max(1, Math.round((weight / sum) * total)),
  }));
}

/**
 * Serves one benchmark day from the requested window, so the fixture follows the
 * browser clock instead of pinning a date the app would never ask for.
 */
export async function perfRecordingRoutes(page: Page, volume: DayVolume) {
  await page.route("**/api/v1/tracks**", async (route) => {
    const url = new URL(route.request().url());
    if (route.request().headers().authorization !== `Bearer ${accessToken}`) {
      await route.fulfill({ status: 401, json: { title: "缺少访问令牌" } });
      return;
    }
    if (url.pathname === "/api/v1/tracks") {
      await route.fulfill({ json: { tracks: [applicationTrack, windowTrack, inputTrack] } });
      return;
    }
    const from = Date.parse(url.searchParams.get("from") ?? "");
    const to = Date.parse(url.searchParams.get("to") ?? "");
    if (url.pathname === `/api/v1/tracks/${inputTrack.id}/point-counts`) {
      const bucketSeconds = Number(url.searchParams.get("bucketSeconds") ?? 900);
      const share = (to - from) / 86_400_000;
      await route.fulfill({
        json: {
          track: inputTrack,
          from: new Date(from).toISOString(),
          to: new Date(to).toISOString(),
          bucketSeconds,
          buckets: inputBuckets(from, to, bucketSeconds, Math.round(volume.inputEvents * share)),
        },
      });
      return;
    }
    if (url.pathname === `/api/v1/tracks/${applicationTrack.id}/records`) {
      await route.fulfill({
        json: {
          track: applicationTrack,
          records: applicationRecords(from, to, volume),
          nextCursor: null,
        },
      });
      return;
    }
    if (url.pathname === `/api/v1/tracks/${windowTrack.id}/records`) {
      await route.fulfill({
        json: { track: windowTrack, records: windowRecords(from, to, volume), nextCursor: null },
      });
      return;
    }
    await route.fulfill({ json: { track: inputTrack, records: [], nextCursor: null } });
  });
}
