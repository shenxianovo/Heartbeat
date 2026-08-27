<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { getIconUrl, submitManagedSubjectAuthorization, type ManagedSubjectStatus } from '../api/index'
import { getAppLabel, isAwayApp } from '../appLabels'
import type { DevicePresence } from '../composables/useDeviceStatus'
import { Card } from '@/components/ui/card'

const props = defineProps<{
  username: string
  isToday: boolean
  isAlive: boolean
  currentApp: string | null
  currentAppId: number | null
  currentAppKey: string | null
  presences: DevicePresence[]
  isAllDevices: boolean
  managedSubjects?: ManagedSubjectStatus[]
}>()
const emit = defineEmits<{ authorizationSubmitted: [] }>()

const onlinePresences = computed(() => props.presences.filter(p => p.isOnline))
const singleDeviceName = computed(() => onlinePresences.value[0]?.deviceName ?? '')

// 多台在线设备时逐行展示：双机并发时"当前应用"本来就不是一个值,不合成。
const showPerDevice = computed(() => props.isAllDevices && onlinePresences.value.length > 1)
const accountSubjects = computed(() => props.managedSubjects ?? [])
const activeAuthorization = ref<string | null>(null)
const authorizationValues = reactive<Record<string, string>>({})
const authorizationError = ref('')
const submitting = ref(false)

function openAuthorization(subject: ManagedSubjectStatus) {
  activeAuthorization.value = subject.subjectId
  authorizationError.value = ''
  for (const key of Object.keys(authorizationValues)) delete authorizationValues[key]
  for (const field of subject.authorization?.fields ?? []) authorizationValues[field.name] = ''
}

function cancelAuthorization() {
  activeAuthorization.value = null
  authorizationError.value = ''
  for (const key of Object.keys(authorizationValues)) delete authorizationValues[key]
}

async function submitAuthorization(subject: ManagedSubjectStatus) {
  if (!subject.authorization) return
  submitting.value = true
  authorizationError.value = ''
  try {
    if (!subject.collectorInstanceId) throw new Error('Collector Instance is not initialized.')
    await submitManagedSubjectAuthorization(
      subject.collectorInstanceId,
      subject.authorization.interactionId,
      { ...authorizationValues },
    )
    activeAuthorization.value = null
    for (const key of Object.keys(authorizationValues)) delete authorizationValues[key]
    emit('authorizationSubmitted')
  } catch {
    authorizationError.value = '提交失败，请确认信息后重试'
  } finally {
    submitting.value = false
  }
}

function accountState(subject: ManagedSubjectStatus): string {
  if (subject.currentActivity?.title) return subject.currentActivity.title
  if (subject.authorization) return '需要登录'
  if (subject.phase === 'Ready') return '已登录，等待状态'
  if (subject.phase === 'Failed') return '采集器异常'
  return '正在连接'
}
</script>

