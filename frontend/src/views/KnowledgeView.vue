<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useStrandTree } from '../knowledge/useStrandTree'
import { useEpisodes } from '../knowledge/useEpisodes'
import StrandTree from '../knowledge/StrandTree.vue'
import StrandDetail from '../knowledge/StrandDetail.vue'
import EpisodeList from '../knowledge/EpisodeList.vue'
import ProbeList from '../knowledge/ProbeList.vue'
import { ArrowLeft } from 'lucide-vue-next'
import { Button } from '@/components/ui/button'

type Tab = 'strands' | 'episodes' | 'probes'
const activeTab = ref<Tab>('strands')

const strandTree = useStrandTree()
const episodeStore = useEpisodes()

onMounted(async () => {
  await strandTree.load()
  await episodeStore.load()
})
</script>

<template>
  <div class="knowledge">
    <header class="knowledge-header">
      <h1>知识管理</h1>
      <Button variant="glass" size="sm" as-child>
        <router-link to="/settings">
          <ArrowLeft />
          返回设置
        </router-link>
      </Button>
    </header>

    <nav class="tabs">
      <button
        v-for="tab in (['strands', 'episodes', 'probes'] as Tab[])"
        :key="tab"
        class="tab"
        :class="{ active: activeTab === tab }"
        @click="activeTab = tab"
      >
        {{ { strands: '脉络', episodes: '片段事实', probes: '复现探针' }[tab] }}
      </button>
    </nav>

    <div v-if="activeTab === 'strands'" class="tab-content strand-layout">
      <StrandTree
        :tree="strandTree.tree.value"
        :selected-id="strandTree.selectedId.value"
        :expanded-ids="strandTree.expandedIds.value"
        :loading="strandTree.loading.value"
        :error="strandTree.error.value"
        @select="strandTree.select"
        @toggle="strandTree.toggle"
        @create="strandTree.doCreate"
      />
      <StrandDetail
        v-if="strandTree.selectedStrand.value"
        :strand="strandTree.selectedStrand.value"
        :strands="strandTree.strands.value"
        :conflict-error="strandTree.conflictError.value"
        @update="strandTree.doUpdate"
        @move="strandTree.doMove"
        @end="strandTree.doEnd"
        @mute="strandTree.doMute"
        @refresh="strandTree.load"
        @deselect="strandTree.select(null)"
      />
      <div v-else class="detail-placeholder">
        选择一个脉络查看详情
      </div>
    </div>

    <div v-if="activeTab === 'episodes'" class="tab-content">
      <EpisodeList
        :episodes="episodeStore.episodes.value"
        :loading="episodeStore.loading.value"
        :error="episodeStore.error.value"
        :conflict-error="episodeStore.conflictError.value"
        :strands="strandTree.strands.value"
        :filter-date="episodeStore.filterDate.value"
        :filter-strand-id="episodeStore.filterStrandId.value"
        :filter-unrelated="episodeStore.filterUnrelated.value"
        @update:filter-date="episodeStore.filterDate.value = $event"
        @update:filter-strand-id="episodeStore.filterStrandId.value = $event"
        @update:filter-unrelated="episodeStore.filterUnrelated.value = $event"
        @reload="episodeStore.load"
        @create="episodeStore.doCreate"
        @update="episodeStore.doUpdate"
        @relate="episodeStore.doRelate"
        @delete="episodeStore.doDelete"
        @promote="episodeStore.doPromote"
      />
    </div>

    <div v-if="activeTab === 'probes'" class="tab-content">
      <ProbeList
        :episodes="episodeStore.episodes.value"
        :strands="strandTree.strands.value"
        :conflict-error="episodeStore.conflictError.value"
        @resolve="episodeStore.doResolveProbe"
        @promote="episodeStore.doPromote"
        @mute="strandTree.doMute"
        @reload="episodeStore.load"
      />
    </div>
  </div>
</template>

<style scoped>
.knowledge {
  width: min(100%, 1200px);
  margin: 0 auto;
  padding: 2rem;
}
.knowledge-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 1.5rem;
}
.knowledge-header h1 { font-size: 1.5rem; font-weight: 700; }
.tabs {
  display: flex;
  gap: 0;
  border-bottom: 1px solid var(--border);
  margin-bottom: 1.5rem;
}
.tab {
  padding: 0.6rem 1.2rem;
  background: none;
  border: none;
  border-bottom: 2px solid transparent;
  color: var(--muted-foreground);
  cursor: pointer;
  font-size: 0.9rem;
}
.tab.active {
  color: var(--foreground);
  border-bottom-color: var(--primary);
}
.strand-layout {
  display: grid;
  grid-template-columns: 320px 1fr;
  gap: 1.5rem;
  align-items: start;
}
.detail-placeholder {
  color: var(--muted-foreground);
  padding: 2rem;
  text-align: center;
}
@media (max-width: 768px) {
  .strand-layout { grid-template-columns: 1fr; }
}
</style>
