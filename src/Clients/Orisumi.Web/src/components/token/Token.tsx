import type { ComponentType } from "react";
import { BookOpen, Box, Layers } from "lucide-react";
import type { Version } from "@/api/types";
import { useDescribe, useEntityInfo, type EntityInfo } from "@/hooks/useEntityInfo";
import { useThingImage } from "@/hooks/useIcons";
import type { Role } from "@/lib/roles";
import { UNREADABLE } from "@/lib/observers";
import { cn } from "@/lib/utils";
import { useDepth, useHighlight, useStack } from "./context";
import { typeIcon } from "./icons";

const MARK_SIZES = {
  sm: "size-4 rounded-[4px] text-[9px]",
  md: "size-5 rounded-[5px] text-[10px]",
  lg: "size-10 rounded-[10px] text-base",
  xl: "size-14 rounded-2xl text-xl",
};

/**
 * 记号的形状按角色：事物是带类型图标的色块，经历是一段条，来源是圆点，依据是叠起的卡，定义是虚线框。
 * framed 时其他角色的记号也放进同样大小的底块，用在标题这类单独出现、需要分量的位置。
 */
export function Mark({
  role,
  color,
  icon: Icon,
  image,
  size = "md",
  framed,
  className,
}: {
  role: Role | null;
  color: string | null;
  /** 类型的记号用图标代替首字母。 */
  icon?: ComponentType<{ className?: string }> | undefined;
  /** 事物自己的图片，例如应用的图标；有图片时代替带类型图标的色块。 */
  image?: string | undefined;
  size?: keyof typeof MARK_SIZES;
  framed?: boolean | undefined;
  className?: string | undefined;
}) {
  const base = cn("grid shrink-0 place-items-center font-semibold", MARK_SIZES[size], className);
  if (framed && role !== "thing")
    return (
      <span aria-hidden="true" className={cn(base, "bg-panel")}>
        <Mark role={role} color={color} icon={Icon} size={size} className="size-[58%]" />
      </span>
    );
  if (role === "thing" && image)
    return (
      <span aria-hidden="true" className={cn(base, "overflow-hidden bg-transparent")}>
        <img src={image} alt="" draggable={false} className="size-full object-contain" />
      </span>
    );
  if (role === "thing")
    return (
      <span
        aria-hidden="true"
        className={cn(base, "bg-panel text-fg-2")}
        style={color ? { background: `color-mix(in srgb, ${color} 16%, transparent)`, color } : undefined}
      >
        {Icon ? <Icon className="size-[62%]" /> : <Box className="size-[62%]" />}
      </span>
    );
  if (role === "experience")
    return (
      <span aria-hidden="true" className={cn(base, "bg-transparent")}>
        <span className="bg-fg-3 block h-[30%] w-[75%] rounded-full" />
      </span>
    );
  if (role === "source")
    return (
      <span aria-hidden="true" className={cn(base, "text-fg-2 !rounded-full border-[1.5px] border-current")}>
        <span className="block size-[30%] rounded-full bg-current" />
      </span>
    );
  if (role === "evidence")
    return (
      <span aria-hidden="true" className={cn(base, "text-fg-2")}>
        <Layers className="size-[85%]" />
      </span>
    );
  if (role === "definition")
    return (
      <span aria-hidden="true" className={cn(base, "border-line-3 text-fg-2 border border-dashed")}>
        <BookOpen className="size-[60%]" />
      </span>
    );
  return <span aria-hidden="true" className={cn(base, "bg-panel")} />;
}

/**
 * 一个实体的记号。同一实体在任何地方出现都是这个样子；悬停时页面上它的其他记号一起亮起，
 * 点击在右边推出它的面板。
 */
export function Token({
  versionId,
  version,
  size = "md",
  detail,
  className,
}: {
  versionId?: string | null;
  version?: Version;
  size?: "sm" | "md";
  /** 名字后面的补充，例如安装路径里不同的那一段。 */
  detail?: string | null | undefined;
  className?: string;
}) {
  const byId = useEntityInfo(version ? null : versionId);
  const given = useDescribe(version);
  const { info, pending, unreadable } = version ? given : byId;

  if (unreadable)
    return (
      <span className={cn(chip(size), "text-fg-3 border-line-2 border border-dashed", className)}>
        <span className="font-mono text-[11px]">{versionId?.slice(-8)}</span> {UNREADABLE}
      </span>
    );
  if (pending || !info)
    return (
      <span className={cn(chip(size), "text-fg-3", className)}>
        <Mark role={null} color={null} size={size} />
        <span className="bg-panel h-2.5 w-14 rounded" />
      </span>
    );
  return <LiveToken info={info} size={size} detail={detail} className={className} />;
}

/** 事物的记号用它所属类型的图标。 */
export function iconOf(info: EntityInfo) {
  return info.role === "thing" ? typeIcon(info.schema?.content.resourceName) : undefined;
}

/** 一个实体的记号：角色的形状，事物有自己的图标时用它的图标。 */
export function InfoMark({
  info,
  size = "md",
  framed,
  className,
}: {
  info: EntityInfo;
  size?: keyof typeof MARK_SIZES;
  framed?: boolean | undefined;
  className?: string | undefined;
}) {
  const thing = info.role === "thing";
  const image = useThingImage(thing ? info.version : null, thing ? info.schema?.content.resourceName : null);
  return (
    <Mark
      role={info.role}
      color={info.color}
      icon={iconOf(info)}
      image={image}
      size={size}
      framed={framed}
      className={className}
    />
  );
}

function chip(size: "sm" | "md") {
  return cn(
    "inline-flex max-w-full min-w-0 items-center rounded-md align-middle",
    size === "sm" ? "gap-1 px-1 py-px text-xs" : "gap-1.5 px-1.5 py-0.5 text-[13px]",
  );
}

function LiveToken({
  info,
  size,
  detail,
  className,
}: {
  info: EntityInfo;
  size: "sm" | "md";
  detail?: string | null | undefined;
  className?: string | undefined;
}) {
  const highlight = useHighlight();
  const depth = useDepth();
  const stack = useStack();
  const entityId = info.version.entityId;
  const lit = highlight.active === entityId;
  const open = stack.ids.includes(entityId);
  return (
    <button
      type="button"
      data-entity={entityId}
      title={`${info.roleName} · ${info.typeName}`}
      aria-label={`${info.typeName} ${info.title}`}
      onPointerEnter={() => highlight.set(entityId)}
      onPointerLeave={() => highlight.set(null)}
      onFocus={() => highlight.set(entityId)}
      onBlur={() => highlight.set(null)}
      onClick={(event) => {
        event.stopPropagation();
        stack.open(entityId, depth);
      }}
      className={cn(
        chip(size),
        "cursor-pointer text-left transition-[background-color,box-shadow] duration-150 ease-out",
        lit ? "bg-accent-soft ring-line-3 ring-1" : open ? "bg-panel" : "hover:bg-panel",
        className,
      )}
    >
      <InfoMark info={info} size={size} />
      <span className="min-w-0 truncate">
        {info.title}
        {detail && <span className="text-fg-3 font-mono text-[11px]"> {detail}</span>}
      </span>
    </button>
  );
}
