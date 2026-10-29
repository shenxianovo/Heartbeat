import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider, createRouter } from "@tanstack/react-router";
import { createHttpSource } from "@/api/client";
import { SourceProvider } from "@/api/source";
import { applyTheme, applyTone, readTheme, readTone } from "@/lib/preferences";
import { routeTree } from "./routeTree.gen";
import "./styles/globals.css";

applyTheme(readTheme());
applyTone(readTone());

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { refetchOnWindowFocus: false },
  },
});

const router = createRouter({ routeTree, defaultPreload: false });

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router;
  }
}

const source = createHttpSource();

const rootElement = document.getElementById("root");
if (!rootElement) throw new Error("找不到 #root");

createRoot(rootElement).render(
  <StrictMode>
    <SourceProvider source={source}>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </SourceProvider>
  </StrictMode>,
);
