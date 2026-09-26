"use client";

import { Component, createElement, type PropsWithChildren } from "react";

import { FallbackJsonRenderer } from "@/components/records/renderers/FallbackJsonRenderer";
import { findRecordRenderer } from "@/components/records/renderers/registry";

interface RecordValueProps {
  type: string;
  version: number;
  value: unknown;
}

interface BoundaryProps extends PropsWithChildren {
  value: unknown;
}

interface BoundaryState {
  failed: boolean;
}

class RendererBoundary extends Component<BoundaryProps, BoundaryState> {
  state: BoundaryState = { failed: false };

  static getDerivedStateFromError(): BoundaryState {
    return { failed: true };
  }

  render() {
    if (this.state.failed) {
      return <FallbackJsonRenderer value={this.props.value} error="记录解析失败" />;
    }
    return this.props.children;
  }
}

export function RecordValue({ type, version, value }: RecordValueProps) {
  const Renderer = findRecordRenderer(type, version);
  if (!Renderer) return <FallbackJsonRenderer value={value} />;

  return <RendererBoundary value={value}>{createElement(Renderer, { value })}</RendererBoundary>;
}
