import { Moon, Sun } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { useAppearance, type MascotTheme } from "@/lib/storage";
import { cn } from "@/lib/utils";

const MASCOTS: { id: MascotTheme; name: string; img: string }[] = [
  { id: "windows", name: "Windows 娘", img: "/mascots/windows.png" },
  { id: "mac", name: "Mac 娘", img: "/mascots/macos.png" },
];

/** 双生主题切换：两只 Q 版头像选吉祥物（决定配色），日月钮切换明暗。 */
export function AppearanceSwitcher() {
  const { mascot, mode, setMascot, toggleMode } = useAppearance();

  return (
    <div className="flex items-center gap-1.5">
      <div className="bg-card/70 flex items-center gap-1 rounded-full border p-1 backdrop-blur">
        {MASCOTS.map((m) => (
          <Tooltip key={m.id}>
            <TooltipTrigger asChild>
              <button
                type="button"
                aria-label={`切换到${m.name}主题`}
                aria-pressed={mascot === m.id}
                onClick={() => setMascot(m.id)}
                className={cn(
                  "size-7 overflow-hidden rounded-full ring-2 transition-all",
                  mascot === m.id ? "ring-primary scale-105 shadow-sm" : "opacity-55 ring-transparent hover:opacity-90",
                )}
              >
                <img src={m.img} alt="" className="size-full object-cover" />
              </button>
            </TooltipTrigger>
            <TooltipContent>{m.name}主题</TooltipContent>
          </Tooltip>
        ))}
      </div>
      <Tooltip>
        <TooltipTrigger asChild>
          <Button variant="outline" size="icon" className="rounded-full" onClick={toggleMode} aria-label="切换明暗模式">
            {mode === "dark" ? <Sun className="size-4" /> : <Moon className="size-4" />}
          </Button>
        </TooltipTrigger>
        <TooltipContent>{mode === "dark" ? "切换到浅色" : "切换到深色"}</TooltipContent>
      </Tooltip>
    </div>
  );
}