<template>
  <Card v-if="isToday && ((isAlive && onlinePresences.length > 0) || accountSubjects.length > 0)" class="mb-6 gap-3 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
    <div class="flex flex-col gap-3 px-5">
      <h2 class="text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">当前使用</h2>

      <!-- 聚合视图多设备：per-device 行,每台各说自己的前台应用 -->
      <template v-if="isAlive && showPerDevice">
        <div
          v-for="p in onlinePresences"
          :key="p.deviceId"
          class="flex items-center gap-3 border-b border-border/40 py-1.5 last:border-0"
        >
          <span class="status-dot" :class="{ alive: p.isOnline }"></span>
          <img
            v-if="p.isOnline && p.currentAppId && !isAwayApp(p.currentAppKey, p.currentApp)"
            :src="getIconUrl(username, p.currentAppId)"
            class="h-6 w-6 shrink-0 object-contain"
            @error="($event.target as HTMLImageElement).style.display = 'none'"
          />
          <div class="flex min-w-0 flex-col gap-0.5">
            <span
              class="truncate text-[1rem]"
              :class="p.isOnline && p.currentApp && !isAwayApp(p.currentAppKey, p.currentApp)
                ? 'font-semibold'
                : 'font-normal text-muted-foreground'"
            >
              {{ isAwayApp(p.currentAppKey, p.currentApp) ? '离开中' : (p.currentApp ?? '无前台应用') }}
            </span>
            <span v-if="p.isOnline && p.currentApp && getAppLabel(p.currentAppKey ?? p.currentApp)" class="text-[0.7rem] text-muted-foreground">
              {{ getAppLabel(p.currentAppKey ?? p.currentApp) }}
            </span>
            <span class="truncate text-[0.75rem] text-muted-foreground">
              {{ p.deviceName }}
            </span>
          </div>
        </div>
      </template>

      <!-- 在线但人离开（心跳照实上报 __away__，ADR-021） -->
      <div v-else-if="isAlive && isAwayApp(currentAppKey, currentApp)" class="flex items-center gap-3 py-1">
        <span class="status-dot alive"></span>
        <div class="flex min-w-0 flex-col gap-0.5">
          <span class="text-[1.1rem] font-normal text-muted-foreground">离开中</span>
          <span class="truncate text-[0.75rem] text-muted-foreground">{{ singleDeviceName }}</span>
        </div>
      </div>

      <!-- 在线 + 有前台应用 -->
      <div v-else-if="isAlive && currentApp" class="flex items-center gap-3 py-1">
        <span class="status-dot alive"></span>
        <img
          v-if="currentAppId"
          :src="getIconUrl(username, currentAppId)"
          class="h-7 w-7 shrink-0 object-contain"
          @error="($event.target as HTMLImageElement).style.display = 'none'"
        />
        <div class="flex flex-col gap-0.5">
          <span class="text-[1.1rem] font-semibold">{{ currentApp }}</span>
          <span v-if="getAppLabel(currentAppKey ?? currentApp)" class="text-[0.8rem] text-muted-foreground">
            {{ getAppLabel(currentAppKey ?? currentApp) }}
          </span>
          <span class="truncate text-[0.75rem] text-muted-foreground">{{ singleDeviceName }}</span>
        </div>
      </div>

      <!-- 在线但无前台应用 -->
      <div v-else-if="isAlive" class="flex items-center gap-3 py-1">
        <span class="status-dot alive"></span>
        <div class="flex min-w-0 flex-col gap-0.5">
          <span class="text-[1.1rem] font-normal text-muted-foreground">无前台应用</span>
          <span class="truncate text-[0.75rem] text-muted-foreground">{{ singleDeviceName }}</span>
        </div>
      </div>

      <div
        v-for="subject in accountSubjects"
        :key="subject.subjectId"
        class="border-t border-border/40 pt-3"
      >
        <div class="flex items-center gap-3">
          <span class="status-dot" :class="{ alive: subject.phase === 'Ready' }"></span>
          <div class="min-w-0 flex-1">
            <div class="truncate text-[1rem] font-semibold">{{ accountState(subject) }}</div>
            <div class="truncate text-[0.75rem] text-muted-foreground">{{ subject.subjectName }}</div>
          </div>
          <button
            v-if="subject.authorization"
            class="glass-control shrink-0 px-3 py-1.5 text-[0.8rem] font-medium text-primary"
            @click="openAuthorization(subject)"
          >登录</button>
        </div>

        <form
          v-if="activeAuthorization === subject.subjectId && subject.authorization"
          class="mt-3 flex flex-col gap-2 rounded-lg border border-border/50 bg-background/30 p-3"
          @submit.prevent="submitAuthorization(subject)"
        >
          <div class="text-sm font-semibold">{{ subject.authorization.title }}</div>
          <div v-if="subject.authorization.message" class="text-xs text-muted-foreground">
            {{ subject.authorization.message }}
          </div>
          <label v-for="field in subject.authorization.fields" :key="field.name" class="flex flex-col gap-1 text-xs text-muted-foreground">
            {{ field.label }}
            <input
              v-model="authorizationValues[field.name]"
              :type="field.isSecret ? 'password' : 'text'"
              :inputmode="field.inputMode ?? undefined"
              autocomplete="off"
              required
              class="glass-control px-3 py-2 text-sm text-foreground"
            />
          </label>
          <div v-if="authorizationError" class="text-xs text-red-300">{{ authorizationError }}</div>
          <div class="flex justify-end gap-2">
            <button type="button" class="glass-control px-3 py-1.5 text-xs text-muted-foreground" @click="cancelAuthorization">取消</button>
            <button type="submit" class="glass-control px-3 py-1.5 text-xs font-medium text-primary" :disabled="submitting">
              {{ submitting ? '提交中…' : (subject.authorization.fields.length ? '继续' : '确认') }}
            </button>
          </div>
        </form>
      </div>
    </div>
  </Card>
</template>
