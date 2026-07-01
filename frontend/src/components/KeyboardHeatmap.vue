<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import { Card } from '@/components/ui/card'

const props = defineProps<{
  keyFrequency: { code: number; count: number }[]
}>()

// 主键区布局：每个键 { code: VK码, label: 显示名, w?: 相对宽度(默认1) }
// VK 码参考 Windows Virtual-Key Codes。
type KeyDef = { code: number; label: string; w?: number }

const ROWS: KeyDef[][] = [
  [
    { code: 192, label: '`' }, { code: 49, label: '1' }, { code: 50, label: '2' },
    { code: 51, label: '3' }, { code: 52, label: '4' }, { code: 53, label: '5' },
    { code: 54, label: '6' }, { code: 55, label: '7' }, { code: 56, label: '8' },
    { code: 57, label: '9' }, { code: 48, label: '0' }, { code: 189, label: '-' },
    { code: 187, label: '=' }, { code: 8, label: 'Bksp', w: 2 },
  ],
  [
    { code: 9, label: 'Tab', w: 1.5 }, { code: 81, label: 'Q' }, { code: 87, label: 'W' },
    { code: 69, label: 'E' }, { code: 82, label: 'R' }, { code: 84, label: 'T' },
    { code: 89, label: 'Y' }, { code: 85, label: 'U' }, { code: 73, label: 'I' },
    { code: 79, label: 'O' }, { code: 80, label: 'P' }, { code: 219, label: '[' },
    { code: 221, label: ']' }, { code: 220, label: '\\', w: 1.5 },
  ],
  [
    { code: 20, label: 'Caps', w: 1.75 }, { code: 65, label: 'A' }, { code: 83, label: 'S' },
    { code: 68, label: 'D' }, { code: 70, label: 'F' }, { code: 71, label: 'G' },
    { code: 72, label: 'H' }, { code: 74, label: 'J' }, { code: 75, label: 'K' },
    { code: 76, label: 'L' }, { code: 186, label: ';' }, { code: 222, label: "'" },
    { code: 13, label: 'Enter', w: 2.25 },
  ],
  [
    { code: 160, label: 'LShift', w: 2.25 }, { code: 90, label: 'Z' }, { code: 88, label: 'X' },
    { code: 67, label: 'C' }, { code: 86, label: 'V' }, { code: 66, label: 'B' },
    { code: 78, label: 'N' }, { code: 77, label: 'M' }, { code: 188, label: ',' },
    { code: 190, label: '.' }, { code: 191, label: '/' }, { code: 161, label: 'RShift', w: 2.75 },
  ],
  [
    { code: 162, label: 'LCtrl', w: 1.25 }, { code: 91, label: 'Win', w: 1.25 },
    { code: 164, label: 'LAlt', w: 1.25 }, { code: 32, label: 'Space', w: 6.25 },
    { code: 165, label: 'RAlt', w: 1.25 }, { code: 93, label: 'Menu', w: 1.25 },
    { code: 163, label: 'RCtrl', w: 1.25 },
  ],
]

const countByCode = computed(() => {
  const m = new Map<number, number>()
  for (const k of props.keyFrequency) m.set(k.code, k.count)
  return m
})

const maxCount = computed(() => {
  let max = 0
  for (const k of props.keyFrequency) if (k.count > max) max = k.count
  return max
})

const totalCount = computed(() => props.keyFrequency.reduce((s, k) => s + k.count, 0))

function countFor(code: number): number {
  return countByCode.value.get(code) ?? 0
}

// 0 → 透明，按比例插值到 primary 色的不透明度
function intensityStyle(code: number): Record<string, string> {
  const c = countFor(code)
  if (c === 0 || maxCount.value === 0) return {}
  const t = c / maxCount.value
  // 用对数缓和，避免高频键一枝独秀把其它全压成透明
  const alpha = 0.12 + 0.88 * Math.sqrt(t)
  return { backgroundColor: `color-mix(in srgb, var(--primary) ${Math.round(alpha * 100)}%, transparent)` }
}

const hovered = ref<{ label: string; code: number; count: number } | null>(null)

// ── 趣味换算 ──
// 每条都用"≈"，基准标注在注释里，避免显得精确。
type FunFact = { text: string }

