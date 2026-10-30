import { useEffect, useRef, type ComponentType, type ReactNode } from "react";
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
  primary,
  type = "button",
  disabled,
  title,
}: {
  children: ReactNode;
  onClick?: () => void;
  danger?: boolean;
  primary?: boolean;
  type?: "button" | "submit";
  disabled?: boolean | undefined;
  title?: string | undefined;
}) {
  return (
    <button
      type={type}
      onClick={onClick}
      disabled={disabled}
      title={title}
      className={cn(
        "inline-flex cursor-pointer items-center gap-1.5 rounded-lg border px-3 py-1 text-[13px] transition-colors disabled:cursor-default disabled:opacity-50",
        danger
          ? "border-danger text-danger hover:bg-danger/10"
          : primary
            ? "bg-fg text-card border-transparent hover:opacity-90"
            : "border-line-2 text-fg-2 hover:text-fg enabled:hover:border-line-3",
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

/** 锚在触发按钮下方的弹出层。点外面或按 Esc 关闭；Esc 不会再关掉右侧的面板。 */
export function Anchored({
  trigger,
  open,
  onClose,
  children,
  align = "start",
}: {
  trigger: ReactNode;
  open: boolean;
  onClose: () => void;
  children: ReactNode;
  align?: "start" | "end";
}) {
  const ref = useRef<HTMLSpanElement>(null);
  useEffect(() => {
    if (!open) return;
    function down(event: PointerEvent) {
      if (!ref.current?.contains(event.target as Node)) onClose();
    }
    document.addEventListener("pointerdown", down);
    return () => document.removeEventListener("pointerdown", down);
  }, [open, onClose]);
  return (
    <span ref={ref} className="relative inline-flex min-w-0">
      {trigger}
      {open && (
        <div
          role="dialog"
          onKeyDown={(event) => {
            if (event.key !== "Escape") return;
            event.preventDefault();
            onClose();
          }}
          className={cn(
            "pop bg-card border-line absolute top-full z-40 mt-1 w-72 rounded-xl border p-1.5",
            "shadow-[0_12px_32px_-12px_rgb(0_0_0/0.35)]",
            align === "end" ? "right-0" : "left-0",
          )}
        >
          {children}
        </div>
      )}
    </span>
  );
}

export function MenuItem({
  children,
  onClick,
  danger,
  disabled,
}: {
  children: ReactNode;
  onClick: () => void;
  danger?: boolean;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={cn(
        "flex w-full cursor-pointer items-center gap-2 rounded-lg px-2 py-1.5 text-left text-[13px] disabled:cursor-default disabled:opacity-50",
        danger ? "text-danger hover:bg-danger/10" : "hover:bg-panel",
      )}
    >
      {children}
    </button>
  );
}

export function MenuLabel({ children }: { children: ReactNode }) {
  return <p className="text-fg-3 px-2 pt-1 pb-0.5 text-[11px]">{children}</p>;
}

export function Switch({
  checked,
  onChange,
  label,
  disabled,
}: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: string;
  disabled?: boolean | undefined;
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={cn(
        "relative inline-flex h-[18px] w-[30px] shrink-0 cursor-pointer items-center rounded-full transition-colors disabled:cursor-default disabled:opacity-40",
        checked ? "bg-fg" : "bg-line-2",
      )}
    >
      <span
        className={cn(
          "bg-card block size-[14px] rounded-full shadow-sm transition-transform duration-150",
          checked ? "translate-x-[14px]" : "translate-x-[2px]",
        )}
      />
    </button>
  );
}

/** 页面中央的对话框。点背景或按 Esc 关闭。 */
export function Dialog({
  title,
  onClose,
  children,
  wide,
}: {
  title: string;
  onClose: () => void;
  children: ReactNode;
  wide?: boolean;
}) {
  useEffect(() => {
    function key(event: KeyboardEvent) {
      if (event.key !== "Escape") return;
      event.preventDefault();
      onClose();
    }
    window.addEventListener("keydown", key);
    return () => window.removeEventListener("keydown", key);
  }, [onClose]);
  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-black/25 p-4" onPointerDown={onClose}>
      <section
        role="dialog"
        aria-label={title}
        onPointerDown={(event) => event.stopPropagation()}
        className={cn(
          "pop bg-card border-line w-full rounded-2xl border p-5 shadow-[0_24px_64px_-24px_rgb(0_0_0/0.45)]",
          wide ? "max-w-[480px]" : "max-w-[400px]",
        )}
      >
        <h2 className="mb-3 text-[15px] font-medium">{title}</h2>
        {children}
      </section>
    </div>
  );
}
