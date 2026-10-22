import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { TanStackRouterVite } from "@tanstack/router-plugin/vite";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.dirname(fileURLToPath(import.meta.url));

// Aspire 把分配到的端口经 PORT 注入；独立运行时回落到 5173。
const port = process.env.PORT ? Number(process.env.PORT) : 5173;

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    TanStackRouterVite({ routesDirectory: "./src/routes", generatedRouteTree: "./src/routeTree.gen.ts" }),
    react(),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      "@": path.resolve(root, "./src"),
    },
  },
  server: {
    host: true,
    port,
    strictPort: true,
    // 经 nginx 同源反代时，HMR 沿用浏览器访问的主机与端口，无需额外 clientPort。
    proxy: {
      // Aspire/nginx 对外把 API 挂在 /api；直连后端时指向 8080。
      "/api": {
        target: "http://localhost:8080",
        changeOrigin: true,
        rewrite: (p) => p.replace(/^\/api/, ""),
      },
    },
  },
});
