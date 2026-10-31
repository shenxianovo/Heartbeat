import { useState, type ReactNode } from "react";
import { Brain, ChevronRight, FileText, PanelRight, Wrench } from "lucide-react";
import { useContentUrl } from "@/hooks/queries";
import { usePartValue } from "@/hooks/useConversations";
import {
  argumentSummary,
  inlineText,
  contextLabel,
  partText,
  productName,
  toolLabel,
  type Actor,
  type Interaction,
  type InteractionContent,
  type InteractionPart,
  type Item,
} from "@/lib/conversations";
import { clock, duration } from "@/lib/time";
import { cn } from "@/lib/utils";
import { useStack } from "../token/context";

/** 一个会话这一天的交互，按时间排列。工具调用和它的结果合成一行，推理和上下文默认收起。 */
export function Conversation({ items, product }: { items: Item[]; product: string | null | undefined }) {
  return (
    <ol className="flex flex-col">
      {items.map((item) => (
        <li
          key={item.type === "tool" ? (item.call ?? item.result)!.version.entityId : item.interaction.version.entityId}
          className="group grid grid-cols-[44px_minmax(0,1fr)] gap-x-3 py-1.5 [contain-intrinsic-size:auto_64px] [content-visibility:auto]"
        >
          <time className="text-fg-3 pt-1 text-right text-[11px] tabular-nums">{clock(itemAt(item))}</time>
          {item.type === "tool" ? (
            <ToolItem call={item.call} result={item.result} />
          ) : (
            <InteractionItem interaction={item.interaction} product={product} />
          )}
        </li>
      ))}
    </ol>
  );
}

function itemAt(item: Item): number {
  return item.type === "tool" ? item.at : item.interaction.at;
}

function InteractionItem({ interaction, product }: { interaction: Interaction; product: string | null | undefined }) {
  const content = interaction.version.content;
  if (content.kind === "reasoning") return <ReasoningItem interaction={interaction} />;
  if (content.kind === "context") return <ContextItem interaction={interaction} />;
  return <MessageItem interaction={interaction} product={product} />;
}

const ACTORS: Record<Actor, string> = {
  user: "你",
  assistant: "助手",
  tool: "工具",
  application: "应用",
  agent: "Agent",
};

function MessageItem({ interaction, product }: { interaction: Interaction; product: string | null | undefined }) {
  const content = interaction.version.content;
  const user = content.actor === "user";
  const label =
    content.actor === "assistant" ? productName(product) : content.actor ? ACTORS[content.actor] : "发出方未知";
  return (
    <div className="min-w-0">
      <div className="text-fg-3 mb-1 flex items-center gap-2 text-[11px]">
        <span className={cn(user && "text-fg-2 font-medium")}>{label}</span>
        {content.phase && (
          <span>
            {content.phase === "final_answer" ? "回答" : content.phase === "commentary" ? "过程" : content.phase}
          </span>
        )}
        <Detail interaction={interaction} />
      </div>
      <div
        className={cn(
          "text-[14px] leading-relaxed break-words whitespace-pre-wrap",
          user && "bg-panel rounded-xl px-3.5 py-2.5",
          (content.actor === "agent" || content.actor === "application" || !content.actor) &&
            "border-line-2 rounded-xl border border-dashed px-3.5 py-2.5",
          content.phase === "commentary" && "text-fg-2",
        )}
      >
        {content.parts.map((part, index) => (
          <Part key={index} content={content} part={part} />
        ))}
      </div>
    </div>
  );
}

/** 一个内容片段：文字直接显示，图片和只在原始记录中的内容按位置读取。 */
function Part({ content, part }: { content: InteractionContent; part: InteractionPart }) {
  if (part.type === "image") return <PartImage content={content} part={part} />;
  if (part.text !== null) return <span className="block">{inlineText(part)}</span>;
  return <RawText content={content} part={part} />;
}

function PartImage({ content, part }: { content: InteractionContent; part: InteractionPart }) {
  const file = useContentUrl(part.file);
  const raw = usePartValue(content, part, !part.file);
  const src = part.file
    ? file.data
    : typeof raw.value === "string"
      ? `data:${part.mediaType ?? "image/png"};base64,${raw.value}`
      : undefined;
  if (!src)
    return (
      <span className="text-fg-3 my-1 block text-xs">
        {raw.isPending || file.isPending ? "读取图片" : "图片读不到"}
      </span>
    );
  return <img src={src} alt="" className="border-line my-1.5 block max-h-72 max-w-full rounded-lg border" />;
}

const LIMIT = 20_000;

function RawText({ content, part }: { content: InteractionContent; part: InteractionPart }) {
  const raw = usePartValue(content, part, true);
  const [all, setAll] = useState(false);
  if (raw.isPending) return <span className="text-fg-3 text-xs">读取原始记录</span>;
  if (raw.error || raw.value === undefined) return <span className="text-fg-3 text-xs">原始记录读不到</span>;
  const text = partText(raw.value);
  return (
    <>
      {all ? text : text.slice(0, LIMIT)}
      {text.length > LIMIT && !all && (
        <button
          type="button"
          onClick={() => setAll(true)}
          className="text-fg-2 hover:text-fg mt-1 block cursor-pointer text-xs underline"
        >
          显示全部 {text.length.toLocaleString()} 个字符
        </button>
      )}
    </>
  );
}

