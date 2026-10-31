import type { ReactNode } from "react";
import { Link } from "@tanstack/react-router";
import type { Version } from "@/api/types";
import { useStatements } from "@/components/applications/parts";
import { Mono } from "@/components/ui";
import { useNow } from "@/hooks/useNow";
import { useCatalog } from "@/hooks/useApplications";
import {
  installDetails,
  keyOf,
  platformOf,
  type Catalog,
  type Membership as ClueMembership,
  type Product,
} from "@/lib/applications";
import { ago } from "@/lib/time";
import { Token } from "../token/Token";

function Section({ title, aside, children }: { title: string; aside?: ReactNode; children: ReactNode }) {
  return (
    <section>
      <div className="mb-1.5 flex items-baseline gap-2">
        <h3 className="text-fg-3 text-xs">{title}</h3>
        <span className="flex-1" />
        {aside && <span className="text-fg-3 text-xs">{aside}</span>}
      </div>
      {children}
    </section>
  );
}

/** 本机应用属于哪个产品，以及凭哪条线索。 */
export function Membership({ install }: { install: Version }) {
  const catalog = useCatalog();
  if (!catalog.data) return null;
  const product = catalog.data.productOf(install);
  const hasClues = (catalog.data.installs.find((x) => x.version.entityId === install.entityId)?.clues.length ?? 0) > 0;
  const clue = product?.memberships.find((m) =>
    catalog.data.installsOf(m.identifier).some((x) => x.version.entityId === install.entityId),
  )?.identifier;
  return (
    <Section title="属于">
      {product ? (
        <p className="flex flex-wrap items-center gap-1.5 text-[13px]">
          <Token version={product.version} />
          {clue && (
            <span className="text-fg-3">
              凭 <Mono>{clue.value}</Mono>
            </span>
          )}
        </p>
      ) : (
        <p className="text-fg-3 text-[13px]">
          {hasClues ? "未归属，等应用识别 Observer 认出它" : "没有跨设备线索，归不进产品"}
        </p>
      )}
    </Section>
  );
}

/** 产品面板：各平台的线索和归到它的安装，以及每条线索的判断。 */
export function ProductSections({ product }: { product: Version }) {
  const catalog = useCatalog();
  const current = catalog.data?.products.find((p) => p.version.entityId === product.entityId);
  if (!current || !catalog.data) return null;
  return (
    <>
      <Installs product={current} catalog={catalog.data} />
      <Judgements product={current} />
    </>
  );
}

function Installs({ product, catalog }: { product: Product; catalog: Catalog }) {
  return (
    <Section title="线索与安装">
      <ul className="grid gap-2">
        {product.memberships.map(({ identifier }) => {
          const list = catalog.installsOf(identifier);
          const details = installDetails(list.map((install) => install.version));
          return (
            <li key={keyOf(identifier)}>
              <p className="flex items-baseline gap-2 text-[13px]">
                <span className="text-fg-2 w-14 shrink-0 text-xs">{platformOf(identifier)}</span>
                <Mono className="min-w-0 truncate">{identifier.value}</Mono>
              </p>
              <div className="mt-0.5 ml-16 flex flex-wrap gap-1">
                {list.length === 0 ? (
                  <span className="text-fg-3 text-xs">还没有出现在任何设备上</span>
                ) : (
                  list.map((install, i) => (
                    <Token key={install.version.entityId} version={install.version} size="sm" detail={details[i]} />
                  ))
                )}
              </div>
            </li>
          );
        })}
      </ul>
    </Section>
  );
}

/** 判断记录：一条线索归属一行。判错时在它的实体页删除。 */
function Judgements({ product }: { product: Product }) {
  const list = [...product.memberships].sort((a, b) => b.version.recordedAt.localeCompare(a.version.recordedAt));
  return (
    <Section title="判断记录" aside={`${list.length} 条`}>
      <ol className="border-line grid gap-3 border-l pl-3">
        {list.map((membership) => (
          <Judgement key={membership.version.entityId} membership={membership} />
        ))}
      </ol>
    </Section>
  );
}

function Judgement({ membership }: { membership: ClueMembership }) {
  const now = useNow(60_000);
  const statements = useStatements(membership.version.entityId);
  const statement = statements.data?.get(membership.version.versionId);
  return (
    <li className="relative">
      <span className="bg-fg-3 absolute top-[7px] -left-[15.5px] size-[6px] rounded-full" />
      <Link
        to="/entities/$entityId"
        params={{ entityId: membership.version.entityId }}
        className="block text-[13px] leading-snug break-all hover:underline hover:underline-offset-4"
      >
        {membership.identifier.value}
      </Link>
      <p className="text-fg-3 mt-1 flex flex-wrap items-center gap-1 text-xs">
        {statement && <Token versionId={statement.content.observerVersionId} size="sm" />}
        <span>{ago(now - Date.parse(membership.version.recordedAt))}</span>
        {statement?.content.inputSetVersionId && (
          <>
            <span>· 依据</span>
            <Token versionId={statement.content.inputSetVersionId} size="sm" />
          </>
        )}
      </p>
    </li>
  );
}
