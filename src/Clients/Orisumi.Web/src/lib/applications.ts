import type { EntitySchema, InputSet, Observation, Version, VersionSubmission } from "@/api/types";
import { distinctSegment } from "./roles";

// 应用产品和本机应用的归属：本机应用的跨设备线索写在哪个产品的当前版本里，它就属于哪个产品。
// 规则见 Docs observers/application-identity/index.mdx#membership，修改的写法见 clients/web.mdx#application-edits。

export const PRODUCTS = "application-products";
export const LOCAL_APPLICATIONS = "local-applications";
export const USERS = "users";
export const PRODUCT_SCHEMA_ID = "019e1bbb-ec00-7000-8000-000000000004";
export const USER_SCHEMA_ID = "019e1bbb-ec00-7000-8000-000000000006";

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

export interface Product {
  version: Version;
  name: string;
  identifiers: Identifier[];
}

export interface Install {
  version: Version;
  /** 跨设备线索，按它们归属。 */
  clues: Identifier[];
}

export interface Catalog {
  products: Product[];
  installs: Install[];
  /** 这条线索属于哪个产品；写在多个产品中时，取当前版本保存得最晚的那个。 */
  ownerOf(identifier: Identifier): Product | null;
  /** 本机应用属于哪个产品。 */
  productOf(install: Version): Product | null;
  /** 拥有这条线索的本机应用。 */
  installsOf(identifier: Identifier): Install[];
}

const later = (a: Product, b: Product) =>
  Date.parse(a.version.recordedAt) >= Date.parse(b.version.recordedAt) ? a : b;

