import { describe, it, expect } from 'vitest'
import { buildTree, flattenTree } from './strandTreeUtils'
import type { IStrandResponse } from '../api/client'

function strand(id: string, parentStrandId?: string, opts?: Partial<IStrandResponse>): IStrandResponse {
  return { id, parentStrandId, name: id, path: [id], version: 1, members: [], ...opts }
}

describe('buildTree', () => {
  it('builds roots from flat list', () => {
    const strands = [strand('a'), strand('b')]
    const tree = buildTree(strands)
    expect(tree).toHaveLength(2)
    expect(tree[0].strand.id).toBe('a')
    expect(tree[0].depth).toBe(0)
  })

  it('nests children under parents', () => {
    const strands = [strand('root'), strand('child', 'root')]
    const tree = buildTree(strands)
    expect(tree).toHaveLength(1)
    expect(tree[0].children).toHaveLength(1)
    expect(tree[0].children[0].strand.id).toBe('child')
    expect(tree[0].children[0].depth).toBe(1)
  })

  it('handles multi-level nesting', () => {
    const strands = [strand('a'), strand('b', 'a'), strand('c', 'b')]
    const tree = buildTree(strands)
    expect(tree).toHaveLength(1)
    expect(tree[0].children[0].children[0].strand.id).toBe('c')
    expect(tree[0].children[0].children[0].depth).toBe(2)
  })

  it('treats orphaned parents as roots', () => {
    const strands = [strand('child', 'nonexistent')]
    const tree = buildTree(strands)
    expect(tree).toHaveLength(1)
    expect(tree[0].strand.id).toBe('child')
  })

  it('returns empty array for empty input', () => {
    expect(buildTree([])).toEqual([])
  })
})

describe('flattenTree', () => {
  it('flattens depth-first', () => {
    const strands = [strand('a'), strand('b', 'a'), strand('c')]
    const tree = buildTree(strands)
    const flat = flattenTree(tree)
    expect(flat.map(n => n.strand.id)).toEqual(['a', 'b', 'c'])
  })

  it('preserves sibling order', () => {
    const strands = [strand('root'), strand('x', 'root'), strand('y', 'root')]
    const tree = buildTree(strands)
    const flat = flattenTree(tree)
    expect(flat.map(n => n.strand.id)).toEqual(['root', 'x', 'y'])
  })
})