/** 收起的一行，点开显示内容。 */
function Fold({
  icon,
  title,
  summary,
  status,
  interaction,
  children,
}: {
  icon: ReactNode;
  title: string;
  summary?: string | null;
  status?: ReactNode;
  interaction: Interaction;
  children: (open: boolean) => ReactNode;
}) {
  const [open, setOpen] = useState(false);
  return (
    <div className="min-w-0">
      <div className="flex items-center gap-1">
        <button
          type="button"
          aria-expanded={open}
          onClick={() => setOpen(!open)}
          className="hover:bg-accent-soft text-fg-2 flex min-w-0 flex-1 cursor-pointer items-center gap-2 rounded-lg px-2 py-1 text-left text-[13px]"
        >
          <ChevronRight
            aria-hidden="true"
            className={cn("size-3.5 shrink-0 transition-transform", open && "rotate-90")}
          />
          {icon}
          <span className="text-fg shrink-0 font-mono text-xs">{title}</span>
          {summary && <span className="text-fg-3 min-w-0 truncate font-mono text-xs">{summary}</span>}
          {status && <span className="ml-auto shrink-0 pl-2 text-xs">{status}</span>}
        </button>
        <Detail interaction={interaction} />
      </div>
      {open && <div className="mt-1 mb-2 ml-7 flex flex-col gap-2">{children(open)}</div>}
    </div>
  );
}

function Block({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <p className="text-fg-3 mb-1 text-[11px]">{label}</p>
      <pre className="bg-panel max-h-96 overflow-auto rounded-lg px-3 py-2 font-mono text-xs leading-relaxed break-words whitespace-pre-wrap">
        {children}
      </pre>
    </div>
  );
}

function ToolItem({ call, result }: { call: Interaction | null; result: Interaction | null }) {
  const head = (call ?? result)!;
  const callContent = call?.version.content;
  const resultContent = result?.version.content;
  const took = call && result ? (result.end ?? result.at) - call.at : null;
  const status = !result ? (
    <span className="text-fg-3">没有结果</span>
  ) : resultContent?.isError ? (
    <span className="text-danger">失败</span>
  ) : resultContent?.exitCode !== null && resultContent?.exitCode !== undefined && resultContent.exitCode !== 0 ? (
    <span className="text-danger">退出码 {resultContent.exitCode}</span>
  ) : took !== null && took >= 1000 ? (
    <span className="text-fg-3 tabular-nums">{duration(took)}</span>
  ) : null;
  return (
    <Fold
      icon={<Wrench aria-hidden="true" className="size-3.5 shrink-0" />}
      title={toolLabel(head.version.content)}
      summary={argumentSummary(callContent)}
      status={status}
      interaction={head}
    >
      {() => (
        <>
          {callContent?.parts.map((part, index) => (
            <Block key={`c${index}`} label={part.type === "json" ? "参数" : "调用"}>
              <Part content={callContent} part={part} />
            </Block>
          ))}
          {resultContent?.parts.map((part, index) => (
            <Block key={`r${index}`} label="结果">
              <Part content={resultContent} part={part} />
            </Block>
          ))}
          {resultContent && !resultContent.parts.length && <p className="text-fg-3 text-xs">结果没有内容</p>}
        </>
      )}
    </Fold>
  );
}

function ReasoningItem({ interaction }: { interaction: Interaction }) {
  const content = interaction.version.content;
  // Claude Code 的推理可以只有签名、文字为空，这时只有不可读的内容。
  const readable = content.parts.filter((part) => part.type !== "opaque" && part.text !== "");
  const first = readable.find((part) => part.text)?.text?.split("\n")[0] ?? null;
  return (
    <Fold
      icon={<Brain aria-hidden="true" className="size-3.5 shrink-0" />}
      title={readable.some((part) => part.type === "summary") ? "推理摘要" : "推理"}
      summary={first ?? (readable.length ? null : "只有加密的内容")}
      interaction={interaction}
    >
      {() =>
        readable.length ? (
          readable.map((part, index) => (
            <p key={index} className="text-fg-2 text-[13px] leading-relaxed whitespace-pre-wrap">
              <Part content={content} part={part} />
            </p>
          ))
        ) : (
          <p className="text-fg-3 text-xs">来源只提供了加密的推理</p>
        )
      }
    </Fold>
  );
}

function ContextItem({ interaction }: { interaction: Interaction }) {
  const content = interaction.version.content;
  return (
    <Fold
      icon={<FileText aria-hidden="true" className="size-3.5 shrink-0" />}
      title="上下文"
      summary={contextLabel(content)}
      interaction={interaction}
    >
      {() =>
        content.parts.map((part, index) => (
          <Block key={index} label={part.type === "json" ? "结构" : "内容"}>
            <Part content={content} part={part} />
          </Block>
        ))
      }
    </Fold>
  );
}

/** 推出这次交互的面板。 */
function Detail({ interaction }: { interaction: Interaction }) {
  const stack = useStack();
  return (
    <button
      type="button"
      aria-label="详情"
      title="详情"
      onClick={() => stack.open(interaction.version.entityId, -1)}
      className="text-fg-3 hover:text-fg cursor-pointer rounded-md p-0.5 opacity-0 group-hover:opacity-100 focus-visible:opacity-100"
    >
      <PanelRight aria-hidden="true" className="size-3.5" />
    </button>
  );
}
