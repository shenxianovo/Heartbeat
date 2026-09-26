import type { Metadata } from "next";
import type { PropsWithChildren } from "react";

import { Providers } from "@/app/providers";
import { themeBootScript } from "@/lib/theme";

import "./globals.css";

export const metadata: Metadata = {
  title: "Heartbeat · 回放",
  description: "查看 Timeline 中由 Collector 采集的 Record。",
};

export default function RootLayout({ children }: PropsWithChildren) {
  return (
    <html lang="zh-CN" suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: themeBootScript }} />
      </head>
      <body>
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
