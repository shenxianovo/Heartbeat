import type { NextConfig } from "next";

function backendUrl(): string {
  const configured = process.env.HEARTBEAT_API_URL ?? process.env.BACKEND_URL;
  return (configured ?? "http://127.0.0.1:8080").replace(/\/$/, "");
}

const nextConfig: NextConfig = {
  async rewrites() {
    return [
      {
        source: "/api/:path*",
        destination: `${backendUrl()}/api/:path*`,
      },
    ];
  },
};

export default nextConfig;
