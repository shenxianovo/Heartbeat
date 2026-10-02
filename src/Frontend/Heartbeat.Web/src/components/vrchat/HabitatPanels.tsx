"use client";

import Link from "next/link";
import { useObjectScope, useObjectRange } from "@/components/objects/ObjectScope";
import { relatedObjectHref } from "@/components/objects/model";
import { useState } from "react";
import { Button } from "@/components/ui/button";
import { formatDurationMinutes } from "@/lib/dates";
import { accessColors, heatmap, type World, type Encounter } from "./model";
import { WorldBubbles } from "./WorldBubbles";
import { VisitDetails } from "./VisitDetails";

export function WorldPanel({ worlds }: { worlds: World[] }) {
  const scope = useObjectScope();
  const range = useObjectRange();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [list, setList] = useState(false);
  const selected = worlds.find((world) => world.id === selectedId) ?? worlds[0];
  return (
    <section className="glass-panel vrc-panel" aria-labelledby="worlds-heading">
      <div className="section-heading">
        <div>
          <h2 id="worlds-heading">常去的世界</h2>
        </div>
        <Button variant="glass" onClick={() => setList(!list)}>
          {list ? "气泡视图" : "列表视图"}
        </Button>
      </div>
      <div className="vrc-world-layout">
        {list ? (
          <div className="vrc-world-list">
            {worlds.map((world) => (
              <button
                type="button"
                key={world.id}
                aria-pressed={selected?.id === world.id}
                onClick={() => setSelectedId(world.id)}
              >
                <strong>{world.name}</strong>
                <span>{formatDurationMinutes(world.milliseconds)}</span>
                <small>{world.visits.length} 段</small>
              </button>
            ))}
          </div>
        ) : (
          <WorldBubbles worlds={worlds} selected={selected?.id ?? null} onSelect={setSelectedId} />
        )}
        {selected ? (
          <aside className="vrc-world-detail" aria-live="polite">
            <h3>
              <Link href={relatedObjectHref(selected.id, range, scope)}>{selected.name} ↗</Link>
            </h3>
            <div className="vrc-duration">{formatDurationMinutes(selected.milliseconds)}</div>
            <p>{selected.visits.length} 个观测段</p>
            <div className="vrc-legend">
              {[...selected.access].map(([access, time]) => (
                <span key={access}>
                  <i style={{ background: accessColors[access] }} />
                  {access}{" "}
                  {Math.round(
                    (time /
                      Math.max(
                        1,
                        [...selected.access.values()].reduce((sum, value) => sum + value, 0),
                      )) *
                      100,
                  )}
                  %
                </span>
              ))}
            </div>
            <a
              href={`https://vrchat.com/home/world/${encodeURIComponent(selected.key)}`}
              target="_blank"
              rel="noreferrer"
            >
              在 VRChat 查看世界 ↗
            </a>
            <details>
              <summary>查看访问明细</summary>
              <VisitDetails visits={selected.visits} />
            </details>
          </aside>
        ) : null}
      </div>
      {!list && worlds.length > 24 ? (
        <p className="vrc-caption">显示停留最久的 24 个世界；切换列表查看全部。</p>
      ) : null}
    </section>
  );
}

export function EncounterPanel({ people }: { people: Encounter[] }) {
  const scope = useObjectScope();
  const range = useObjectRange();
  return (
    <section className="glass-panel vrc-panel" aria-labelledby="encounters-heading">
      <h2 id="encounters-heading">同场好友</h2>
      {people.length ? (
        <div className="vrc-people">
          {people.map((person) => (
            <details key={person.id}>
              <summary>
                <span className="vrc-person-avatar" aria-hidden="true">
                  {Array.from(person.name)[0]}
                </span>
                <strong>{person.name}</strong>
                <span>{formatDurationMinutes(person.milliseconds)}</span>
                <small>{person.visits.length} 段</small>
              </summary>
              <Link href={relatedObjectHref(person.id, range, scope)}>查看账号 ↗</Link>
              <VisitDetails visits={person.visits} />
            </details>
          ))}
        </div>
      ) : (
        <p className="vrc-empty">暂无同场记录。</p>
      )}
    </section>
  );
}

export function RhythmPanel({ worlds }: { worlds: World[] }) {
  const cells = heatmap(worlds);
  const maximum = Math.max(1, ...cells);
  const days = ["一", "二", "三", "四", "五", "六", "日"];
  return (
    <section className="glass-panel vrc-panel" aria-labelledby="rhythm-heading">
      <h2 id="rhythm-heading">活动节奏</h2>
      <div
        className="vrc-heatmap"
        role="img"
        aria-label="按星期和小时汇总的 VRChat 世界停留时长，颜色越深时间越长"
      >
        <span />
        {Array.from({ length: 24 }, (_, hour) => (
          <small key={hour}>{hour % 3 === 0 ? hour : ""}</small>
        ))}
        {days.map((day, index) => (
          <div className="vrc-heat-row" key={day}>
            <small>{day}</small>
            {cells.slice(index * 24, (index + 1) * 24).map((value, hour) => (
              <span
                key={hour}
                title={`星期${day} ${hour}:00 · ${value ? formatDurationMinutes(value) : "无记录"}`}
                style={{
                  background: `color-mix(in oklch, var(--primary) ${value ? 15 + (value / maximum) * 75 : 0}%, var(--muted))`,
                }}
              />
            ))}
          </div>
        ))}
      </div>
    </section>
  );
}
