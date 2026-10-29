import { AppWindow, Box, type LucideIcon } from "lucide-react";

/** 事物类型的图标。接入新的事物类型时在这里加一行，键是 Schema 的 resourceName；没有登记的用方块。 */
const BY_RESOURCE_NAME: Record<string, LucideIcon> = {
  "local-applications": AppWindow,
};

export function typeIcon(resourceName: string | null | undefined): LucideIcon {
  return (resourceName && BY_RESOURCE_NAME[resourceName]) || Box;
}
