import { cn } from "@/lib/utils";

/** 吉祥物，出现在空状态和读不到的提示里。 */
export function Mascot({ className }: { className?: string }) {
  return <img src="/mascots/macos-512.png" alt="" draggable={false} className={cn("size-24 select-none", className)} />;
}
