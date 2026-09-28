"use client";

import { useState } from "react";
import { Button } from "@/components/ui/button";
import { accessColors, duration, heatmap, type World, type Encounter } from "./model";
import { WorldBubbles } from "./WorldBubbles";
import { VisitDetails } from "./VisitDetails";

export function WorldPanel({ worlds }: { worlds: World[] }) {
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [list, setList] = useState(false);
  const selected = worlds.find((world) => world.id === selectedId) ?? worlds[0];
  return (
    <section className="glass-panel vrc-panel" aria-labelledby="worlds-heading">
      <div className="section-heading">
        <div>
          <span className="track-source">HABITAT MAP</span>
          <h2 id="worlds-heading">常去的世界</h2>
        </div>
        <Button variant="glass" onClick={() => setList(!list)}>
          {list ? "气泡视图" : "列表视图"}
        </Button>
      </div>
      <p className="vrc-caption">
        面积近似表示已记录时长，外圈粗细表示观测段数；颜色表示实例类型组成。
      </p>
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
                <span>{duration(world.milliseconds)}</span>
                <small>{world.visits.length} 段</small>
              </button>
            ))}
          </div>
        ) : (
          <WorldBubbles worlds={worlds} selected={selected?.id ?? null} onSelect={setSelectedId} />
        )}
        {selected ? (
          <aside className="vrc-world-detail" aria-live="polite">
            <h3>{selected.name}</h3>
            <div className="vrc-duration">{duration(selected.milliseconds)}</div>
            <p>{selected.visits.length} 个观测段</p>
            <div className="vrc-legend">
              {[...selected.access].map(([access, time]) => (
                <span key={access}>
                  <i style={{ background: accessColors[access] }} />
                  {access} {Math.round((time / Math.max(1, selected.milliseconds)) * 100)}%
                </span>
              ))}
            </div>
            <a
              href={`https://vrchat.com/home/world/${encodeURIComponent(selected.id)}`}
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
  return (
    <section className="glass-panel vrc-panel" aria-labelledby="encounters-heading">
      <span className="track-source">SHARED MOMENTS</span>
      <h2 id="encounters-heading">可见的同场相遇</h2>
      <p className="vrc-caption">
        仅统计 API
        显示与你处于同一实例的好友。隐藏位置和非好友不在覆盖范围内；同场不代表发生了互动。
      </p>
      {people.length ? (
        <div className="vrc-people">
          {people.map((person) => (
            <details key={person.id}>
              <summary>
                <span className="vrc-person-avatar" aria-hidden="true">
                  {Array.from(person.name)[0]}
                </span>
                <strong>{person.name}</strong>
                <span>{duration(person.milliseconds)}</span>
                <small>{person.visits.length} 段</small>
              </summary>
              <VisitDetails visits={person.visits} />
            </details>
          ))}
        </div>
      ) : (
        <p className="vrc-empty">这个时间范围内还没有可见同场记录。</p>
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
      <span className="track-source">ACTIVITY RHYTHM</span>
      <h2 id="rhythm-heading">活动节奏</h2>
      <p className="vrc-caption">
        按你的本地时区汇总已记录的世界停留时间 · {Intl.DateTimeFormat().resolvedOptions().timeZone}
      </p>
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
                title={`星期${day} ${hour}:00 · ${value ? duration(value) : "无记录"}`}
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
