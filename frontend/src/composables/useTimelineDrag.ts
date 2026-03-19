import { ref, onUnmounted, type Ref } from 'vue'

type PointerEvent = MouseEvent | TouchEvent

const getClientX = (e: PointerEvent): number =>
  'touches' in e ? e.touches[0].clientX : e.clientX

const getClientY = (e: PointerEvent): number =>
  'touches' in e ? e.touches[0].clientY : e.clientY

/**
 * 主时间线和 minimap 的拖拽交互逻辑
 */
export function useTimelineDrag(
  viewStart: Ref<number>,
  viewEnd: Ref<number>,
  timelineEl: Ref<HTMLElement | null>,
  selectedDate: Ref<string>
) {
  // --- State ---
  const isDraggingTimeline = ref(false)
  const isDraggingMinimap = ref(false)

  let rafId = 0

  // Timeline drag state
  let tlDragStartX = 0
  let tlDragViewStart = 0
  let tlDragViewEnd = 0
  let tlDragStartY = 0
  let directionLocked: 'h' | 'v' | null = null
  let lastDragDeltaY = 0

  // Minimap drag state
  let mmDragStartX = 0
  let mmDragViewStart = 0
  let mmDragViewEnd = 0
  let mmDragType: 'left' | 'right' | 'center' | null = null

  // --- Helpers ---
  const getDayBounds = () => {
    const dayStart = new Date(selectedDate.value).setHours(0, 0, 0, 0)
    return { dayStart, dayEnd: dayStart + 24 * 60 * 60 * 1000 }
  }

  const applyViewUpdate = (newS: number, newE: number) => {
    const { dayStart, dayEnd } = getDayBounds()
    const range = newE - newS
    if (newS < dayStart) { newS = dayStart; newE = newS + range }
    if (newE > dayEnd) { newE = dayEnd; newS = newE - range }
    viewStart.value = newS
    viewEnd.value = newE
  }

  // --- Wheel ---
  const handleWheel = (e: WheelEvent) => {
    const range = viewEnd.value - viewStart.value
    const { dayStart, dayEnd } = getDayBounds()

    if (e.ctrlKey || e.metaKey) {
      e.preventDefault()
      const zoomFactor = e.deltaY > 0 ? 1.2 : 0.8
      const cw = timelineEl.value?.clientWidth || 800
      const pivotPercent = e.offsetX / cw
      const pivotTime = viewStart.value + range * pivotPercent

      let newStart = pivotTime - (pivotTime - viewStart.value) * zoomFactor
      let newEnd = pivotTime + (viewEnd.value - pivotTime) * zoomFactor

      const minRange = 5 * 60 * 1000
      const maxRange = 24 * 60 * 60 * 1000

      if (newEnd - newStart < minRange) {
        newStart = pivotTime - minRange * pivotPercent
        newEnd = pivotTime + minRange * (1 - pivotPercent)
      }
      if (newEnd - newStart > maxRange) {
        newStart = dayStart
        newEnd = dayEnd
      }

      viewStart.value = Math.max(dayStart, newStart)
      viewEnd.value = Math.min(dayEnd, newEnd)

    } else if (Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
      e.preventDefault()
      const shift = e.deltaX * (range / 1000)
      applyViewUpdate(viewStart.value + shift, viewEnd.value + shift)
    }
  }

  // --- Main Timeline Drag ---
  const timelinePointerDown = (e: PointerEvent) => {
    const target = ('touches' in e
      ? document.elementFromPoint(getClientX(e), getClientY(e))
      : e.target) as HTMLElement | null
    if (target?.closest('.row-header')) return

    isDraggingTimeline.value = true
    tlDragStartX = getClientX(e)
    tlDragStartY = getClientY(e)
    tlDragViewStart = viewStart.value
    tlDragViewEnd = viewEnd.value
    directionLocked = null
    lastDragDeltaY = 0

    window.addEventListener('mousemove', timelinePointerMove)
    window.addEventListener('mouseup', timelinePointerUp)
    window.addEventListener('touchmove', timelinePointerMove, { passive: false })
    window.addEventListener('touchend', timelinePointerUp)
  }

  const timelinePointerMove = (e: PointerEvent) => {
    if (!isDraggingTimeline.value) return

    // Direction detection
    if (!directionLocked) {
      const dx = Math.abs(getClientX(e) - tlDragStartX)
      const dy = Math.abs(getClientY(e) - tlDragStartY)
      if (dx + dy < 5) return
      directionLocked = dx >= dy ? 'h' : 'v'
    }

    // Vertical: scroll rows
    if (directionLocked === 'v') {
      const rowsEl = timelineEl.value?.querySelector('.timeline-rows') as HTMLElement | null
      if (rowsEl) {
        const deltaY = getClientY(e) - tlDragStartY
        rowsEl.scrollTop -= deltaY - lastDragDeltaY
        lastDragDeltaY = deltaY
      }
      if ('touches' in e) e.preventDefault()
      return
    }

    // Horizontal: pan timeline
    if ('touches' in e) e.preventDefault()
    const clientX = getClientX(e)
    if (rafId) cancelAnimationFrame(rafId)
    rafId = requestAnimationFrame(() => {
      const deltaX = clientX - tlDragStartX
      const trackWidth = (timelineEl.value?.clientWidth || 800) - 120
      const range = tlDragViewEnd - tlDragViewStart
      const timeDelta = -(deltaX / trackWidth) * range
      applyViewUpdate(tlDragViewStart + timeDelta, tlDragViewEnd + timeDelta)
    })
  }

  const timelinePointerUp = () => {
    isDraggingTimeline.value = false
    directionLocked = null
    lastDragDeltaY = 0
    window.removeEventListener('mousemove', timelinePointerMove)
    window.removeEventListener('mouseup', timelinePointerUp)
    window.removeEventListener('touchmove', timelinePointerMove)
    window.removeEventListener('touchend', timelinePointerUp)
  }

  // --- Minimap Drag ---
  const minimapPointerDown = (e: PointerEvent, type: 'left' | 'right' | 'center') => {
    isDraggingMinimap.value = true
    mmDragType = type
    mmDragStartX = getClientX(e)
    mmDragViewStart = viewStart.value
    mmDragViewEnd = viewEnd.value
    window.addEventListener('mousemove', minimapPointerMove)
    window.addEventListener('mouseup', minimapPointerUp)
    window.addEventListener('touchmove', minimapPointerMove, { passive: false })
    window.addEventListener('touchend', minimapPointerUp)
  }

  const minimapPointerMove = (e: PointerEvent) => {
    if (!isDraggingMinimap.value) return
    if ('touches' in e) e.preventDefault()
    const clientX = getClientX(e)

    if (rafId) cancelAnimationFrame(rafId)
    rafId = requestAnimationFrame(() => {
      const deltaX = clientX - mmDragStartX
      const minimapWidth = timelineEl.value?.clientWidth || 800
      const dayRange = 24 * 60 * 60 * 1000
      const timeDelta = (deltaX / minimapWidth) * dayRange
      const { dayStart, dayEnd } = getDayBounds()
      const minRange = 5 * 60 * 1000

      if (mmDragType === 'center') {
        applyViewUpdate(mmDragViewStart + timeDelta, mmDragViewEnd + timeDelta)
      } else if (mmDragType === 'left') {
        let newS = mmDragViewStart + timeDelta
        if (newS < dayStart) newS = dayStart
        if (newS > viewEnd.value - minRange) newS = viewEnd.value - minRange
        viewStart.value = newS
      } else if (mmDragType === 'right') {
        let newE = mmDragViewEnd + timeDelta
        if (newE > dayEnd) newE = dayEnd
        if (newE < viewStart.value + minRange) newE = viewStart.value + minRange
        viewEnd.value = newE
      }
    })
  }

  const minimapPointerUp = () => {
    isDraggingMinimap.value = false
    mmDragType = null
    window.removeEventListener('mousemove', minimapPointerMove)
    window.removeEventListener('mouseup', minimapPointerUp)
    window.removeEventListener('touchmove', minimapPointerMove)
    window.removeEventListener('touchend', minimapPointerUp)
  }

  // --- Cleanup ---
  onUnmounted(() => {
    timelinePointerUp()
    minimapPointerUp()
    if (rafId) cancelAnimationFrame(rafId)
  })

  return {
    isDraggingTimeline,
    isDraggingMinimap,
    handleWheel,
    timelinePointerDown,
    minimapPointerDown,
  }
}
