<script setup lang="ts">
import { computed, type HTMLAttributes } from 'vue'
import { CalendarDate, type DateValue } from '@internationalized/date'
import { CalendarIcon } from 'lucide-vue-next'
import { Calendar } from '@/components/ui/calendar'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { cn } from '@/lib/utils'

const props = defineProps<{ modelValue: string; class?: HTMLAttributes['class'] }>()
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()

/** "yyyy-MM-dd" <-> DateValue 双向桥接 */
const dateValue = computed<DateValue | undefined>({
  get: () => {
    const [y, m, d] = props.modelValue.split('-').map(Number)
    if (!y || !m || !d) return undefined
    return new CalendarDate(y, m, d)
  },
  set: (v) => {
    if (!v) return
    const s = `${v.year}-${String(v.month).padStart(2, '0')}-${String(v.day).padStart(2, '0')}`
    emit('update:modelValue', s)
  },
})
</script>

<template>
  <Popover>
    <PopoverTrigger
      :class="cn('glass-control px-3 py-1.5 text-sm text-foreground', props.class)"
    >
      <CalendarIcon :size="15" class="text-muted-foreground" />
      <span class="font-mono">{{ modelValue }}</span>
    </PopoverTrigger>
    <PopoverContent class="w-auto p-0" align="end">
      <Calendar v-model="dateValue" :weekday-format="'short'" locale="zh-CN" />
    </PopoverContent>
  </Popover>
</template>
