import type { Version } from "@/api/types";
import { distinctSegment } from "./roles";

// 应用产品和本机应用的归属：本机应用的跨设备线索有线索归属时，它属于线索归属指向的产品。
// 规则见 Docs observers/application-identity/index.mdx#membership。

export const PRODUCTS = "application-products";
export const MEMBERSHIPS = "application-memberships";
export const LOCAL_APPLICATIONS = "local-applications";

export interface Identifier {
  scheme: string;
  value: string;
}

/** 能跨设备复用的 scheme，见 Local Application · 标识线索。 */
const CROSS_DEVICE = new Set(["macos.bundle-id", "android.package-name"]);

const PLATFORMS: Record<string, string> = { macos: "macOS", android: "Android", windows: "Windows", linux: "Linux" };

export function isCrossDevice(identifier: Identifier): boolean {
  return CROSS_DEVICE.has(identifier.scheme);
}

export function platformOf(identifier: Identifier): string {
  const prefix = identifier.scheme.split(".")[0] ?? identifier.scheme;
  return PLATFORMS[prefix] ?? prefix;
}

export function keyOf(identifier: Identifier): string {
  return `${identifier.scheme}\n${identifier.value}`;
}

export function identifiersOf(content: unknown): Identifier[] {
  const items = (content as { identifiers?: unknown } | null)?.identifiers;
  if (!Array.isArray(items)) return [];
  return items.flatMap((item) => {
    const entry = item as Partial<Identifier> | null;
    return typeof entry?.scheme === "string" && typeof entry.value === "string"
      ? [{ scheme: entry.scheme, value: entry.value }]
      : [];
  });
}

export interface Membership {
  version: Version;
  identifier: Identifier;
}

export interface Product {
  version: Version;
  name: string;
  memberships: Membership[];
}

export interface Install {
  version: Version;
  /** 跨设备线索，按它们归属。 */
  clues: Identifier[];
}

export interface Catalog {
  /** 有线索归属的产品。 */
  products: Product[];
  installs: Install[];
  /** 这条线索当前属于哪个产品。 */
  ownerOf(identifier: Identifier): Product | null;
  /** 本机应用属于哪个产品。 */
  productOf(install: Version): Product | null;
  /** 拥有这条线索的本机应用。 */
  installsOf(identifier: Identifier): Install[];
}

/** targets 是线索归属引用的产品版本到实体的索引，已删除的版本不在其中。 */
export function buildCatalog(
  productVersions: readonly Version[],
  membershipVersions: readonly Version[],
  installVersions: readonly Version[],
  targets: ReadonlyMap<string, string>,
): Catalog {
  const products = new Map<string, Product>(
    productVersions.map((version) => [
      version.entityId,
      { version, name: (version.content as { name: string }).name, memberships: [] },
    ]),
  );
  const owners = new Map<string, Product>();
  for (const version of membershipVersions) {
    const content = version.content as { identifier: Identifier; productVersionId: string };
    // 指向的产品已删除时，这条线索未归属。
    const product = products.get(targets.get(content.productVersionId) ?? "");
    if (!product) continue;
    product.memberships.push({ version, identifier: content.identifier });
    owners.set(keyOf(content.identifier), product);
  }
  const installs = installVersions.map((version) => ({
    version,
    clues: identifiersOf(version.content).filter(isCrossDevice),
  }));
  const byClue = new Map<string, Install[]>();
  const byEntity = new Map<string, Install>();
  for (const install of installs) {
    byEntity.set(install.version.entityId, install);
    for (const clue of install.clues) byClue.set(keyOf(clue), [...(byClue.get(keyOf(clue)) ?? []), install]);
  }

  const ownerOf = (identifier: Identifier) => owners.get(keyOf(identifier)) ?? null;
  return {
    products: [...products.values()].filter((product) => product.memberships.length > 0),
    installs,
    ownerOf,
    productOf(version) {
      const clues = byEntity.get(version.entityId)?.clues ?? identifiersOf(version.content).filter(isCrossDevice);
      return clues.map(ownerOf).find((product) => product !== null) ?? null;
    },
    installsOf: (identifier) => byClue.get(keyOf(identifier)) ?? [],
  };
}

/** 产品最近一次判断。 */
export function latestMembership(product: Product): Membership {
  return product.memberships.reduce((a, b) =>
    Date.parse(a.version.recordedAt) >= Date.parse(b.version.recordedAt) ? a : b,
  );
}

/** 同一条线索的几个安装，各自路径里不同的那一段，例如 `…/Release/…` 和 `…/Debug/…`；只有一个时为 null。 */
export function installDetails(installs: readonly Version[]): (string | null)[] {
  const paths = installs.map(
    (install) => identifiersOf(install.content).find((i) => i.scheme === "macos.executable-path")?.value ?? "",
  );
  return paths.map((path) => (path ? distinctSegment(path, paths) : null));
}

/** 没有归入任何产品的跨设备线索，以及只有本机线索、归不进产品的本机应用。 */
export function unassigned(catalog: Catalog): { clues: Identifier[]; withoutClues: Install[] } {
  const seen = new Map<string, Identifier>();
  for (const install of catalog.installs)
    for (const clue of install.clues) if (!catalog.ownerOf(clue)) seen.set(keyOf(clue), clue);
  return {
    clues: [...seen.values()].sort((a, b) => a.value.localeCompare(b.value)),
    withoutClues: catalog.installs.filter((install) => install.clues.length === 0),
  };
}
