import { referencedObject } from "@/components/objects/model";
import { coveredMilliseconds } from "@/lib/recording/intervals";
import type { TimelineRecord, RecordObject } from "@/api/types";
import { isObject, nonEmptyString } from "@/components/records/renderers/values";
import { clipExplicitRange } from "@/lib/recording/intervals";

export interface LocationValue {
  account_id: string;
  account_name: string;
  world_id: string;
  world_key: string;
  world_name?: string | null;
  instance_id: string;
}
interface EncounterValue extends LocationValue {
  friend_id: string;
  friend_name?: string | null;
}
export function readLocation(value: unknown, objects: RecordObject[]): LocationValue {
  if (!isObject(value) || !nonEmptyString(value.instance_id))
    throw new Error("VRChat 位置记录格式不正确");
  const account = referencedObject(objects, "account");
  const world = referencedObject(objects, "world");
  return {
    account_id: account.id,
    account_name: account.name || account.key,
    world_id: world.id,
    world_key: world.key,
    world_name: world.name || world.key,
    instance_id: value.instance_id,
  };
}
export function readEncounter(value: unknown, objects: RecordObject[]): EncounterValue {
  const friend = referencedObject(objects, "friend");
  return {
    ...readLocation(value, objects),
    friend_id: friend.id,
    friend_name: friend.name || friend.key,
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
  key: string;
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
  read: (value: unknown, objects: RecordObject[]) => T,
  from: number,
  to: number,
) {
  const items: (Visit & { value: T })[] = [];
  let invalid = 0;
  for (const record of records) {
    try {
      const value = read(record.value, record.objects);
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
  objectId?: string,
) {
  const own = visits(locations, readLocation, from, to);
  const shared = visits(encounters, readEncounter, from, to);
  const located = [...own.items, ...shared.items];
  return {
    worlds: collectWorlds(located),
    people: collectPeople(shared.items, objectId),
    invalid: own.invalid + shared.invalid,
    milliseconds: coveredMilliseconds(located),
  };
}

function collectWorlds(located: Visit[]): World[] {
  const worlds = new Map<string, World>();
  for (const visit of located) {
    const value = visit.value;
    const world = worlds.get(value.world_id) ?? {
      id: value.world_id,
      key: value.world_key,
      name: value.world_name || value.world_id,
      milliseconds: 0,
      visits: [],
      access: new Map<string, number>(),
    };
    world.visits.push(visit);
    world.access.set(accessType(value.instance_id), 0);
    worlds.set(world.id, world);
  }
  for (const world of worlds.values()) {
    world.milliseconds = coveredMilliseconds(world.visits);
    for (const access of world.access.keys())
      world.access.set(
        access,
        coveredMilliseconds(
          world.visits.filter((visit) => accessType(visit.value.instance_id) === access),
        ),
      );
  }
  return [...worlds.values()].sort((a, b) => b.milliseconds - a.milliseconds);
}

function collectPeople(
  shared: (Visit & { value: EncounterValue })[],
  objectId?: string,
): Encounter[] {
  const people = new Map<string, Encounter>();
  for (const visit of shared) {
    const value = visit.value;
    const peerId = value.friend_id === objectId ? value.account_id : value.friend_id;
    const peerName = value.friend_id === objectId ? value.account_name : value.friend_name;
    const person = people.get(peerId) ?? {
      id: peerId,
      name: peerName || peerId,
      milliseconds: 0,
      visits: [],
    };
    person.visits.push(visit);
    people.set(person.id, person);
  }
  for (const person of people.values()) person.milliseconds = coveredMilliseconds(person.visits);
  return [...people.values()].sort((a, b) => b.milliseconds - a.milliseconds);
}

export function heatmap(worlds: World[]): number[] {
  const cells = Array<number>(168).fill(0);
  const intervals: { from: number; to: number }[] = [];
  for (const visit of worlds.flatMap((world) => world.visits).sort((a, b) => a.from - b.from)) {
    const last = intervals.at(-1);
    if (last && visit.from <= last.to) last.to = Math.max(last.to, visit.to);
    else intervals.push({ from: visit.from, to: visit.to });
  }
  for (const visit of intervals) {
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
