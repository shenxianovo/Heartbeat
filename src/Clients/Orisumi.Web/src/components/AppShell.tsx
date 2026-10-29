import { useState, type FormEvent } from "react";
import { Link, Outlet, useNavigate } from "@tanstack/react-router";
import { isUnreadable } from "@/api/client";
import { useSource } from "@/api/source";
import { readTheme, readTone, saveTheme, saveTone, type Theme, type Tone } from "@/lib/preferences";
import { isUuidv7 } from "@/lib/uuid";
import { cn } from "@/lib/utils";
import { Stack } from "./stack/Stack";
import { HighlightProvider } from "./token/context";
import { Segmented } from "./ui";

const NAV = [
  { to: "/", label: "日", exact: true },
  { to: "/applications", label: "应用", exact: true },
  { to: "/entities", label: "实体", exact: false },
  { to: "/observers", label: "Observer", exact: true },
] as const;

export function AppShell() {
  return (
    <HighlightProvider>
      <Shell />
      <Stack />
    </HighlightProvider>
  );
}

function Shell() {
  return (
    <div className="min-h-screen">
      <div className="mx-auto max-w-[1080px] px-4 pt-5 pb-16">
        <header className="mb-6 flex flex-wrap items-center gap-x-4 gap-y-3">
          <Link to="/" className="flex items-center gap-2 font-medium">
            <img src="/mascots/macos-128.png" alt="" className="size-6" />
            Orisumi
          </Link>
          <nav aria-label="页面" className="bg-panel flex gap-0.5 rounded-lg p-[3px]">
            {NAV.map((item) => (
              <Link
                key={item.to}
                to={item.to}
                activeOptions={{ exact: item.exact, includeSearch: false }}
                className="text-fg-2 hover:text-fg rounded-md px-3 py-1 text-[13px]"
                activeProps={{ className: "bg-card !text-fg" }}
              >
                {item.label}
              </Link>
            ))}
          </nav>
          <div className="flex-1" />
          <IdSearch />
          <ToneSwitch />
          <ThemeSwitch />
        </header>
        <main>
          <Outlet />
        </main>
      </div>
    </div>
  );
}

function ToneSwitch() {
  const [tone, setTone] = useState<Tone>(readTone);
  return (
    <Segmented
      label="基调"
      value={tone}
      onChange={(value) => {
        setTone(value);
        saveTone(value);
      }}
      options={[
        { value: "gray", label: "灰" },
        { value: "sky", label: "蓝" },
      ]}
    />
  );
}

function ThemeSwitch() {
  const [theme, setTheme] = useState<Theme>(readTheme);
  return (
    <Segmented
      label="主题"
      value={theme}
      onChange={(value) => {
        setTheme(value);
        saveTheme(value);
      }}
      options={[
        { value: "system", label: "系统" },
        { value: "light", label: "浅" },
        { value: "dark", label: "深" },
      ]}
    />
  );
}

/** 接受 EntityId 和 VersionId：先按 EntityId 读取，读不到再按 VersionId 读取。 */
export function IdSearch() {
  const source = useSource();
  const navigate = useNavigate();
  const [value, setValue] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    const id = value.trim().toLowerCase();
    if (!isUuidv7(id)) {
      setError("不是 UUIDv7");
      return;
    }
    setBusy(true);
    setError(null);
    try {
      try {
        await source.getEntity(id);
        await navigate({ to: "/entities/$entityId", params: { entityId: id } });
      } catch (error) {
        if (!isUnreadable(error)) throw error;
        try {
          const version = await source.getVersion(id);
          await navigate({
            to: "/entities/$entityId",
            params: { entityId: version.entityId },
            search: { version: id },
          });
        } catch (inner) {
          if (!isUnreadable(inner)) throw inner;
          setError("找不到这个 ID");
          return;
        }
      }
      setValue("");
    } catch (error) {
      setError(error instanceof Error ? error.message : String(error));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="relative" role="search">
      <input
        aria-label="EntityId 或 VersionId"
        placeholder="EntityId 或 VersionId"
        value={value}
        disabled={busy}
        onChange={(event) => {
          setValue(event.target.value);
          setError(null);
        }}
        className={cn(
          "bg-card text-fg placeholder:text-fg-3 h-[30px] w-[230px] rounded-lg border px-2.5 font-mono text-xs outline-none max-sm:w-[150px]",
          error ? "border-danger" : "border-line-2 focus:border-line-3",
        )}
      />
      {error && (
        <p role="alert" className="text-danger absolute top-full left-0 mt-1 text-xs whitespace-nowrap">
          {error}
        </p>
      )}
    </form>
  );
}
