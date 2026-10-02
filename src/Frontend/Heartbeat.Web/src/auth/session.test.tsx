import { QueryClient, QueryClientProvider, useQuery } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { SessionCacheGuard } from "./session";

const session = vi.hoisted(() => ({ subject: null as string | null }));
vi.mock("react-oidc-context", () => ({
  useAuth: () => ({ user: session.subject ? { profile: { sub: session.subject } } : null }),
}));

it("loads the first query after restoring or changing the signed-in account", async () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  function Objects() {
    const query = useQuery({
      queryKey: ["owner", session.subject, "objects"],
      queryFn: async ({ signal }) => {
        await new Promise<void>((resolve, reject) => {
          const timer = setTimeout(resolve, 5);
          signal.addEventListener("abort", () => {
            clearTimeout(timer);
            reject(new Error("cancelled"));
          });
        });
        return session.subject;
      },
    });
    return <p>{query.data ?? "loading"}</p>;
  }
  function App() {
    return (
      <QueryClientProvider client={client}>
        <SessionCacheGuard>{session.subject ? <Objects /> : null}</SessionCacheGuard>
      </QueryClientProvider>
    );
  }
  session.subject = null;
  const view = render(<App />);
  session.subject = "owner-a";
  view.rerender(<App />);
  await screen.findByText("owner-a");
  session.subject = "owner-b";
  view.rerender(<App />);
  await screen.findByText("owner-b");
  expect(client.getQueryData(["owner", "owner-a", "objects"])).toBeUndefined();
  client.clear();
});
