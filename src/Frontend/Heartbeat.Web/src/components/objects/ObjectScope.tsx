"use client";

import { createContext, useContext, type PropsWithChildren } from "react";
import type { ObjectScope } from "@/api/types";
import type { DateRange } from "@/lib/dates";

const Context = createContext<ObjectScope>({});
const RangeContext = createContext<DateRange | undefined>(undefined);
export function ObjectScopeProvider({
  scope,
  range,
  children,
}: PropsWithChildren<{ scope: ObjectScope; range?: DateRange }>) {
  return (
    <Context.Provider value={scope}>
      <RangeContext.Provider value={range}>{children}</RangeContext.Provider>
    </Context.Provider>
  );
}
export function useObjectScope() {
  return useContext(Context);
}
export function useObjectRange() {
  return useContext(RangeContext);
}
