import type { TimelineRecord } from "@/api/types";
import { isObject, nonEmptyString } from "@/components/records/renderers/values";
import { clipExplicitRange } from "@/lib/recording/intervals";

export interface LocationValue {
  account_id: string;
  world_id: string;
  world_name?: string | null;
  instance_id: string;
}
interface EncounterValue extends LocationValue {
  friend_id: string;
  friend_name?: string | null;
}
export function readLocation(value: unknown): LocationValue {
  if (
    !isObject(value) ||
    !nonEmptyString(value.account_id) ||
    !nonEmptyString(value.world_id) ||
    !nonEmptyString(value.instance_id)
  )
    throw new Error("VRChat 位置记录格式不正确");
  return {
    account_id: value.account_id,
    world_id: value.world_id,
    instance_id: value.instance_id,
    world_name: typeof value.world_name === "string" ? value.world_name : null,
  };
}
export function readEncounter(value: unknown): EncounterValue {
  const location = readLocation(value);
  if (!isObject(value) || !nonEmptyString(value.friend_id))
    throw new Error("VRChat 同场记录缺少账号");
  return {
    ...location,
    friend_id: value.friend_id,
    friend_name: typeof value.friend_name === "string" ? value.friend_name : null,
  };
}

export function accessType(instance: string): string {
  if (instance.includes("~groupAccessType(public)")) return "Group Public";
  if (instance.includes("~groupAccessType(plus)")) return "Group+";
  if (instance.includes("~group(")) return "Group";
  if (instance.includes("~hidden(")) return "Friends+";
  if (instance.includes("~friends(")) return "Friends";
  if (instance.includes("~private("))
    return instance.includes("~canRequestInvite") ? "Invite+" : "Invite";
  return "Public";
}
export const accessColors: Record<string, string> = {
  Public: "var(--vrc-public)",
  "Group Public": "var(--vrc-group)",
  "Friends+": "var(--vrc-friends-plus)",
  Friends: "var(--vrc-friends)",
  "Invite+": "var(--vrc-invite-plus)",
  Invite: "var(--vrc-invite)",
  "Group+": "var(--vrc-group)",
  Group: "var(--vrc-group)",
};
export interface Visit {
  id: string;
  from: number;
  to: number;
  value: LocationValue;
}
export interface World {
  id: string;
  name: string;
  milliseconds: number;
  visits: Visit[];
  access: Map<string, number>;
}
export interface Encounter {
  id: string;
  name: string;
  milliseconds: number;
  visits: Visit[];
}
function visits<T extends LocationValue>(
  records: TimelineRecord[],
  read: (value: unknown) => T,
  from: number,
  to: number,
) {
  const items: (Visit & { value: T })[] = [];
  let invalid = 0;
  for (const record of records) {
    try {
      const value = read(record.value);
      const time = clipExplicitRange(record, from, to);
      if (time) items.push({ id: record.id, ...time, value });
    } catch {
      invalid++;
    }
  }
  return { items, invalid };
}

export function aggregate(
  locations: TimelineRecord[],
  encounters: TimelineRecord[],
  from: number,
  to: number,
) {
  const worlds = new Map<string, World>();
  const people = new Map<string, Encounter>();
  const own = visits(locations, readLocation, from, to);
  const shared = visits(encounters, readEncounter, from, to);
  for (const visit of own.items) {
    const value = visit.value;
    const world = worlds.get(value.world_id) ?? {
      id: value.world_id,
      name: value.world_name || value.world_id,
      milliseconds: 0,
      visits: [],
      access: new Map<string, number>(),
    };
    const length = visit.to - visit.from;
    world.milliseconds += length;
    world.visits.push(visit);
    const access = accessType(value.instance_id);
    world.access.set(access, (world.access.get(access) ?? 0) + length);
    worlds.set(world.id, world);
  }
  for (const visit of shared.items) {
    const value = visit.value;
    const person = people.get(value.friend_id) ?? {
      id: value.friend_id,
      name: value.friend_name || value.friend_id,
      milliseconds: 0,
      visits: [],
    };
    person.milliseconds += visit.to - visit.from;
    person.visits.push(visit);
    people.set(person.id, person);
  }
  return {
    worlds: [...worlds.values()].sort((a, b) => b.milliseconds - a.milliseconds),
    people: [...people.values()].sort((a, b) => b.milliseconds - a.milliseconds),
    invalid: own.invalid + shared.invalid,
  };
}

export function heatmap(worlds: World[]): number[] {
  const cells = Array<number>(168).fill(0);
  for (const visit of worlds.flatMap((world) => world.visits)) {
    let at = visit.from;
    while (at < visit.to) {
      const date = new Date(at);
      const remaining =
        3600000 - date.getMinutes() * 60000 - date.getSeconds() * 1000 - date.getMilliseconds();
      const end = Math.min(visit.to, at + remaining);
      const index = ((date.getDay() + 6) % 7) * 24 + date.getHours();
      cells[index] = (cells[index] ?? 0) + end - at;
      at = end;
    }
  }
  return cells;
}