export function buildCatalog(productVersions: readonly Version[], installVersions: readonly Version[]): Catalog {
  const products = productVersions.map((version) => {
    const content = version.content as { name?: unknown };
    return {
      version,
      name: typeof content.name === "string" ? content.name : "",
      identifiers: identifiersOf(content),
    };
  });
  const installs = installVersions.map((version) => ({
    version,
    clues: identifiersOf(version.content).filter(isCrossDevice),
  }));

  const owners = new Map<string, Product>();
  for (const product of products)
    for (const identifier of product.identifiers) {
      const key = keyOf(identifier);
      const current = owners.get(key);
      owners.set(key, current ? later(current, product) : product);
    }
  const byClue = new Map<string, Install[]>();
  const byEntity = new Map<string, Install>();
  for (const install of installs) {
    byEntity.set(install.version.entityId, install);
    for (const clue of install.clues) byClue.set(keyOf(clue), [...(byClue.get(keyOf(clue)) ?? []), install]);
  }

  const ownerOf = (identifier: Identifier) => owners.get(keyOf(identifier)) ?? null;
  return {
    products,
    installs,
    ownerOf,
    productOf(version) {
      const clues = byEntity.get(version.entityId)?.clues ?? identifiersOf(version.content).filter(isCrossDevice);
      return clues.map(ownerOf).reduce<Product | null>((found, p) => (p ? (found ? later(found, p) : p) : found), null);
    },
    installsOf: (identifier) => byClue.get(keyOf(identifier)) ?? [],
  };
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

// —— 修改 ——

export type Edit =
  | { kind: "rename"; product: Product; name: string }
  /** 把线索加入另一个产品或新产品；原来所在的产品移出它。 */
  | { kind: "move"; identifier: Identifier; to: Product | { name: string } }
  /** 把线索移出产品，应用识别 Observer 下一轮重新判断它。 */
  | { kind: "release"; identifier: Identifier }
  /** 把 from 的线索全部加入 into，再删除 from。 */
  | { kind: "merge"; from: Product; into: Product }
  /** 删除产品，它的线索由应用识别 Observer 重新判断。 */
  | { kind: "delete"; product: Product };

export interface EditContext {
  /** 用户本人的当前版本；可以是同一批中排在前面的新版本。 */
  observerVersionId: string;
  productSchemaVersionId: string;
  newId: () => string;
}

export interface EditPlan {
  versions: VersionSubmission[];
  /** 提交成功后再删除的实体。 */
  deletes: string[];
  /** 给用户看的一句话。 */
  summary: string;
}

interface Draft {
  product: Product | null;
  name: string;
  identifiers: Identifier[];
}

const without = (list: Identifier[], identifier: Identifier) => list.filter((i) => keyOf(i) !== keyOf(identifier));
const isProduct = (to: Product | { name: string }): to is Product => "version" in to;

/**
 * 一次修改要提交的版本，按文档的顺序：InputSet，产品的版本（先写加入线索的，再写移出线索的），
 * 每个产品版本一份 Observation。InputSet 列出涉及的产品在修改前的当前版本，以及线索所属的本机应用。
 */
export function planEdit(edit: Edit, catalog: Catalog, context: EditContext): EditPlan {
  if (edit.kind === "delete")
    return { versions: [], deletes: [edit.product.version.entityId], summary: `已删除「${edit.product.name}」` };
  const drafts: Draft[] = [];
  const read: Version[] = [];
  const deletes: string[] = [];
  let summary: string;

  const owners = (identifier: Identifier) =>
    catalog.products.filter((p) => p.identifiers.some((i) => keyOf(i) === keyOf(identifier)));

  switch (edit.kind) {
    case "rename": {
      const name = edit.name.trim();
      drafts.push({ product: edit.product, name, identifiers: edit.product.identifiers });
      summary = `已把「${edit.product.name}」改名为「${name}」`;
      break;
    }
    case "move": {
      const { identifier, to } = edit;
      const sources = owners(identifier).filter((p) => !isProduct(to) || p.version.entityId !== to.version.entityId);
      if (isProduct(to))
        drafts.push({ product: to, name: to.name, identifiers: [...without(to.identifiers, identifier), identifier] });
      else drafts.push({ product: null, name: to.name.trim(), identifiers: [identifier] });
      for (const source of sources)
        drafts.push({ product: source, name: source.name, identifiers: without(source.identifiers, identifier) });
      read.push(...catalog.installsOf(identifier).map((install) => install.version));
      summary = `已把 ${identifier.value} 归入${isProduct(to) ? "" : "新产品"}「${isProduct(to) ? to.name : to.name.trim()}」`;
      break;
    }
    case "release": {
      for (const source of owners(edit.identifier))
        drafts.push({ product: source, name: source.name, identifiers: without(source.identifiers, edit.identifier) });
      read.push(...catalog.installsOf(edit.identifier).map((install) => install.version));
      summary = `已把 ${edit.identifier.value} 移出，等待重新识别`;
      break;
    }
    case "merge": {
      const added = edit.from.identifiers.filter((i) => !edit.into.identifiers.some((j) => keyOf(i) === keyOf(j)));
      drafts.push({ product: edit.into, name: edit.into.name, identifiers: [...edit.into.identifiers, ...added] });
      read.push(
        edit.from.version,
        ...edit.from.identifiers.flatMap((i) => catalog.installsOf(i).map((x) => x.version)),
      );
      deletes.push(edit.from.version.entityId);
      summary = `已把「${edit.from.name}」合并进「${edit.into.name}」`;
      break;
    }
  }

  read.unshift(...drafts.flatMap((draft) => (draft.product ? [draft.product.version] : [])));
  const inputSet: VersionSubmission<InputSet> = {
    entityId: context.newId(),
    versionId: context.newId(),
    previousVersionId: null,
    category: "input_set",
    schemaVersionId: null,
    content: { members: [...new Set(read.map((version) => version.versionId))] },
  };
  const products: VersionSubmission[] = drafts.map((draft) => ({
    entityId: draft.product?.version.entityId ?? context.newId(),
    versionId: context.newId(),
    previousVersionId: draft.product?.version.versionId ?? null,
    category: "business",
    schemaVersionId: context.productSchemaVersionId,
    content: { name: draft.name, identifiers: draft.identifiers },
  }));
  const observations: VersionSubmission<Observation>[] = products.map((product) => ({
    entityId: context.newId(),
    versionId: context.newId(),
    previousVersionId: null,
    category: "observation",
    schemaVersionId: null,
    content: {
      observerVersionId: context.observerVersionId,
      contentVersionId: product.versionId,
      inputSetVersionId: inputSet.versionId,
      startAt: null,
      endAt: null,
      timeZone: null,
    },
  }));
  return { versions: [inputSet, ...products, ...observations], deletes, summary };
}

// —— 首次写入时要保存的 EntitySchema ——

/** 与应用识别 Observer 登记的内容相同，见 Docs observers/application-identity/application-product.mdx#schema。 */
export const PRODUCT_SCHEMA: EntitySchema = {
  displayName: "应用产品",
  description:
    "表示用户认知中的一款软件，保存它的名称和属于它的跨设备线索；本机应用的跨设备线索出现在哪个产品中，就属于哪个产品。",
  resourceName: PRODUCTS,
  fields: {
    name: { type: "string", description: "产品的名称", required: true, nullable: false },
    identifiers: {
      type: "array",
      description: "属于这个产品的跨设备线索，顺序无意义；没有线索时为 []",
      required: true,
      nullable: false,
      items: {
        type: "object",
        description: "一项跨设备线索，由 scheme 和 value 共同解释，与本机应用的标识线索相同",
        nullable: false,
        fields: {
          scheme: { type: "string", description: "线索的种类，例如 macos.bundle-id", required: true, nullable: false },
          value: { type: "string", description: "线索的原始值", required: true, nullable: false },
        },
      },
    },
  },
};

/** 见 Docs observers/user/user.mdx#schema。 */
export const USER_SCHEMA: EntitySchema = {
  displayName: "用户本人",
  description: "表示拥有这些数据的用户本人；用户确认、否定或陈述时，作为 Observer 对陈述负责。",
  resourceName: USERS,
  fields: { name: { type: "string", description: "用户的称呼，没有填写时为 null", required: true, nullable: true } },
};

/** 一个 EntitySchema 的首版。 */
export function schemaVersion(entityId: string, versionId: string, content: EntitySchema): VersionSubmission {
  return { entityId, versionId, previousVersionId: null, category: "entity_schema", schemaVersionId: null, content };
}

/** 版本之间的变化，用在判断记录里。 */
export function describeChange(version: Version, previous: Version | null): string[] {
  const now = version.content as { name?: string };
  const ids = identifiersOf(version.content);
  if (!previous) return [`新建「${now.name ?? ""}」`, ...ids.map((i) => `加入 ${i.value}`)];
  const before = previous.content as { name?: string };
  const was = identifiersOf(previous.content);
  const changes: string[] = [];
  if (before.name !== now.name) changes.push(`改名为「${now.name ?? ""}」`);
  for (const i of ids) if (!was.some((j) => keyOf(i) === keyOf(j))) changes.push(`加入 ${i.value}`);
  for (const j of was) if (!ids.some((i) => keyOf(i) === keyOf(j))) changes.push(`移出 ${j.value}`);
  return changes.length ? changes : ["内容相同"];
}
