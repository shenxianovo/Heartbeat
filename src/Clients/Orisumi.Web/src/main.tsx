import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider, createRouter } from "@tanstack/react-router";
import { createSource, initialSourceKind, SourceProvider } from "@/api/source";
import { applyTheme, readTheme } from "@/lib/preferences";
import { routeTree } from "./routeTree.gen";
import "./styles/globals.css";

applyTheme(readTheme());

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

const kind = initialSourceKind();
const source = createSource(kind);

const rootElement = document.getElementById("root");
if (!rootElement) throw new Error("找不到 #root");

createRoot(rootElement).render(
  <StrictMode>
    <SourceProvider kind={kind} source={source}>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </SourceProvider>
  </StrictMode>,
);
