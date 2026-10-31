import { useEffect, useState } from "react";
import { Check, Copy } from "lucide-react";
import { management, type Host, type Instance, type Operation, type Package, type Pairing } from "@/api/management";
import { Button, Dialog } from "@/components/ui";
import { Mark } from "@/components/token/Token";
import { useOperation } from "@/hooks/useManagement";
import { pad } from "@/lib/time";

/** 签发配对码。带 host 时，配对码只能让这个托管方重新接入。 */
export function PairingDialog({ host, now, onClose }: { host: Host | null; now: number; onClose: () => void }) {
  const [pairing, setPairing] = useState<Pairing>();
  const [error, setError] = useState<string>();
  const [copied, setCopied] = useState(false);
  const hostId = host?.hostId;

  useEffect(() => {
    management<Pairing>("/pairings", "POST", hostId ? { hostId } : {}).then(setPairing, (failure: Error) =>
      setError(failure.message),
    );
  }, [hostId]);

  const left = pairing ? Math.max(0, Math.ceil((Date.parse(pairing.expiresAt) - now) / 1000)) : 0;
  const expired = !!pairing && left === 0;

  return (
    <Dialog title={host ? `重新接入 ${host.name}` : "接入设备或服务器"} onClose={onClose}>
      {error ? (
        <p className="text-danger text-[13px]">{error}</p>
      ) : (
        <>
          <div className="flex items-center justify-center gap-1.5 py-3" aria-live="polite">
            {(pairing?.code ?? "········").split("").map((letter, index) => (
              <span
                key={index}
                className={`bg-panel grid h-12 w-9 place-items-center rounded-lg font-mono text-2xl font-medium ${expired ? "text-fg-3 line-through" : ""}`}
              >
                {letter}
              </span>
            ))}
          </div>
          <p className="text-fg-3 mb-4 text-center text-xs tabular-nums">
            {!pairing
              ? "正在签发"
              : expired
                ? "已失效"
                : `${Math.floor(left / 60)}:${pad(left % 60)} 后失效，只能用一次`}
          </p>
          <p className="text-fg-2 text-[13px] leading-relaxed">
            {host
              ? `在 ${host.name} 上输入这个配对码，它会以原来的身份回来，继续发送本机还没发出去的数据。`
              : "在新设备的 Orisumi 中打开“设置 › 连接”输入它。服务器上，把它作为 --pairing-code 交给 Runtime。"}
          </p>
        </>
      )}
      <div className="mt-5 flex justify-end gap-2">
        {pairing && !expired && (
          <Button
            onClick={() => {
              void navigator.clipboard.writeText(pairing.code).then(() => setCopied(true));
            }}
          >
            {copied ? <Check className="size-3.5" /> : <Copy className="size-3.5" />}
            {copied ? "已拷贝" : "拷贝"}
          </Button>
        )}
        <Button primary onClick={onClose}>
          完成
        </Button>
      </div>
    </Dialog>
  );
}

/** 向一个在线的托管方安装 Observer：先读它能装的包，再选一个。 */
export function InstallDialog({
  host,
  instances,
  onClose,
}: {
  host: Host;
  instances: Instance[];
  onClose: () => void;
}) {
  const catalog = useOperation(host.hostId);
  const install = useOperation(host.hostId);
  const [chosen, setChosen] = useState<string>();
  const startCatalog = catalog.start;

  useEffect(() => {
    void startCatalog("catalog");
    // eslint-disable-next-line react-hooks/exhaustive-deps -- 打开时读一次
  }, []);

  const packages = catalog.operation?.state === "succeeded" ? (catalog.operation.result?.packages ?? []) : null;
  const catalogProblem =
    catalog.error ??
    (catalog.operation && ["failed", "expired", "unconfirmed"].includes(catalog.operation.state)
      ? (catalog.operation.error ?? "没能读到可以安装的 Observer")
      : null);

  return (
    <Dialog title={`安装到 ${host.name}`} onClose={onClose} wide>
      {catalogProblem ? (
        <p className="text-danger text-[13px]">{catalogProblem}</p>
      ) : !packages ? (
        <p className="text-fg-3 py-6 text-center text-[13px]">正在读取 {host.name} 可以安装的 Observer</p>
      ) : !packages.length ? (
        <p className="text-fg-3 py-6 text-center text-[13px]">没有可以安装的 Observer</p>
      ) : (
        <ul className="border-line -mx-1 divide-y">
          {packages.map((pkg) => (
            <PackageRow
              key={pkg.id}
              pkg={pkg}
              fresh={isFresh(pkg, instances)}
              state={chosen === pkg.id ? install.operation?.state : undefined}
              error={chosen === pkg.id ? installProblem(install.error, install.operation) : null}
              busy={install.running}
              onInstall={() => {
                setChosen(pkg.id);
                void install.start("install", { packageId: pkg.id, newInstance: isFresh(pkg, instances) });
              }}
            />
          ))}
        </ul>
      )}
      <div className="mt-5 flex justify-end">
        <Button primary onClick={onClose}>
          完成
        </Button>
      </div>
    </Dialog>
  );
}

function installProblem(error: string | undefined, operation: Operation | undefined): string | null {
  if (error) return error;
  switch (operation?.state) {
    case "failed":
      return operation.error ?? "安装失败";
    case "expired":
      return "设备没有响应，没有安装";
    case "unconfirmed":
      return "没有收到结果，稍后在列表中看是否已安装";
    default:
      return null;
  }
}

/** 这个包原来的实例已删除，安装会是一个新的程序实例。 */
function isFresh(pkg: Package, instances: Instance[]): boolean {
  const same = instances.filter((instance) => instance.packageId === pkg.id);
  return same.length > 0 && same.every((instance) => instance.removed);
}

function PackageRow({
  pkg,
  fresh,
  state,
  error,
  busy,
  onInstall,
}: {
  pkg: Package;
  fresh: boolean;
  state: string | undefined;
  error: string | null;
  busy: boolean;
  onInstall: () => void;
}) {
  const installed = !!pkg.installedVersion && !fresh;
  return (
    <li className="flex items-center gap-3 px-1 py-3">
      <Mark role="source" color={null} size="lg" framed />
      <div className="min-w-0 flex-1">
        <p className="text-[13px]">
          {pkg.name} <span className="text-fg-3 font-mono text-[11px]">{pkg.version}</span>
        </p>
        <p className="text-fg-3 text-xs">
          {error ??
            (!pkg.compatible
              ? "不适用于这台机器"
              : fresh
                ? "原来的实例已删除，安装后是一个新的程序实例"
                : (pkg.description ?? ""))}
        </p>
      </div>
      {state === "succeeded" || installed ? (
        <span className="text-fg-3 text-xs">已安装</span>
      ) : state === "queued" || state === "delivered" ? (
        <span className="text-fg-3 text-xs">正在安装…</span>
      ) : (
        <Button primary disabled={!pkg.compatible || busy} onClick={onInstall}>
          {fresh ? "新建实例" : "安装"}
        </Button>
      )}
    </li>
  );
}
