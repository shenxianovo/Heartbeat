import {
  Calendar,
  Check,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  ListFilter,
  Minus,
  Monitor,
  Moon,
  Plus,
  RefreshCw,
  Sun,
  type LucideProps,
} from "lucide-react";

const icons = {
  calendar: Calendar,
  check: Check,
  chevronDown: ChevronDown,
  chevronLeft: ChevronLeft,
  chevronRight: ChevronRight,
  filter: ListFilter,
  minus: Minus,
  monitor: Monitor,
  moon: Moon,
  plus: Plus,
  refresh: RefreshCw,
  sun: Sun,
} as const;

export function Icon({ name, ...props }: LucideProps & { name: keyof typeof icons }) {
  const Glyph = icons[name];
  return <Glyph size={16} strokeWidth={1.7} aria-hidden="true" {...props} />;
}
