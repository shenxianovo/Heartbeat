import {
  Calendar,
  Check,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  ChartNoAxesGantt,
  HeartPulse,
  House,
  ListFilter,
  LogOut,
  Minus,
  Monitor,
  Moon,
  Plus,
  RefreshCw,
  Server,
  Sun,
  X,
  type LucideProps,
} from "lucide-react";

const icons = {
  calendar: Calendar,
  check: Check,
  chevronDown: ChevronDown,
  chevronLeft: ChevronLeft,
  chevronRight: ChevronRight,
  close: X,
  filter: ListFilter,
  home: House,
  logout: LogOut,
  minus: Minus,
  monitor: Monitor,
  moon: Moon,
  plus: Plus,
  pulse: HeartPulse,
  refresh: RefreshCw,
  server: Server,
  sun: Sun,
  timeline: ChartNoAxesGantt,
} as const;

export function Icon({ name, ...props }: LucideProps & { name: keyof typeof icons }) {
  const Glyph = icons[name];
  return <Glyph size={16} strokeWidth={1.7} aria-hidden="true" {...props} />;
}
