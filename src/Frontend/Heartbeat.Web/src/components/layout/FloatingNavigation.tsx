"use client";

import { useEffect, useId, useRef, useState } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useSessionActions } from "@/auth/session";
import { Button, buttonVariants } from "@/components/ui/button";
import { Icon } from "@/components/ui/Icon";
import { useTheme } from "@/lib/theme";

const destinations = [
  { href: "/", label: "概览", icon: "home" },
  { href: "/objects", label: "对象", icon: "timeline" },
  { href: "/hubs", label: "Hub 管理", icon: "server" },
] as const;

export function FloatingNavigation() {
  const [open, setOpen] = useState(false);
  const [leaving, setLeaving] = useState(false);
  const [error, setError] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const navigationId = useId();
  const pathname = usePathname();
  const { signOut } = useSessionActions();
  const { isDark, toggle } = useTheme();

  function close() {
    trigger.current?.focus();
    setOpen(false);
  }

  useEffect(() => {
    if (!open) return;
    function outside(event: PointerEvent) {
      if (event.target instanceof Node && !root.current?.contains(event.target)) {
        trigger.current?.focus();
        setOpen(false);
      }
    }
    document.addEventListener("pointerdown", outside);
    return () => document.removeEventListener("pointerdown", outside);
  }, [open]);

  async function leave() {
    setLeaving(true);
    setError(false);
    try {
      await signOut();
      close();
    } catch {
      setError(true);
    } finally {
      setLeaving(false);
    }
  }

  return (
    <div
      ref={root}
      className="floating-navigation"
      data-open={open}
      onKeyDown={(event) => {
        if (open && event.key === "Escape") {
          event.preventDefault();
          event.stopPropagation();
          close();
        }
      }}
      onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget)) setOpen(false);
      }}
    >
      <Button
        ref={trigger}
        variant="glassPrimary"
        size="icon"
        className="floating-nav-trigger"
        aria-label={open ? "收起导航" : "展开导航"}
        aria-expanded={open}
        aria-controls={navigationId}
        title={open ? "收起导航" : "Heartbeat 导航"}
        onClick={() => setOpen((value) => !value)}
      >
        <Icon name={open ? "close" : "pulse"} />
      </Button>
      <nav
        id={navigationId}
        className="floating-nav-actions"
        aria-label="主导航"
        aria-hidden={!open}
        inert={!open}
      >
        <ul className="floating-nav-items">
          {destinations.map((destination) => (
            <li className="floating-nav-item" key={destination.href}>
              <Link
                href={destination.href}
                className={buttonVariants({
                  variant: "glass",
                  size: "icon",
                  className: "floating-nav-orb",
                })}
                aria-label={destination.label}
                aria-current={
                  pathname === destination.href ||
                  (destination.href !== "/" && pathname.startsWith(`${destination.href}/`))
                    ? "page"
                    : undefined
                }
                onClick={close}
              >
                <Icon name={destination.icon} />
                <span className="floating-nav-label">{destination.label}</span>
              </Link>
            </li>
          ))}
          <li className="floating-nav-item">
            <Button
              variant="glass"
              size="icon"
              className="floating-nav-orb"
              aria-label={isDark ? "切换到浅色" : "切换到深色"}
              onClick={() => {
                toggle();
                close();
              }}
            >
              <Icon name={isDark ? "sun" : "moon"} />
              <span className="floating-nav-label">{isDark ? "浅色模式" : "深色模式"}</span>
            </Button>
          </li>
          <li className="floating-nav-item">
            <Button
              variant="glass"
              size="icon"
              className="floating-nav-orb"
              aria-label={leaving ? "正在退出" : "退出登录"}
              disabled={leaving}
              onClick={() => void leave()}
            >
              <Icon name="logout" />
              <span className="floating-nav-label">{leaving ? "正在退出…" : "退出登录"}</span>
            </Button>
          </li>
        </ul>
        {error ? (
          <p className="floating-nav-error" role="alert">
            退出失败，请重试。
          </p>
        ) : null}
      </nav>
    </div>
  );
}
