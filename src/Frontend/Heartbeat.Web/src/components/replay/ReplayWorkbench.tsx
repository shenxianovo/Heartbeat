"use client";

import { useAuth } from "react-oidc-context";
import { Button } from "@/components/ui/button";
import { DatePicker } from "@/components/ui/DatePicker";
import { MultiSelectPicker } from "@/components/ui/MultiSelectPicker";
import { Popover } from "@/components/ui/Popover";
import { Icon } from "@/components/ui/Icon";
import { DateRangeControls } from "@/components/filters/DateRangeControls";
import { trackLabel } from "@/components/filters/TrackPicker";
import { AppHeader } from "@/components/layout/AppHeader";
import { RecordsPanel } from "./RecordsPanel";
import { TimelineViewport } from "./TimelineViewport";
import { todayRange } from "@/lib/dates";
import { LoadingState } from "@/components/status/LoadingState";
import { QueryState } from "@/components/status/QueryState";
import { useReplaySelection } from "./useReplaySelection";
import { useReplayData } from "./useReplayData";

function RefreshButton({
  fetching,
  onRefresh,
  className = "",
}: {
  fetching: boolean;
  onRefresh: () => void;
  className?: string;
}) {
  return (
    <Button className={className} variant="glass" disabled={fetching} onClick={onRefresh}>
      <Icon name="refresh" />
      {fetching ? "正在刷新" : "刷新"}
    </Button>
  );
}

export function ReplayWorkbench() {
  const auth = useAuth();
  const accessToken = auth.user?.access_token ?? "";
  const ownerSubject = auth.user?.profile.sub ?? "";
  const selection = useReplaySelection();
  const {
    chosen,
    from,
    to,
    bounds,
    range,
    selectedCollectorIds,
    detail,
    chooseDate,
    stepDay,
    setRange,
    selectCollectors,
    setDetail,
  } = selection;
  const {
    tracksQuery,
    allTracks,
    tracks,
    collectors,
    replayQuery,
    densityQuery,
    densityStatus,
    lanes,
    detailTrack,
    detailQuery,
    refresh,
    fetching,
    densityFailed,
  } = useReplayData(ownerSubject, accessToken, selection);
  return (
    <div className="app-frame">
      <AppHeader />
      <main className="workspace experience-workspace">
        {tracksQuery.isPending ? (
          <LoadingState label="正在读取采集来源" />
        ) : tracksQuery.isError ? (
          <QueryState
            eyebrow="读取失败"
            title="暂时无法读取采集来源"
            description={tracksQuery.error.message}
            action={
              <Button variant="outline" type="button" onClick={() => void tracksQuery.refetch()}>
                重试
              </Button>
            }
          />
        ) : allTracks.length === 0 ? (
          <QueryState
            eyebrow="暂无来源"
            title="还没有可以回放的记录"
            description="采集端开始记录并完成上传后，对应来源会出现在这里。"
            action={<RefreshButton fetching={fetching} onRefresh={() => void refresh()} />}
          />
        ) : chosen && bounds && range ? (
          <>
            <section className="experience-filters" aria-label="回放筛选">
              <div className="experience-date">
                <Button size="icon" aria-label="前一天" onClick={() => stepDay(-1)}>
                  <Icon name="chevronLeft" />
                </Button>
                <DatePicker
                  value={chosen.from.slice(0, 10)}
                  onChange={(value) => chooseDate(todayRange(new Date(`${value}T00:00:00`)))}
                />
                <Button size="icon" aria-label="后一天" onClick={() => stepDay(1)}>
                  <Icon name="chevronRight" />
                </Button>
              </div>
              <MultiSelectPicker
                label="采集来源"
                allLabel="全部来源"
                options={collectors.map(([value, label]) => ({ value, label }))}
                value={selectedCollectorIds ?? collectors.map(([id]) => id)}
                onChange={(ids) => selectCollectors(ids.length === collectors.length ? null : ids)}
              />
              <Popover
                label="自定义时间范围"
                className="range-picker"
                trigger={
                  <>
                    <Icon name="filter" />
                    <span>时间范围</span>
                    <Icon name="chevronDown" />
                  </>
                }
              >
                {(close) => (
                  <DateRangeControls
                    key={`${chosen.from}/${chosen.to}`}
                    value={chosen}
                    onApply={(next) => {
                      chooseDate(next);
                      close();
                    }}
                  />
                )}
              </Popover>
              <RefreshButton
                className="experience-refresh"
                fetching={fetching}
                onRefresh={() => void refresh()}
              />
            </section>
            {!tracks.length ? (
              <QueryState
                eyebrow="暂无选择"
                title="尚未选择来源"
                description="选择一个或多个来源，在同一时间轴中组合查看。"
              />
            ) : replayQuery.pending.length > 0 && replayQuery.data.length === 0 ? (
              <LoadingState label="正在构建统一时间窗口" />
            ) : replayQuery.data.length === 0 && replayQuery.failures.length > 0 ? (
              <QueryState
                eyebrow="读取失败"
                title="暂时无法显示时间线"
                description="所选 Track 均读取失败。"
                action={
                  <Button
                    variant="outline"
                    type="button"
                    onClick={() => void replayQuery.refetch()}
                  >
                    重试
                  </Button>
                }
              />
            ) : (
              <>
                {replayQuery.failures.length > 0 ? (
                  <div className="density-error" role="alert">
                    {replayQuery.failures.length} 条 Track 读取失败，其余数据仍可查看。
                    <Button
                      type="button"
                      variant="ghost"
                      onClick={() =>
                        void Promise.all(replayQuery.failures.map((failure) => failure.retry()))
                      }
                    >
                      重试失败 Track
                    </Button>
                  </div>
                ) : null}
                {densityFailed ? (
                  <div className="density-error" role="alert">
                    当前范围的细节读取失败，仍显示已有密度。
                    <Button
                      type="button"
                      variant="ghost"
                      onClick={() => void densityQuery.refetch()}
                    >
                      重试密度
                    </Button>
                  </div>
                ) : null}
                <TimelineViewport
                  key={`${ownerSubject}/${from}/${to}`}
                  lanes={lanes}
                  overviewLanes={replayQuery.data}
                  bounds={bounds}
                  range={range}
                  densityStatus={densityStatus}
                  onRange={setRange}
                  onSelectPoints={setDetail}
                />
              </>
            )}
            {detail && detailTrack ? (
              <section className="point-details glass-panel">
                <div className="section-heading">
                  <div>
                    <span className="track-source">所选密度区间 · {detail.count} 条</span>
                    <h2>{trackLabel(detailTrack)}</h2>
                  </div>
                  <Button variant="ghost" type="button" onClick={() => setDetail(null)}>
                    关闭详情
                  </Button>
                </div>
                <RecordsPanel query={detailQuery} selectedTrack={detailTrack} />
              </section>
            ) : null}
            <footer className="experience-footer">
              {Intl.DateTimeFormat().resolvedOptions().timeZone}
            </footer>
          </>
        ) : (
          <LoadingState label="正在准备时间范围" />
        )}
      </main>
    </div>
  );
}
