import type { ComponentType, ReactNode } from "react";
import { cn } from "@/lib/utils";
import { Mascot } from "./Mascot";

export function Segmented<T extends string>({
  label,
  options,
  value,
  onChange,
}: {
  label: string;
  options: readonly { value: T; label: string; icon?: ComponentType<{ className?: string }> }[];
  value: T;
  onChange: (value: T) => void;
}) {
  return (
    <div role="group" aria-label={label} className="bg-panel flex gap-0.5 rounded-lg p-[3px]">
      {options.map(({ value: key, label: text, icon: Icon }) => (
        <button
          key={key}
          type="button"
          aria-pressed={key === value}
          onClick={() => onChange(key)}
          className={cn(
            "inline-flex cursor-pointer items-center gap-1.5 rounded-md px-3 py-1 text-[13px] transition-colors",
            key === value ? "bg-card text-fg" : "text-fg-2 hover:text-fg",
          )}
        >
          {Icon && <Icon aria-hidden="true" className="size-3.5" />}
          {text}
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

/** 空状态和读不到时的提示。mascot 为 true 时，吉祥物一起出现。 */
export function Message({
  title,
  detail,
  mascot,
  children,
}: {
  title: string;
  detail?: string;
  mascot?: boolean;
  children?: ReactNode;
}) {
  return (
    <div className="grid place-items-center gap-2 py-16 text-center">
      {mascot && <Mascot className="mb-2" />}
      <p className="text-fg text-[15px]">{title}</p>
      {detail && <p className="text-fg-3 text-xs">{detail}</p>}
      {children}
    </div>
  );
}

export function Mono({ children, className }: { children: ReactNode; className?: string }) {
  return <span className={cn("font-mono text-xs", className)}>{children}</span>;
}

const SIGIL_SIZES = {
  sm: "size-6 rounded-lg text-[11px]",
  md: "size-9 rounded-xl text-sm",
  lg: "size-14 rounded-2xl text-xl",
};

/** 类型的徽记：业务类型写名字的第一个字，Core 类型用图标。颜色来自类型。 */
export function Sigil({
  tint,
  text,
  icon: Icon,
  size = "md",
  className,
}: {
  tint: string;
  text?: string | null;
  icon?: ComponentType<{ className?: string }>;
  size?: keyof typeof SIGIL_SIZES;
  className?: string;
}) {
  return (
    <span
      aria-hidden="true"
      className={cn("grid shrink-0 place-items-center font-semibold", SIGIL_SIZES[size], className)}
      style={{ background: `color-mix(in srgb, ${tint} 15%, transparent)`, color: tint }}
    >
      {Icon ? <Icon className="size-[55%]" /> : (text ?? "·").slice(0, 1)}
    </span>
  );
}
