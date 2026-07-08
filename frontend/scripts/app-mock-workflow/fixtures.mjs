export function makeStressChapters(count, stressTitle) {
  return Array.from({ length: count }, (_, index) => {
    const chapterNumber = index + 1
    return {
      id: chapterNumber,
      novel_id: 42,
      chapter_number: chapterNumber,
      title: chapterNumber === count ? stressTitle : `压力章 ${String(chapterNumber).padStart(3, '0')}`,
      summary: chapterNumber === count ? '10MB 中文正文压力章节。' : '压力测试占位章节。',
      word_count: chapterNumber === count ? 3_500_000 : 1200,
      file_path: `chapters/${chapterNumber}.md`,
      created_at: '2026-07-05T12:00:00.000Z',
      updated_at: '2026-07-05T12:00:00.000Z',
    }
  })
}

export function makeLargeChineseFixture(targetBytes) {
  const chunks = []
  let bytes = 0
  let paragraph = 1

  while (bytes < targetBytes) {
    const line = `第${paragraph}段，雨声沿着旧城门往下落，林岚把杯子推远，仍然只记录自己能看见的水痕、灯影和门缝里的停顿。她不替门外的人下结论，也不把未经确认的身份写进正文。\n`
    chunks.push(line)
    bytes += Buffer.byteLength(line, 'utf8')
    paragraph += 1
  }

  return chunks.join('')
}

export function makeStressReferenceFixture(sourceText, stressChapterCount) {
  const sourceBytes = Buffer.byteLength(sourceText, 'utf8')
  const sourceSegmentCount = Math.max(1_200, Math.ceil(sourceBytes / 4096))
  const materialTotal = Math.max(1_200, sourceSegmentCount)
  const anchorId = 1001
  const chapterNumber = stressChapterCount
  const sourcePath = 'D:\\books\\stress-reference-10mb.md'

  const anchor = {
    anchor_id: anchorId,
    novel_id: 42,
    title: '10MB 自动分段参考源',
    author: 'Phase 13 Stress Fixture',
    source_path: sourcePath,
    source_kind: 'markdown',
    license_status: 'user_provided',
    visibility: 'workspace',
    source_trust: 'user_verified',
    owner_scope: 'workspace_corpus',
    owner_novel_id: null,
    user_tags: ['10MB', '自动分段', '压力测试'],
    source_file_hash: `hash-stress-${sourceBytes}`,
    build_version: 'mock-reference-stress-v1',
    status: 'ready',
    created_at: '2026-07-05T12:00:00.000Z',
    updated_at: '2026-07-05T12:00:00.000Z',
  }

  return {
    anchor,
    buildStatus: {
      novel_id: 42,
      anchor_id: anchorId,
      status: 'ready',
      stage: 'completed',
      source_segment_count: sourceSegmentCount,
      material_count: materialTotal,
      slot_count: Math.ceil(materialTotal / 5),
      vector_count: 0,
      last_error: '',
      updated_at: '2026-07-05T12:00:00.000Z',
    },
    sourceBytes,
    sourceCharacters: sourceText.length,
    materialTotal,
    chapterNumber,
    sourcePath,
  }
}

export function realisticWritingText() {
  const paragraphs = [
    '## 雨夜复盘',
    '林岚在雨夜旧宅门前停住。门缝里没有灯，檐下却挂着半截湿线，像有人刚把伞收起，又故意把水滴留在青砖上。',
    '她没有立刻推门，只把手套往上拉了拉，低声记下三件事：第一，杯沿朝外；第二，桌面水痕还没散；第三，门后的人知道她会来。',
    '> 这不是供词，只是一段给自己看的现场笔记。',
    '- 风从旧城门方向吹来，带着铁锈味。',
    '- 鞋印停在门槛前，没有跨进去。',
    '- “如果我说没看见，你会信吗？”周砚问。',
    '林岚没有回答。她把那句话在心里放慢了一遍，确认其中的停顿比内容更像线索。雨声压住远处车灯，整条巷子只剩纸页被潮气卷起的声音。',
    '她写下最后一行：不要替门外的人下结论；先让读者看见水、光、脚印和沉默。',
  ]

  return paragraphs.join('\n\n')
}

export function mockImportRecoveryResult() {
  const now = '2026-07-05T12:00:00.000Z'
  return {
    reconciled_runs: [
      {
        task_id: 'startup-cleaned-import',
        state: 'cleanup_completed',
        stage: 'cleanup_completed',
        source_display_name: '半截导入.txt',
        source_path_hash: 'sha256:startup-cleaned',
        parser_type: 'txt',
        created_novel_id: 77,
        created_file_roots: ['novels/77'],
        skipped_chapters: [],
        diagnostics: [],
        warnings: [],
        error: {
          code: 'import.recovered_cleanup',
          message: '启动恢复已清理未完成的导入。',
          detail: 'Created rows and files were removed.',
          operation: 'ReconcileNovelImportRuns',
          task_id: 'startup-cleaned-import',
          run_id: null,
          bridge_method: 'ReconcileNovelImportRuns',
          timestamp: now,
        },
        started_at: now,
        updated_at: now,
        completed_at: now,
      },
    ],
    blocked_runs: [
      {
        task_id: 'startup-blocked-import',
        state: 'cleanup_blocked',
        stage: 'cleanup_blocked',
        source_display_name: '越界导入.txt',
        source_path_hash: 'sha256:startup-blocked',
        parser_type: 'txt',
        created_novel_id: 88,
        created_file_roots: ['novels/88'],
        skipped_chapters: [],
        diagnostics: [],
        warnings: [],
        error: {
          code: 'import.cleanup_blocked',
          message: '导入恢复清理被阻止。',
          detail: 'Created file root resolves outside the allowed cleanup boundary.',
          operation: 'ReconcileNovelImportRuns',
          task_id: 'startup-blocked-import',
          run_id: null,
          bridge_method: 'ReconcileNovelImportRuns',
          timestamp: now,
        },
        started_at: now,
        updated_at: now,
        completed_at: now,
      },
    ],
    diagnostics: [
      {
        code: 'import.cleanup_blocked',
        message: '导入恢复清理被阻止。',
        detail: 'startup-blocked-import requires manual review.',
        severity: 'warning',
      },
    ],
    reconciled_at: now,
  }
}
