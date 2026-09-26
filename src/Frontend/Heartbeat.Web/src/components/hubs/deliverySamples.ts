import type { DeliveryActivity } from "@/api/hubs";

export const deliveryStages = [
  { key: "accepted", label: "已接受", color: "#38a9e8" },
  { key: "delivered", label: "已上传", color: "#2fbc95" },
] as const;
export const displaySeconds = 60;
export interface DeliveryPoint {
  at: number;
  from?: number;
  accepted: number | null;
  delivered: number | null;
}

/** View-local samples. Counters are never converted to rates or reconstructed into event history. */
export class DeliverySamples {
  readonly points: DeliveryPoint[] = [];
  private previous?: DeliveryActivity;
  private epoch?: string;

  observe(activity?: DeliveryActivity) {
    if (!activity) {
      this.previous = undefined;
      return false;
    }
    if (activity.epoch !== this.epoch) {
      this.points.length = 0;
      this.previous = undefined;
      this.epoch = activity.epoch;
    }
    if (this.previous && activity.capturedAt <= this.previous.capturedAt) return false;
    const before = this.previous;
    this.previous = activity;
    const at = activity.capturedAt / 1000;
    this.points.push({
      at,
      from: before ? before.capturedAt / 1000 : undefined,
      accepted: before ? activity.accepted - before.accepted : null,
      delivered: before ? activity.delivered - before.delivered : null,
    });
    while (this.points[0]!.at < at - displaySeconds) this.points.shift();
    return true;
  }
}