const funFacts = computed<FunFact[]>(() => {
  const n = totalCount.value
  if (n === 0) return []

  const facts: FunFact[] = []

  // 《三体》三部曲约 88 万字
  const santi = n / 880_000
  if (santi >= 0.01) {
    facts.push({
      text: `相当于敲出了 ${santi >= 1 ? santi.toFixed(1) : santi.toFixed(2)} 部《三体》三部曲`,
    })
  }

  // 代码行数：每行约 30 字符
  const lines = Math.round(n / 30)
  facts.push({ text: `约等于写了 ${lines.toLocaleString()} 行代码` })

  // 卡路里：1h 打字约 30 cal，60 WPM × 6 字符/词 ≈ 21600 次/h
  //   → 每次 ≈ 0.00000139 kCal（小到可忽略，正是笑点）
  const kcal = n * 0.00000139
  facts.push({ text: `手指燃烧了约 ${kcal.toFixed(4)} kCal` })

  // 手指键程：每次按下约 8mm
  const meters = n * 0.008
  if (meters >= 1000) {
    facts.push({ text: `手指累计移动约 ${(meters / 1000).toFixed(2)} 公里` })
  } else {
    facts.push({ text: `手指累计移动约 ${Math.round(meters)} 米` })
  }

  // 莎士比亚全集约 88.4 万单词 ≈ 按 5 字符/词算 442 万字符
  const shakespeare = n / 4_420_000
  if (shakespeare >= 0.01) {
    facts.push({
      text: `约是莎士比亚全集的 ${(shakespeare * 100).toFixed(0)}%`,
    })
  }

  return facts
})

const factIndex = ref(0)
const currentFact = computed(() => funFacts.value[factIndex.value] ?? null)

function nextFact() {
  if (funFacts.value.length === 0) return
  factIndex.value = (factIndex.value + 1) % funFacts.value.length
}

let rotateTimer: ReturnType<typeof setInterval> | undefined
onMounted(() => {
  rotateTimer = setInterval(nextFact, 4000)
})
onUnmounted(() => clearInterval(rotateTimer))

// 数据变化时若当前下标越界则归零
watch(funFacts, (facts) => {
  if (factIndex.value >= facts.length) factIndex.value = 0
})
</script>

<template>
  <Card class="mb-6 gap-3 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
    <div class="flex flex-col gap-3 px-5">
      <div class="flex items-baseline justify-between">
        <h2 class="text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">键盘热力图</h2>
        <span class="font-mono text-[0.75rem] text-muted-foreground">
          <template v-if="hovered">{{ hovered.label }} · {{ hovered.count.toLocaleString() }} 次</template>
          <template v-else>共 {{ totalCount.toLocaleString() }} 次按键</template>
        </span>
      </div>

      <div v-if="totalCount > 0" class="flex flex-col gap-1.5 overflow-x-auto">
        <div
          v-for="(row, ri) in ROWS"
          :key="ri"
          class="flex gap-1.5"
        >
          <div
            v-for="(key, ki) in row"
            :key="ki"
            class="relative flex h-9 min-w-0 shrink-0 items-center justify-center rounded-md border border-border/50 bg-secondary/40 text-[0.7rem] font-medium text-foreground/80 transition-colors"
            :style="{ flexGrow: key.w ?? 1, flexBasis: `${(key.w ?? 1) * 2.2}rem`, ...intensityStyle(key.code) }"
            @mouseenter="hovered = { label: key.label, code: key.code, count: countFor(key.code) }"
            @mouseleave="hovered = null"
          >
            {{ key.label }}
          </div>
        </div>
      </div>

      <button
        v-if="currentFact"
        type="button"
        class="group flex items-center gap-2 self-start rounded-full border border-border/50 bg-secondary/30 px-3 py-1.5 text-left text-[0.8rem] text-foreground/80 transition-colors hover:bg-accent"
        title="点击切换"
        @click="nextFact"
      >
        <span>{{ currentFact.text }}</span>
        <span class="font-mono text-[0.65rem] text-muted-foreground opacity-0 transition-opacity group-hover:opacity-100">↻</span>
      </button>

      <div v-else-if="totalCount === 0" class="py-8 text-center text-[0.9rem] text-muted-foreground">暂无数据</div>
    </div>
  </Card>
</template>
