import type { IStrandResponse } from '../api/client'

export interface StrandTreeNode {
  strand: IStrandResponse
  children: StrandTreeNode[]
  depth: number
}

export function buildTree(strands: IStrandResponse[]): StrandTreeNode[] {
  const map = new Map<string, StrandTreeNode>()
  for (const s of strands) {
    map.set(s.id!, { strand: s, children: [], depth: 0 })
  }
  const roots: StrandTreeNode[] = []
  for (const node of map.values()) {
    const parentId = node.strand.parentStrandId
    if (parentId && map.has(parentId)) {
      map.get(parentId)!.children.push(node)
    } else {
      roots.push(node)
    }
  }
  function setDepth(nodes: StrandTreeNode[], depth: number) {
    for (const n of nodes) {
      n.depth = depth
      setDepth(n.children, depth + 1)
    }
  }
  setDepth(roots, 0)
  return roots
}

export function flattenTree(roots: StrandTreeNode[]): StrandTreeNode[] {
  const result: StrandTreeNode[] = []
  function walk(nodes: StrandTreeNode[]) {
    for (const n of nodes) {
      result.push(n)
      walk(n.children)
    }
  }
  walk(roots)
  return result
}
