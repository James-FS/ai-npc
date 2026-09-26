<script setup lang="ts">
defineProps<{ title: string; description: string }>()
</script>

<template>
  <div class="page-head">
    <div class="page-head-text">
      <h1>{{ title }}</h1>
      <p>{{ description }}</p>
    </div>
    <div class="page-actions"><slot /></div>
  </div>
</template>

