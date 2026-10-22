import * as React from "react";
import { cn } from "@/lib/utils";

function Textarea({ className, ...props }: React.ComponentProps<"textarea">) {
  return (
    <textarea
      className={cn(
        "border-input flex min-h-[80px] w-full rounded-md border bg-transparent px-3 py-2 text-sm shadow-sm",
        "placeholder:text-muted-foreground focus-visible:ring-ring/50 focus-visible:ring-2 focus-visible:outline-none",
        "disabled:cursor-not-allowed disabled:opacity-50",
        className,
      )}
      {...props}
    />
  );
}

export { Textarea };
