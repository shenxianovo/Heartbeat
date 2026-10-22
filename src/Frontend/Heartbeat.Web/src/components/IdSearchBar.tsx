import { useState } from "react";
import { useNavigate } from "@tanstack/react-router";
import { Search } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { isUuidv7 } from "@/lib/uuid";
import { cn } from "@/lib/utils";

export function IdSearchBar({ initialId, large = false }: { initialId?: string; large?: boolean }) {
  const [value, setValue] = useState(initialId ?? "");
  const navigate = useNavigate();
  const valid = isUuidv7(value.trim());

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!valid) return;
    void navigate({ to: "/entities/$id", params: { id: value.trim() } });
  };

  return (
    <form onSubmit={submit} className={cn("flex w-full items-center gap-2", large ? "max-w-2xl" : "max-w-xs")}>
      <div className="relative flex-1">
        <Search className="text-muted-foreground pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2" />
        <Input
          value={value}
          onChange={(e) => setValue(e.target.value)}
          placeholder="粘贴实体 UUIDv7…"
          spellCheck={false}
          autoComplete="off"
          className={cn(
            "font-mono text-sm",
            large ? "h-12 pl-10" : "h-9 pl-9",
            !valid && value && "border-destructive/60",
          )}
          aria-label="实体 ID"
        />
      </div>
      <Button type="submit" disabled={!valid} size={large ? "lg" : "default"}>
        观测
      </Button>
    </form>
  );
}
