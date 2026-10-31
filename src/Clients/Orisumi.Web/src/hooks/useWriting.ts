import { isActive, observerRows } from "@/lib/activity";
import { useActivity } from "./useActivity";

/** 侧栏与 Observer 页面共用最近保存时间，包括时间点和事实时间未知的观测。 */
export function useWriting(now: number) {
  const activity = useActivity(now);
  return observerRows({ ...activity, now }).filter((row) => isActive(row, now)).length;
}
