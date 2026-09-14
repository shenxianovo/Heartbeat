import type { InfiniteData, UseInfiniteQueryResult } from "@tanstack/react-query";

import { ApiError } from "@/api/client";
import type { RecordsResponse, TrackSummary } from "@/api/types";
import { RecordList } from "@/components/records/RecordList";
import { LoadingState } from "@/components/status/LoadingState";
import { QueryState } from "@/components/status/QueryState";

interface RecordsPanelProps {
  query: UseInfiniteQueryResult<InfiniteData<RecordsResponse, unknown>, Error>;
  selectedTrack: TrackSummary;
}

function errorMessage(error: Error): string {
  if (error instanceof ApiError) {
    if (error.status === 401) return "登录状态已失效，请退出后重新登录。";
    if (error.status === 404) return "这个 Track 已不存在，刷新来源后再试。";
  }
  return error.message || "读取记录时发生未知错误。";
}

export function RecordsPanel({ query, selectedTrack }: RecordsPanelProps) {
  if (query.isPending) return <LoadingState label="正在读取这段时间的记录" />;

  if (query.isError) {
    return (
      <QueryState
        eyebrow="读取失败"
        title="暂时无法显示记录"
        description={errorMessage(query.error)}
        action={
          <button className="secondary-button" type="button" onClick={() => void query.refetch()}>
            重试
          </button>
        }
      />
    );
  }

  const pages = query.data.pages;
  const records = pages.flatMap((page) => page.records);
  const track = pages[0]?.track ?? selectedTrack;

  if (records.length === 0) {
    return (
      <QueryState
        eyebrow="没有记录"
        title="这段时间很安静"
        description="当前 Track 在所选时间范围内没有可回放的记录，可以调整时间后再查看。"
      />
    );
  }

  return (
    <>
      <div className="list-summary" aria-live="polite">
        <span>已显示 {records.length} 条</span>
        <span>按开始时间升序</span>
      </div>
      <RecordList records={records} track={track} />
      {query.hasNextPage ? (
        <div className="load-more">
          <button
            className="secondary-button"
            type="button"
            disabled={query.isFetchingNextPage}
            onClick={() => void query.fetchNextPage()}
          >
            {query.isFetchingNextPage ? "正在加载…" : "加载更多记录"}
          </button>
        </div>
      ) : (
        <p className="list-end">已经到达这段时间的末尾</p>
      )}
    </>
  );
}
