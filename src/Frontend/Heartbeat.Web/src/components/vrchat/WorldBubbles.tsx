"use client";
import { BubbleMap } from "@/components/visualizations/BubbleMap";
import { formatDurationMinutes } from "@/lib/dates";
import { accessColors, type World } from "./model";

function fill(world: World) {
  const total = Math.max(1, world.milliseconds);
  let offset = 0;
  const stops = [...world.access].map(([access, milliseconds]) => {
    const start = offset;
    offset += (milliseconds / total) * 100;
    return `${accessColors[access]} ${start}% ${offset}%`;
  });
  return `conic-gradient(${stops.join(", ")})`;
}
export function WorldBubbles({
  worlds,
  selected,
  onSelect,
}: {
  worlds: World[];
  selected: string | null;
  onSelect: (id: string) => void;
}) {
  return (
    <BubbleMap
      label="世界停留气泡图"
      selected={selected}
      onSelect={onSelect}
      items={worlds.slice(0, 24).map((world) => ({
        id: world.id,
        label: world.name,
        detail: formatDurationMinutes(world.milliseconds),
        accessibleLabel: `${world.name}，${formatDurationMinutes(world.milliseconds)}，${world.visits.length} 个观测段`,
        weight: world.milliseconds,
        color: fill(world),
        borderWidth: Math.min(5, 1 + Math.log2(world.visits.length + 1)),
      }))}
    />
  );
}
