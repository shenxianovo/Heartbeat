<script setup lang="ts">
import { computed, ref, type HTMLAttributes } from 'vue'
import { CalendarDate, getLocalTimeZone, today, type DateValue } from '@internationalized/date'
import { CalendarIcon } from 'lucide-vue-next'
import { Calendar } from '@/components/ui/calendar'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

const props = defineProps<{ modelValue: string; class?: HTMLAttributes['class'] }>()
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()
const open = ref(false)
const todayValue = today(getLocalTimeZone())

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
    open.value = false
  },
})

function selectToday() {
  dateValue.value = todayValue
}
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger
      :class="cn('glass-control px-3 py-1.5 text-sm text-foreground', props.class)"
    >
      <CalendarIcon :size="15" class="text-muted-foreground" />
      <span class="font-mono">{{ modelValue }}</span>
    </PopoverTrigger>
    <PopoverContent class="w-auto p-0" align="end">
      <Calendar
        v-model="dateValue"
        :max-value="todayValue"
        :weekday-format="'short'"
        locale="zh-CN"
      />
      <div class="border-t border-border/60 p-2">
        <Button variant="ghost" size="sm" class="w-full justify-start text-primary" @click="selectToday">
          今天
        </Button>
      </div>
    </PopoverContent>
  </Popover>
</template>
