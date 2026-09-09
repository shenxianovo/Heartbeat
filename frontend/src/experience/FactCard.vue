<script setup lang="ts">
import { computed } from 'vue'
import { presentFact, type ExperienceSegment } from './factViews'
const props = defineProps<{ fact: ExperienceSegment; selected?: boolean; timeZone: string }>()
defineEmits<{ select: [fact: ExperienceSegment]; focus: [fact: ExperienceSegment] }>()
const view = computed(() => presentFact(props.fact))
const crossesDate = computed(() => new Date(props.fact.startTime).toLocaleDateString('en-CA', { timeZone: props.timeZone }) !==
  new Date(props.fact.endTime).toLocaleDateString('en-CA', { timeZone: props.timeZone }))
const format = (value: string) => (crossesDate.value ? new Date(value).toLocaleDateString('zh-CN', {
  month: 'numeric', day: 'numeric', timeZone: props.timeZone,
}) + ' ' : '') + new Date(value).toLocaleTimeString('zh-CN', {
  hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit', timeZone: props.timeZone,
})
</script>

<template>
  <article class="fact-card" :class="{ selected }" :style="{ '--fact-color': view.color }">
    <button class="fact-main" @click="$emit('select', fact)">
      <span class="fact-time">{{ format(fact.startTime) }} — {{ format(fact.endTime) }}</span>
      <strong>{{ view.title }}</strong>
      <span class="fact-subtitle">{{ view.subtitle }}</span>
    </button>
    <div class="fact-actions">
      <button @click="$emit('focus', fact)">在时间轴聚焦</button>
      <details>
        <summary>记录详情</summary>
        <dl>
          <template v-for="field in view.fields" :key="field.label">
            <dt>{{ field.label }}</dt>
            <dd><a v-if="field.href" :href="field.href" target="_blank" rel="noopener noreferrer">{{ field.value }}</a><span v-else>{{ field.value }}</span></dd>
          </template>
          <dt>原始时间</dt><dd>{{ fact.startTime }} → {{ fact.endTime }}</dd>
          <dt>Fact / Revision</dt><dd>{{ fact.factId }} / {{ fact.revision }}</dd>
          <dt>Stream</dt><dd>{{ fact.streamId }}</dd>
        </dl>
        <details class="payload"><summary>Payload JSON</summary><pre>{{ JSON.stringify(fact.payload, null, 2) }}</pre></details>
      </details>
    </div>
  </article>
</template>

<style scoped>
.fact-card { border: 1px solid var(--glass-border); border-left: 3px solid var(--fact-color); border-radius: 12px; background: var(--glass-bg); backdrop-filter: blur(var(--glass-blur)); overflow: hidden; }
.fact-card.selected { outline: 2px solid var(--fact-color); outline-offset: 2px; }
.fact-main { display: grid; gap: 5px; width: 100%; padding: 16px 18px 10px; text-align: left; cursor: pointer; }
.fact-main strong { font-size: .95rem; font-weight: 550; overflow-wrap: anywhere; }
.fact-time { font-variant-numeric: tabular-nums; color: var(--muted-foreground); font-size: .75rem; }
.fact-subtitle { font-size: .8rem; color: var(--fact-color); }
.fact-actions { padding: 0 18px 12px; font-size: .75rem; color: var(--muted-foreground); }
.fact-actions > button { float: right; cursor: pointer; color: var(--primary); }
summary { cursor: pointer; width: fit-content; padding: 3px 0; }
dl { clear: both; display: grid; grid-template-columns: auto 1fr; gap: 6px 15px; padding-top: 10px; }
dd { overflow-wrap: anywhere; min-width: 0; } a { color: var(--primary); text-decoration: underline; }
.payload { margin-top: 12px; } pre { padding: 12px; border-radius: 8px; background: var(--muted); overflow: auto; max-height: 300px; white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
