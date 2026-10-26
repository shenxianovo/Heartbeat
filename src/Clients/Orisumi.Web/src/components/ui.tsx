import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

export function Segmented<T extends string>({
  label,
  options,
  value,
  onChange,
}: {
  label: string;
  options: readonly { value: T; label: string }[];
  value: T;
  onChange: (value: T) => void;
}) {
  return (
    <div role="group" aria-label={label} className="bg-panel flex gap-0.5 rounded-lg p-[3px]">
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          aria-pressed={option.value === value}
          onClick={() => onChange(option.value)}
          className={cn(
            "cursor-pointer rounded-md px-3 py-1 text-[13px] transition-colors",
            option.value === value ? "bg-card text-fg" : "text-fg-2 hover:text-fg",
          )}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

export function Card({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn("bg-card border-line rounded-xl border px-[18px] py-3.5", className)}>{children}</div>;
}

export function SectionTitle({ children }: { children: ReactNode }) {
  return <h2 className="mb-2 text-[13px] font-medium">{children}</h2>;
}

export function Dot({ color }: { color: string }) {
  return <span className="inline-block size-[7px] shrink-0 rounded-full" style={{ background: color }} />;
}

export function Chip({
  children,
  strong,
  dashed,
  className,
}: {
  children: ReactNode;
  strong?: boolean;
  dashed?: boolean;
  className?: string;
}) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 rounded-md border px-2 py-0.5 text-xs whitespace-nowrap",
        strong ? "border-line-3 text-fg" : "border-line-2 text-fg-2",
        dashed && "text-fg-3 border-dashed",
        className,
      )}
    >
      {children}
    </span>
  );
}

export function Button({
  children,
  onClick,
  danger,
  type = "button",
  disabled,
}: {
  children: ReactNode;
  onClick?: () => void;
  danger?: boolean;
  type?: "button" | "submit";
  disabled?: boolean;
}) {
  return (
    <button
      type={type}
      onClick={onClick}
      disabled={disabled}
      className={cn(
        "cursor-pointer rounded-lg border px-3 py-1 text-[13px] transition-colors disabled:cursor-default disabled:opacity-60",
        danger ? "border-danger text-danger hover:bg-danger/10" : "border-line-2 text-fg-2 hover:text-fg",
      )}
    >
      {children}
    </button>
  );
}

export function Message({ title, detail, children }: { title: string; detail?: string; children?: ReactNode }) {
  return (
    <div className="grid place-items-center gap-2 py-16 text-center">
      <p className="text-fg text-[15px]">{title}</p>
      {detail && <p className="text-fg-3 text-xs">{detail}</p>}
      {children}
    </div>
  );
}

export function Mono({ children, className }: { children: ReactNode; className?: string }) {
  return <span className={cn("font-mono text-xs", className)}>{children}</span>;
}
