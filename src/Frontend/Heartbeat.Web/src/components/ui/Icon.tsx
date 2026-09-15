import type { SVGProps } from "react";

const paths = {
  calendar: (
    <>
      <path d="M8 2v4m8-4v4M3 10h18" />
      <rect x="3" y="4" width="18" height="18" rx="2" />
    </>
  ),
  chevronDown: <path d="m6 9 6 6 6-6" />,
  chevronLeft: <path d="m15 18-6-6 6-6" />,
  chevronRight: <path d="m9 18 6-6-6-6" />,
  refresh: (
    <>
      <path d="M20 11a8 8 0 1 0-2.4 6.1M20 4v7h-7" />
    </>
  ),
  filter: (
    <>
      <path d="M4 7h16M7 12h10m-7 5h4" />
    </>
  ),
  plus: <path d="M12 5v14M5 12h14" />,
  minus: <path d="M5 12h14" />,
  check: <path d="m5 12 4 4L19 6" />,
  close: <path d="m6 6 12 12M6 18 18 6" />,
  monitor: (
    <>
      <rect x="3" y="3" width="18" height="13" rx="2" />
      <path d="M8 21h8m-4-5v5" />
    </>
  ),
} as const;

export function Icon({ name, ...props }: SVGProps<SVGSVGElement> & { name: keyof typeof paths }) {
  return (
    <svg
      width="16"
      height="16"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      {...props}
    >
      {paths[name]}
    </svg>
  );
}
