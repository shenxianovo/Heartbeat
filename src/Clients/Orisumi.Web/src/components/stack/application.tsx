import type { ReactNode } from "react";
import { Link } from "@tanstack/react-router";
import type { Version } from "@/api/types";
import { useStatements } from "@/components/applications/parts";
import { Mono } from "@/components/ui";
import { useEntityVersions } from "@/hooks/queries";
import { useNow } from "@/hooks/useNow";
import { useCatalog } from "@/hooks/useApplications";
import { describeChange, installDetails, keyOf, platformOf, type Catalog, type Product } from "@/lib/applications";
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
  const clue = product?.identifiers.find((i) =>
    catalog.data.installsOf(i).some((x) => x.version.entityId === install.entityId),
  );
  return (
    <Section title="属于" aside={<EditLink />}>
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

/** 产品面板：各平台的线索和归到它的安装，以及每次修改由谁写下。 */
export function ProductSections({ product }: { product: Version }) {
  const catalog = useCatalog();
  const current = catalog.data?.products.find((p) => p.version.entityId === product.entityId);
  return (
    <>
      {current && catalog.data && <Installs product={current} catalog={catalog.data} />}
      <History entityId={product.entityId} />
    </>
  );
}

function Installs({ product, catalog }: { product: Product; catalog: Catalog }) {
  return (
    <Section title="线索与安装" aside={<EditLink />}>
      {product.identifiers.length === 0 ? (
        <p className="text-fg-3 text-[13px]">没有线索，可以在应用页中删除它</p>
      ) : (
        <ul className="grid gap-2">
          {product.identifiers.map((identifier) => {
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
      )}
    </Section>
  );
}

/** 产品的每个版本、它改了什么、由谁写下，以及依据。 */
function History({ entityId }: { entityId: string }) {
  const versions = useEntityVersions(entityId);
  const now = useNow(60_000);
  const statements = useStatements(entityId);
  const list = versions.data ?? [];
  return (
    <Section title="修改记录" aside={list.length ? `${list.length} 个版本` : undefined}>
      <ol className="border-line grid gap-3 border-l pl-3">
        {[...list].reverse().map((version) => {
          const index = list.indexOf(version);
          const statement = statements.data?.get(version.versionId);
          return (
            <li key={version.versionId} className="relative">
              <span className="bg-fg-3 absolute top-[7px] -left-[15.5px] size-[6px] rounded-full" />
              <ul className="text-[13px] leading-snug">
                {describeChange(version, list[index - 1] ?? null).map((change) => (
                  <li key={change} className="break-all">
                    {change}
                  </li>
                ))}
              </ul>
              <p className="text-fg-3 mt-1 flex flex-wrap items-center gap-1 text-xs">
                {statement ? (
                  <>
                    <Token versionId={statement.content.observerVersionId} size="sm" />
                    <span>{ago(now - Date.parse(version.recordedAt))}</span>
                    {statement.content.inputSetVersionId && (
                      <>
                        <span>· 依据</span>
                        <Token versionId={statement.content.inputSetVersionId} size="sm" />
                      </>
                    )}
                  </>
                ) : (
                  <span>{ago(now - Date.parse(version.recordedAt))}</span>
                )}
              </p>
            </li>
          );
        })}
      </ol>
    </Section>
  );
}

function EditLink() {
  return (
    <Link to="/applications" className="hover:text-fg">
      在应用页中修改 →
    </Link>
  );
}
