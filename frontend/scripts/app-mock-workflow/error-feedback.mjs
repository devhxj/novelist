import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import path from 'node:path'

export async function verifyErrorFeedbackWorkflow(context) {
  const {
    page,
    outputDir,
    clickActivity,
    clickCardAction,
    waitForBridgeCall,
    waitForBridgeCallCountAfter,
    bridgeCallCount,
    errorAlert,
    expectHidden,
    expectVisible,
    assertNoSensitiveDiagnosticsVisible,
    assertCopyableDiagnostic,
    ensureChapterBlockExpanded,
    chapterButton,
    dispatchNovelImportDrop,
  } = context

  await verifyMetadataCrudErrorFeedback(context)
  await verifyLegacySaveExportErrorFeedback(context)
  await verifyLegacySurfaceErrorLifecycle(context)
  await verifyApprovalSubmitErrorRecovery(context)

  await clickActivity(page, '角色')
  await clickCardAction(page.locator('main'), '林岚', '删除')
  await waitForBridgeCall(page, 'DeleteCharacter')
  const characterAlert = errorAlert(page, '角色删除失败')
  await expectVisible(characterAlert, 'character delete error callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, characterAlert, 'DeleteCharacter')

  await clickActivity(page, '地点')
  await clickCardAction(page.locator('main'), '旧城门', '删除')
  await waitForBridgeCall(page, 'DeleteLocation')
  const locationAlert = errorAlert(page, '地点删除失败')
  await expectVisible(locationAlert, 'location delete error callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, locationAlert, 'DeleteLocation')

  await clickActivity(page, '技能')
  await clickCardAction(page.locator('aside'), '节奏控制', '删除技能')
  await waitForBridgeCall(page, 'DeleteSkill')
  const skillAlert = errorAlert(page, '技能删除失败')
  await expectVisible(skillAlert, 'skill delete error callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, skillAlert, 'DeleteSkill')

  await clickActivity(page, '章节')
  await ensureChapterBlockExpanded(page)
  const chapterRow = chapterButton(page, '雨夜线索').locator('xpath=..')
  await chapterRow.getByRole('button', { name: /编辑章节/ }).click({ force: true })
  await chapterRow.getByRole('textbox').fill('雨夜线索-失败')
  await page.keyboard.press('Enter')
  await waitForBridgeCall(page, 'UpdateChapterTitle')
  const chapterAlert = errorAlert(page, '章节重命名失败')
  await expectVisible(chapterAlert, 'chapter rename error callout')
  await expectVisible(chapterAlert.getByRole('button', { name: '复制错误诊断' }), 'chapter rename copy diagnostics button')
  await assertNoSensitiveDiagnosticsVisible(page)

  const importFixtureDir = path.join(outputDir, 'fixtures', 'error-feedback')
  await fs.mkdir(importFixtureDir, { recursive: true })
  const importFailureFile = path.join(importFixtureDir, 'error-parser-failure.txt')
  await fs.writeFile(importFailureFile, 'error feedback import fixture', 'utf8')
  const importBefore = await bridgeCallCount(page, 'StartNovelImport')
  await clickActivity(page, '书架')
  await dispatchNovelImportDrop(page, {
    kind: 'files',
    files: [{ name: 'error-parser-failure.txt', path: importFailureFile, type: 'text/plain' }],
  })
  await waitForBridgeCallCountAfter(page, 'StartNovelImport', importBefore)
  const importAlert = errorAlert(page, '导入失败')
  await expectVisible(importAlert, 'novel import error callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, importAlert, 'StartNovelImport')
  await page.getByRole('button', { name: '完成', exact: true }).click()

  const createNovelBefore = await bridgeCallCount(page, 'CreateNovel')
  await page.getByRole('button', { name: '新建作品' }).last().click()
  await page.getByPlaceholder('输入书名').fill('错误反馈新书')
  await page.locator('.fixed').getByRole('button', { name: '保存' }).click()
  await waitForBridgeCallCountAfter(page, 'CreateNovel', createNovelBefore)
  const createNovelAlert = errorAlert(page, '创建作品失败')
  await expectVisible(createNovelAlert, 'create novel dialog error callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, createNovelAlert, 'CreateNovel')
  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()
  await expectErrorPersistsAfter(
    async () => { await page.getByRole('button', { name: '新建作品' }).last().click() },
    createNovelAlert,
    expectVisible,
    'create novel error after reopening create dialog',
    page,
  )
  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()

  const updateNovelBefore = await bridgeCallCount(page, 'UpdateNovel')
  await page.getByRole('button', { name: '编辑作品 全局回归小说', exact: true }).click({ force: true })
  await page.getByPlaceholder('输入书名').fill('全局回归小说-错误')
  await page.locator('.fixed').getByRole('button', { name: '保存' }).click()
  await waitForBridgeCallCountAfter(page, 'UpdateNovel', updateNovelBefore)
  const updateNovelAlert = errorAlert(page, '更新作品失败')
  await expectVisible(updateNovelAlert, 'update novel dialog error callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, updateNovelAlert, 'UpdateNovel')
  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()

  const deleteNovelBefore = await bridgeCallCount(page, 'DeleteNovel')
  await page.getByRole('button', { name: '删除作品 全局回归小说', exact: true }).click({ force: true })
  await page.getByPlaceholder('输入书名确认').fill('全局回归小说')
  await page.locator('.fixed').getByRole('button', { name: '确认删除' }).click()
  await waitForBridgeCallCountAfter(page, 'DeleteNovel', deleteNovelBefore)
  const deleteNovelAlert = errorAlert(page, '删除作品失败')
  await expectVisible(deleteNovelAlert, 'delete novel dialog error callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, deleteNovelAlert, 'DeleteNovel')
  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()
  await expectErrorPersistsAfter(
    async () => { await page.getByRole('button', { name: '删除作品 全局回归小说', exact: true }).click({ force: true }) },
    deleteNovelAlert,
    expectVisible,
    'delete novel error after reopening delete dialog',
    page,
  )
  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()
}

async function verifyMetadataCrudErrorFeedback(context) {
  const {
    browser,
    url,
    consoleErrors,
    pageErrors,
    newAppPage,
    installClipboardSpy,
    sensitiveDiagnosticDetails,
    clickActivity,
    clickCardAction,
    waitForBridgeCallCountAfter,
    bridgeCallCount,
    errorAlert,
    expectVisible,
    assertNoSensitiveDiagnosticsVisible,
    assertCopyableDiagnostic,
  } = context
  const metadataPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    confirmResult: true,
    faults: {
      CreateReaderPerspective: {
        mode: 'storage',
        code: 'READER_PERSPECTIVE_CREATE_FAILED',
        message: '创建读者视角失败：Bearer reader-create-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
      UpdateReaderPerspective: [
        {
          mode: 'storage',
          code: 'READER_PERSPECTIVE_QUICK_REVEAL_FAILED',
          message: '标记读者视角已回收失败：Bearer reader-quick-reveal-token-abcdefghijklmnopqrstuvwxyz',
          details: sensitiveDiagnosticDetails(),
          retryable: true,
        },
        {
          mode: 'storage',
          code: 'READER_PERSPECTIVE_UPDATE_FAILED',
          message: '更新读者视角失败：Bearer reader-update-token-abcdefghijklmnopqrstuvwxyz',
          details: sensitiveDiagnosticDetails(),
          retryable: true,
        },
      ],
      DeleteReaderPerspective: {
        mode: 'storage',
        code: 'READER_PERSPECTIVE_DELETE_FAILED',
        message: '删除读者视角失败：Bearer reader-delete-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
      CreatePreference: {
        mode: 'storage',
        code: 'PREFERENCE_CREATE_FAILED',
        message: '创建偏好失败：Bearer preference-create-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
      UpdatePreference: {
        mode: 'storage',
        code: 'PREFERENCE_UPDATE_FAILED',
        message: '更新偏好失败：Bearer preference-update-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
      DeletePreference: {
        mode: 'storage',
        code: 'PREFERENCE_DELETE_FAILED',
        message: '删除偏好失败：Bearer preference-delete-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
      CreateStoryArc: {
        mode: 'storage',
        code: 'STORY_ARC_CREATE_FAILED',
        message: '创建弧线失败：Bearer story-arc-create-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
      UpdateStoryArc: {
        mode: 'storage',
        code: 'STORY_ARC_UPDATE_FAILED',
        message: '更新弧线失败：Bearer story-arc-update-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
      DeleteStoryArc: {
        mode: 'storage',
        code: 'STORY_ARC_DELETE_FAILED',
        message: '删除弧线失败：Bearer story-arc-delete-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
      CreateArcNode: {
        mode: 'storage',
        code: 'ARC_NODE_CREATE_FAILED',
        message: '创建节点失败：Bearer arc-node-create-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
      UpdateArcNode: [
        {
          mode: 'storage',
          code: 'ARC_NODE_QUICK_STATUS_FAILED',
          message: '更新节点状态失败：Bearer arc-node-quick-token-abcdefghijklmnopqrstuvwxyz',
          details: sensitiveDiagnosticDetails(),
          retryable: true,
        },
        {
          mode: 'storage',
          code: 'ARC_NODE_UPDATE_FAILED',
          message: '更新节点失败：Bearer arc-node-update-token-abcdefghijklmnopqrstuvwxyz',
          details: sensitiveDiagnosticDetails(),
          retryable: true,
        },
      ],
      DeleteArcNode: {
        mode: 'storage',
        code: 'ARC_NODE_DELETE_FAILED',
        message: '删除节点失败：Bearer arc-node-delete-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
      UpdateChapterPlan: {
        mode: 'storage',
        code: 'CHAPTER_PLAN_UPDATE_FAILED',
        message: '保存计划失败：Bearer timeline-plan-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
      CreateTimelineEntry: {
        mode: 'storage',
        code: 'TIMELINE_CREATE_FAILED',
        message: '创建时间线条目失败：Bearer timeline-create-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
      UpdateTimelineEntry: [
        {
          mode: 'storage',
          code: 'TIMELINE_QUICK_STATUS_FAILED',
          message: '更新时间线状态失败：Bearer timeline-quick-token-abcdefghijklmnopqrstuvwxyz',
          details: sensitiveDiagnosticDetails(),
          retryable: true,
        },
        {
          mode: 'storage',
          code: 'TIMELINE_UPDATE_FAILED',
          message: '更新时间线条目失败：Bearer timeline-update-token-abcdefghijklmnopqrstuvwxyz',
          details: sensitiveDiagnosticDetails(),
          retryable: true,
        },
      ],
      DeleteTimelineEntry: {
        mode: 'storage',
        code: 'TIMELINE_DELETE_FAILED',
        message: '删除时间线条目失败：Bearer timeline-delete-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
    },
  }, undefined, 'metadata-crud-error')
  await installClipboardSpy(metadataPage)
  await metadataPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(metadataPage.getByText('全局回归小说'), 'workspace title before metadata CRUD failure')

  await clickActivity(metadataPage, '弧线')
  await expectVisible(metadataPage.getByRole('heading', { name: /弧线节点/ }), 'story arc view before error feedback')
  const storyArcCreateBefore = await bridgeCallCount(metadataPage, 'CreateStoryArc')
  await metadataPage.locator('main').getByRole('button', { name: '新弧线' }).click()
  await metadataPage.getByPlaceholder('弧线名称').fill('错误反馈弧线')
  await metadataPage.getByPlaceholder('弧线整体描述').fill('错误反馈弧线描述需要被诊断遮蔽。')
  await metadataPage.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCallCountAfter(metadataPage, 'CreateStoryArc', storyArcCreateBefore)
  const storyArcCreateAlert = errorAlert(metadataPage, '创建弧线失败')
  await expectVisible(storyArcCreateAlert, 'story arc create error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, storyArcCreateAlert, 'CreateStoryArc')
  await metadataPage.locator('main').getByRole('button', { name: '取消' }).last().click()

  const arcNodeQuickBefore = await bridgeCallCount(metadataPage, 'UpdateArcNode')
  await clickCardAction(metadataPage.locator('main'), '桌面水痕触发调查', '标记完成')
  await waitForBridgeCallCountAfter(metadataPage, 'UpdateArcNode', arcNodeQuickBefore)
  const arcNodeQuickAlert = errorAlert(metadataPage, '更新节点状态失败')
  await expectVisible(arcNodeQuickAlert, 'arc node quick status error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, arcNodeQuickAlert, 'UpdateArcNode')

  const storyArcUpdateBefore = await bridgeCallCount(metadataPage, 'UpdateStoryArc')
  await metadataPage.locator('button').filter({ hasText: '雨夜调查线' }).getByTitle('编辑').click({ force: true })
  await metadataPage.getByPlaceholder('弧线名称').fill('雨夜调查线-错误反馈')
  await metadataPage.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCallCountAfter(metadataPage, 'UpdateStoryArc', storyArcUpdateBefore)
  const storyArcUpdateAlert = errorAlert(metadataPage, '更新弧线失败')
  await expectVisible(storyArcUpdateAlert, 'story arc update error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, storyArcUpdateAlert, 'UpdateStoryArc')
  await metadataPage.locator('main').getByRole('button', { name: '取消' }).last().click()

  const storyArcDeleteBefore = await bridgeCallCount(metadataPage, 'DeleteStoryArc')
  await metadataPage.locator('button').filter({ hasText: '雨夜调查线' }).getByTitle('删除').click({ force: true })
  await waitForBridgeCallCountAfter(metadataPage, 'DeleteStoryArc', storyArcDeleteBefore)
  const storyArcDeleteAlert = errorAlert(metadataPage, '删除弧线失败')
  await expectVisible(storyArcDeleteAlert, 'story arc delete error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, storyArcDeleteAlert, 'DeleteStoryArc')

  const arcNodeCreateBefore = await bridgeCallCount(metadataPage, 'CreateArcNode')
  await metadataPage.locator('main').getByRole('button', { name: '新建节点' }).click()
  await metadataPage.getByPlaceholder('节点标题').fill('错误反馈节点')
  await metadataPage.getByPlaceholder('节点详情').fill('错误反馈节点详情需要被诊断遮蔽。')
  await metadataPage.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCallCountAfter(metadataPage, 'CreateArcNode', arcNodeCreateBefore)
  const arcNodeCreateAlert = errorAlert(metadataPage, '创建节点失败')
  await expectVisible(arcNodeCreateAlert, 'arc node create error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, arcNodeCreateAlert, 'CreateArcNode')
  await metadataPage.locator('main').getByRole('button', { name: '取消' }).last().click()

  const arcNodeUpdateBefore = await bridgeCallCount(metadataPage, 'UpdateArcNode')
  await clickCardAction(metadataPage.locator('main'), '桌面水痕触发调查', '编辑')
  await metadataPage.getByPlaceholder('节点标题').fill('桌面水痕触发调查-错误反馈')
  await metadataPage.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCallCountAfter(metadataPage, 'UpdateArcNode', arcNodeUpdateBefore)
  const arcNodeUpdateAlert = errorAlert(metadataPage, '更新节点失败')
  await expectVisible(arcNodeUpdateAlert, 'arc node update error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, arcNodeUpdateAlert, 'UpdateArcNode')

  const arcNodeDeleteBefore = await bridgeCallCount(metadataPage, 'DeleteArcNode')
  await metadataPage.locator('main').getByRole('button', { name: '删除' }).first().click()
  await waitForBridgeCallCountAfter(metadataPage, 'DeleteArcNode', arcNodeDeleteBefore)
  const arcNodeDeleteAlert = errorAlert(metadataPage, '删除节点失败')
  await expectVisible(arcNodeDeleteAlert, 'arc node delete error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, arcNodeDeleteAlert, 'DeleteArcNode')

  await clickActivity(metadataPage, '时间线')
  await expectVisible(metadataPage.getByRole('heading', { name: /章节计划/ }), 'timeline view before error feedback')
  const chapterPlanBefore = await bridgeCallCount(metadataPage, 'UpdateChapterPlan')
  await metadataPage.locator('section').filter({ hasText: '章节计划' }).getByTitle('编辑').click({ force: true })
  await metadataPage.getByPlaceholder('细纲计划内容...').fill('错误反馈章节计划需要被诊断遮蔽。')
  await metadataPage.locator('section').filter({ hasText: '章节计划' }).getByRole('button', { name: '保存' }).click()
  await waitForBridgeCallCountAfter(metadataPage, 'UpdateChapterPlan', chapterPlanBefore)
  const chapterPlanAlert = errorAlert(metadataPage, '保存计划失败')
  await expectVisible(chapterPlanAlert, 'chapter plan update error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, chapterPlanAlert, 'UpdateChapterPlan')
  await metadataPage.locator('section').filter({ hasText: '章节计划' }).getByRole('button', { name: '取消' }).click()

  const timelineCreateBefore = await bridgeCallCount(metadataPage, 'CreateTimelineEntry')
  await metadataPage.locator('main').getByRole('button', { name: '新建' }).click()
  await metadataPage.getByPlaceholder('简短标题').fill('错误反馈时间线')
  await metadataPage.getByPlaceholder('详细描述').fill('错误反馈时间线内容需要被诊断遮蔽。')
  await metadataPage.locator('main').getByRole('button', { name: '创建' }).last().click()
  await waitForBridgeCallCountAfter(metadataPage, 'CreateTimelineEntry', timelineCreateBefore)
  const timelineCreateAlert = errorAlert(metadataPage, '创建时间线条目失败')
  await expectVisible(timelineCreateAlert, 'timeline create error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, timelineCreateAlert, 'CreateTimelineEntry')
  await metadataPage.locator('main').getByRole('button', { name: '取消' }).last().click()

  const timelineQuickBefore = await bridgeCallCount(metadataPage, 'UpdateTimelineEntry')
  await clickCardAction(metadataPage.locator('main'), '桌面水痕', '标记已回收')
  await waitForBridgeCallCountAfter(metadataPage, 'UpdateTimelineEntry', timelineQuickBefore)
  const timelineQuickAlert = errorAlert(metadataPage, '更新时间线状态失败')
  await expectVisible(timelineQuickAlert, 'timeline quick status error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, timelineQuickAlert, 'UpdateTimelineEntry')

  const timelineUpdateBefore = await bridgeCallCount(metadataPage, 'UpdateTimelineEntry')
  await clickCardAction(metadataPage.locator('main'), '桌面水痕', '编辑')
  await metadataPage.getByPlaceholder('简短标题').fill('桌面水痕-错误反馈')
  await metadataPage.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCallCountAfter(metadataPage, 'UpdateTimelineEntry', timelineUpdateBefore)
  const timelineUpdateAlert = errorAlert(metadataPage, '更新时间线条目失败')
  await expectVisible(timelineUpdateAlert, 'timeline update error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, timelineUpdateAlert, 'UpdateTimelineEntry')

  const timelineDeleteBefore = await bridgeCallCount(metadataPage, 'DeleteTimelineEntry')
  await metadataPage.locator('main').getByRole('button', { name: '删除' }).first().click()
  await waitForBridgeCallCountAfter(metadataPage, 'DeleteTimelineEntry', timelineDeleteBefore)
  const timelineDeleteAlert = errorAlert(metadataPage, '删除时间线条目失败')
  await expectVisible(timelineDeleteAlert, 'timeline delete error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, timelineDeleteAlert, 'DeleteTimelineEntry')

  await clickActivity(metadataPage, '读者视角')
  await expectVisible(metadataPage.getByRole('heading', { name: /读者视角/ }), 'reader view before error feedback')
  const readerCreateBefore = await bridgeCallCount(metadataPage, 'CreateReaderPerspective')
  await metadataPage.locator('main').getByRole('button', { name: '新建' }).click()
  await metadataPage.getByPlaceholder('读者知道/想知道/误以为的事情').fill('读者误以为旧城门已经安全。')
  await metadataPage.getByPlaceholder('真实情况是什么').fill('旧城门仍有人守着。')
  await metadataPage.locator('main').getByRole('button', { name: '创建' }).last().click()
  await waitForBridgeCallCountAfter(metadataPage, 'CreateReaderPerspective', readerCreateBefore)
  const readerCreateAlert = errorAlert(metadataPage, '创建读者视角失败')
  await expectVisible(readerCreateAlert, 'reader create error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, readerCreateAlert, 'CreateReaderPerspective')

  const readerUpdateBefore = await bridgeCallCount(metadataPage, 'UpdateReaderPerspective')
  await clickCardAction(metadataPage.locator('main'), '读者知道林岚正在调查旧城门', '标记已回收')
  await waitForBridgeCallCountAfter(metadataPage, 'UpdateReaderPerspective', readerUpdateBefore)
  const readerQuickRevealAlert = errorAlert(metadataPage, '标记读者视角已回收失败')
  await expectVisible(readerQuickRevealAlert, 'reader quick reveal error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, readerQuickRevealAlert, 'UpdateReaderPerspective')

  const readerEditBefore = await bridgeCallCount(metadataPage, 'UpdateReaderPerspective')
  await clickCardAction(metadataPage.locator('main'), '读者知道林岚正在调查旧城门', '编辑')
  await metadataPage.getByPlaceholder('读者知道/想知道/误以为的事情').fill('读者知道林岚正在调查旧城门，但线索仍不完整。')
  await metadataPage.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCallCountAfter(metadataPage, 'UpdateReaderPerspective', readerEditBefore)
  const readerUpdateAlert = errorAlert(metadataPage, '更新读者视角失败')
  await expectVisible(readerUpdateAlert, 'reader update error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, readerUpdateAlert, 'UpdateReaderPerspective')

  const readerDeleteBefore = await bridgeCallCount(metadataPage, 'DeleteReaderPerspective')
  await metadataPage.locator('main').getByRole('button', { name: '删除' }).first().click()
  await waitForBridgeCallCountAfter(metadataPage, 'DeleteReaderPerspective', readerDeleteBefore)
  const readerDeleteAlert = errorAlert(metadataPage, '删除读者视角失败')
  await expectVisible(readerDeleteAlert, 'reader delete error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, readerDeleteAlert, 'DeleteReaderPerspective')

  await clickActivity(metadataPage, '偏好')
  await expectVisible(metadataPage.getByRole('heading', { name: /创作偏好/ }), 'preference view before error feedback')
  const preferenceCreateBefore = await bridgeCallCount(metadataPage, 'CreatePreference')
  await metadataPage.locator('section').filter({ hasText: '全局偏好' }).getByRole('button', { name: '添加' }).click()
  await metadataPage.getByPlaceholder('风格、对话、世界观...').fill('错误反馈')
  await metadataPage.getByPlaceholder('偏好内容').fill('错误反馈偏好内容需要被诊断遮蔽。')
  await metadataPage.locator('main').getByRole('button', { name: '创建' }).last().click()
  await waitForBridgeCallCountAfter(metadataPage, 'CreatePreference', preferenceCreateBefore)
  const preferenceCreateAlert = errorAlert(metadataPage, '创建偏好失败')
  await expectVisible(preferenceCreateAlert, 'preference create error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, preferenceCreateAlert, 'CreatePreference')

  const preferenceUpdateBefore = await bridgeCallCount(metadataPage, 'UpdatePreference')
  await clickCardAction(metadataPage.locator('main'), '保持受限视角', '编辑')
  await metadataPage.getByPlaceholder('偏好内容').fill('保持受限视角，仍不提前解释。')
  await metadataPage.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCallCountAfter(metadataPage, 'UpdatePreference', preferenceUpdateBefore)
  const preferenceUpdateAlert = errorAlert(metadataPage, '更新偏好失败')
  await expectVisible(preferenceUpdateAlert, 'preference update error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, preferenceUpdateAlert, 'UpdatePreference')

  const preferenceDeleteBefore = await bridgeCallCount(metadataPage, 'DeletePreference')
  await metadataPage.locator('main').getByRole('button', { name: '删除' }).first().click()
  await waitForBridgeCallCountAfter(metadataPage, 'DeletePreference', preferenceDeleteBefore)
  const preferenceDeleteAlert = errorAlert(metadataPage, '删除偏好失败')
  await expectVisible(preferenceDeleteAlert, 'preference delete error callout')
  await assertNoSensitiveDiagnosticsVisible(metadataPage)
  await assertCopyableDiagnostic(metadataPage, preferenceDeleteAlert, 'DeletePreference')

  await metadataPage.close()
}

async function verifyLegacySurfaceErrorLifecycle(context) {
  await verifyCharacterAndLocationErrorLifecycle(context)
  await verifyStoryArcErrorLifecycle(context)
  await verifyTimelineErrorLifecycle(context)
  await verifyReaderPreferenceErrorLifecycle(context)
}

async function verifyCharacterAndLocationErrorLifecycle(context) {
  const {
    browser,
    url,
    consoleErrors,
    pageErrors,
    newAppPage,
    installClipboardSpy,
    sensitiveDiagnosticDetails,
    clickActivity,
    clickCardAction,
    waitForBridgeCallCountAfter,
    bridgeCallCount,
    errorAlert,
    expectVisible,
  } = context

  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    confirmResult: true,
    faults: {
      DeleteCharacter: lifecycleFault('CHARACTER_DELETE_LIFECYCLE_FAILED', '角色删除失败：Bearer live-error-token-abcdefghijklmnopqrstuvwxyz', sensitiveDiagnosticDetails()),
      DeleteLocation: lifecycleFault('LOCATION_DELETE_LIFECYCLE_FAILED', '地点删除失败：Bearer live-error-token-abcdefghijklmnopqrstuvwxyz', sensitiveDiagnosticDetails()),
    },
  }, undefined, 'character-location-error-lifecycle')
  await installClipboardSpy(page)
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'workspace title before character/location lifecycle review')

  await clickActivity(page, '角色')
  const characterDeleteBefore = await bridgeCallCount(page, 'DeleteCharacter')
  await clickCardAction(page.locator('main'), '林岚', '删除')
  await waitForBridgeCallCountAfter(page, 'DeleteCharacter', characterDeleteBefore)
  const characterAlert = errorAlert(page, '角色删除失败')
  await expectVisible(characterAlert, 'character delete lifecycle error callout')
  await expectErrorPersistsAfter(
    async () => { await page.getByRole('button', { name: '新建角色' }).click() },
    characterAlert,
    expectVisible,
    'character delete error after opening create form',
  )
  await page.locator('main').getByRole('button', { name: '取消' }).last().click()
  await expectErrorPersistsAfter(
    async () => { await clickCardAction(page.locator('main'), '林岚', '编辑') },
    characterAlert,
    expectVisible,
    'character delete error after opening edit form',
  )
  await page.locator('main').getByRole('button', { name: '取消' }).last().click()

  await clickActivity(page, '地点')
  const locationDeleteBefore = await bridgeCallCount(page, 'DeleteLocation')
  await clickCardAction(page.locator('main'), '旧城门', '删除')
  await waitForBridgeCallCountAfter(page, 'DeleteLocation', locationDeleteBefore)
  const locationAlert = errorAlert(page, '地点删除失败')
  await expectVisible(locationAlert, 'location delete lifecycle error callout')
  await expectErrorPersistsAfter(
    async () => { await page.getByRole('button', { name: '新建地点' }).click() },
    locationAlert,
    expectVisible,
    'location delete error after opening create form',
  )
  await page.locator('main').getByRole('button', { name: '取消' }).last().click()
  await expectErrorPersistsAfter(
    async () => { await clickCardAction(page.locator('main'), '旧城门', '编辑') },
    locationAlert,
    expectVisible,
    'location delete error after opening edit form',
  )
  await page.close()
}

async function verifyStoryArcErrorLifecycle(context) {
  const {
    browser,
    url,
    consoleErrors,
    pageErrors,
    newAppPage,
    installClipboardSpy,
    sensitiveDiagnosticDetails,
    clickActivity,
    clickCardAction,
    waitForBridgeCallCountAfter,
    bridgeCallCount,
    errorAlert,
    expectVisible,
  } = context

  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      CreateStoryArc: lifecycleFault('STORY_ARC_CREATE_LIFECYCLE_FAILED', '创建弧线失败：Bearer story-arc-create-token-abcdefghijklmnopqrstuvwxyz', sensitiveDiagnosticDetails()),
    },
  }, undefined, 'story-arc-error-lifecycle')
  await installClipboardSpy(page)
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'workspace title before story arc lifecycle review')
  await clickActivity(page, '弧线')
  await expectVisible(page.getByRole('heading', { name: /弧线节点/ }), 'story arc view before lifecycle failure')

  const createBefore = await bridgeCallCount(page, 'CreateStoryArc')
  await page.locator('main').getByRole('button', { name: '新弧线' }).click()
  await page.getByPlaceholder('弧线名称').fill('生命周期弧线')
  await page.getByPlaceholder('弧线整体描述').fill('生命周期错误仍应保留。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCallCountAfter(page, 'CreateStoryArc', createBefore)
  const alert = errorAlert(page, '创建弧线失败')
  await expectVisible(alert, 'story arc lifecycle error callout')

  await page.locator('main').getByRole('button', { name: '取消' }).last().click()
  await expectErrorPersistsAfter(
    async () => { await page.locator('main').getByRole('button', { name: '新弧线' }).click() },
    alert,
    expectVisible,
    'story arc create error after reopening create arc form',
  )
  await page.locator('main').getByRole('button', { name: '取消' }).last().click()
  await expectErrorPersistsAfter(
    async () => { await page.locator('button').filter({ hasText: '雨夜调查线' }).getByTitle('编辑').click({ force: true }) },
    alert,
    expectVisible,
    'story arc create error after opening edit arc form',
  )
  await page.locator('main').getByRole('button', { name: '取消' }).last().click()
  await expectErrorPersistsAfter(
    async () => { await page.locator('main').getByRole('button', { name: '新建节点' }).click() },
    alert,
    expectVisible,
    'story arc create error after opening create node form',
  )
  await page.locator('main').getByRole('button', { name: '取消' }).last().click()
  await expectErrorPersistsAfter(
    async () => { await clickCardAction(page.locator('main'), '桌面水痕触发调查', '编辑') },
    alert,
    expectVisible,
    'story arc create error after opening edit node form',
  )
  await page.close()
}

async function verifyTimelineErrorLifecycle(context) {
  const {
    browser,
    url,
    consoleErrors,
    pageErrors,
    newAppPage,
    installClipboardSpy,
    sensitiveDiagnosticDetails,
    clickActivity,
    clickCardAction,
    waitForBridgeCallCountAfter,
    bridgeCallCount,
    errorAlert,
    expectVisible,
  } = context

  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      UpdateChapterPlan: lifecycleFault('CHAPTER_PLAN_LIFECYCLE_FAILED', '保存计划失败：Bearer timeline-plan-token-abcdefghijklmnopqrstuvwxyz', sensitiveDiagnosticDetails()),
    },
  }, undefined, 'timeline-error-lifecycle')
  await installClipboardSpy(page)
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'workspace title before timeline lifecycle review')
  await clickActivity(page, '时间线')
  await expectVisible(page.getByRole('heading', { name: /章节计划/ }), 'timeline view before lifecycle failure')

  const planSection = page.locator('section').filter({ hasText: '章节计划' })
  const planBefore = await bridgeCallCount(page, 'UpdateChapterPlan')
  await planSection.getByTitle('编辑').click({ force: true })
  await page.getByPlaceholder('细纲计划内容...').fill('生命周期计划错误仍应保留。')
  await planSection.getByRole('button', { name: '保存' }).click()
  await waitForBridgeCallCountAfter(page, 'UpdateChapterPlan', planBefore)
  const alert = errorAlert(page, '保存计划失败')
  await expectVisible(alert, 'timeline plan lifecycle error callout')

  await planSection.getByRole('button', { name: '取消' }).click()
  await expectErrorPersistsAfter(
    async () => { await page.locator('section').filter({ hasText: '伏笔与指令' }).getByRole('button', { name: '新建' }).click() },
    alert,
    expectVisible,
    'timeline plan error after opening create entry form',
  )
  await page.locator('main').getByRole('button', { name: '取消' }).last().click()
  await expectErrorPersistsAfter(
    async () => { await planSection.getByTitle('编辑').click({ force: true }) },
    alert,
    expectVisible,
    'timeline plan error after reopening plan edit form',
  )
  await planSection.getByRole('button', { name: '取消' }).click()
  await expectErrorPersistsAfter(
    async () => { await clickCardAction(page.locator('main'), '桌面水痕', '编辑') },
    alert,
    expectVisible,
    'timeline plan error after opening entry edit form',
  )
  await page.close()
}

async function verifyReaderPreferenceErrorLifecycle(context) {
  const {
    browser,
    url,
    consoleErrors,
    pageErrors,
    newAppPage,
    installClipboardSpy,
    sensitiveDiagnosticDetails,
    clickActivity,
    clickCardAction,
    waitForBridgeCallCountAfter,
    bridgeCallCount,
    errorAlert,
    expectVisible,
  } = context

  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      CreateReaderPerspective: lifecycleFault('READER_CREATE_LIFECYCLE_FAILED', '创建读者视角失败：Bearer reader-create-token-abcdefghijklmnopqrstuvwxyz', sensitiveDiagnosticDetails()),
      CreatePreference: lifecycleFault('PREFERENCE_CREATE_LIFECYCLE_FAILED', '创建偏好失败：Bearer preference-create-token-abcdefghijklmnopqrstuvwxyz', sensitiveDiagnosticDetails()),
    },
  }, undefined, 'reader-preference-error-lifecycle')
  await installClipboardSpy(page)
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'workspace title before reader/preference lifecycle review')

  await clickActivity(page, '读者视角')
  const readerBefore = await bridgeCallCount(page, 'CreateReaderPerspective')
  await page.locator('main').getByRole('button', { name: '新建' }).click()
  await page.getByPlaceholder('读者知道/想知道/误以为的事情').fill('生命周期读者视角错误仍应保留。')
  await page.getByPlaceholder('真实情况是什么').fill('旧城门还没有安全。')
  await page.locator('main').getByRole('button', { name: '创建' }).last().click()
  await waitForBridgeCallCountAfter(page, 'CreateReaderPerspective', readerBefore)
  const readerAlert = errorAlert(page, '创建读者视角失败')
  await expectVisible(readerAlert, 'reader lifecycle error callout')
  await page.locator('main').getByRole('button', { name: '取消' }).last().click()
  await expectErrorPersistsAfter(
    async () => { await page.locator('main').getByRole('button', { name: '新建' }).click() },
    readerAlert,
    expectVisible,
    'reader create error after reopening create form',
  )
  await page.locator('main').getByRole('button', { name: '取消' }).last().click()
  await expectErrorPersistsAfter(
    async () => { await clickCardAction(page.locator('main'), '读者知道林岚正在调查旧城门', '编辑') },
    readerAlert,
    expectVisible,
    'reader create error after opening edit form',
  )

  await clickActivity(page, '偏好')
  const preferenceBefore = await bridgeCallCount(page, 'CreatePreference')
  await page.locator('section').filter({ hasText: '全局偏好' }).getByRole('button', { name: '添加' }).click()
  await page.getByPlaceholder('风格、对话、世界观...').fill('生命周期')
  await page.getByPlaceholder('偏好内容').fill('生命周期偏好错误仍应保留。')
  await page.locator('main').getByRole('button', { name: '创建' }).last().click()
  await waitForBridgeCallCountAfter(page, 'CreatePreference', preferenceBefore)
  const preferenceAlert = errorAlert(page, '创建偏好失败')
  await expectVisible(preferenceAlert, 'preference lifecycle error callout')
  await page.locator('main').getByRole('button', { name: '取消' }).last().click()
  await expectErrorPersistsAfter(
    async () => { await page.locator('section').filter({ hasText: '全局偏好' }).getByRole('button', { name: '添加' }).click() },
    preferenceAlert,
    expectVisible,
    'preference create error after reopening create form',
  )
  await page.locator('main').getByRole('button', { name: '取消' }).last().click()
  await expectErrorPersistsAfter(
    async () => { await clickCardAction(page.locator('main'), '保持受限视角', '编辑') },
    preferenceAlert,
    expectVisible,
    'preference create error after opening edit form',
  )
  await page.close()
}

// O10 验收场景：审批提交失败时卡片必须显式报错、保留反馈、给出重试与"结束本轮"出路，
// 等待超时后也必须提供出路，且结束后卡片离开"等待审批"状态而不是永久挂起。
// 聊天故障只能在建页时注入（faults 队列不可运行时新增），所以按 error-feedback 惯例各开独立页面。
async function verifyApprovalSubmitErrorRecovery(context) {
  const {
    browser,
    url,
    consoleErrors,
    pageErrors,
    newAppPage,
    outputDir,
    expectVisible,
    expectHidden,
    bridgeCallCount,
    errorAlert,
  } = context

  const chatInput = (page) => page.getByPlaceholder('输入消息，按 / 调用技能...')
  const approvalCard = (page) => page.locator('.tool-card.awaiting-approval')
  const feedbackBox = (page) => approvalCard(page).locator('textarea.approval-feedback')
  const sendApprovalPrompt = async (page) => {
    const input = chatInput(page)
    await input.fill('等待审批：确认删除角色林岚')
    await input.press('Enter')
    await expectVisible(approvalCard(page), 'approval card after mock awaiting_approval event')
  }

  // 第一页：ApproveTool 一次性故障 → 报错 + 反馈保留 → 重试成功 → 恢复干净审批态。
  const failurePage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      ApproveTool: {
        mode: 'storage',
        code: 'APPROVAL_SUBMIT_FAILED',
        message: '模拟审批提交失败：审批记录写入故障',
        retryable: true,
      },
    },
  }, undefined, 'approval-submit-failure')
  await failurePage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(failurePage.getByText('全局回归小说'), 'workspace title before approval failure scenario')
  await sendApprovalPrompt(failurePage)

  await feedbackBox(failurePage).fill('这条线索后面还要用，先留着，仅移出本章')
  const approveBefore = await bridgeCallCount(failurePage, 'ApproveTool')
  await approvalCard(failurePage).getByRole('button', { name: '批准' }).click()
  const approvalAlert = errorAlert(failurePage, '模拟审批提交失败：审批记录写入故障')
  await expectVisible(approvalAlert, 'approval submit failure error message')
  await expectVisible(approvalCard(failurePage).getByRole('button', { name: '重试批准' }), 'retry button after approval failure')
  await expectVisible(approvalCard(failurePage).getByRole('button', { name: '结束本轮' }), 'end-turn escape hatch after approval failure')
  assert.equal(await feedbackBox(failurePage).inputValue(), '这条线索后面还要用，先留着，仅移出本章', 'feedback text must survive a failed approval submit')
  assert.equal(await bridgeCallCount(failurePage, 'ApproveTool'), approveBefore + 1, 'exactly one ApproveTool call before retry')
  await failurePage.screenshot({ path: path.join(outputDir, 'app-approval-submit-failure.png'), fullPage: true })

  await approvalCard(failurePage).getByRole('button', { name: '重试批准' }).click()
  // 等待审批徽章回归 = 重试的 promise 已结算并完成重渲染，避免读到重渲染前的输入框。
  await failurePage.waitForFunction(() => {
    const badge = document.querySelector('.tool-card.awaiting-approval .tool-badge')
    return badge !== null && badge.textContent?.includes('等待审批')
  }, undefined, { timeout: 12_000 })
  await expectHidden(approvalAlert, 'approval failure error cleared after successful retry')
  assert.equal(await feedbackBox(failurePage).inputValue(), '', 'feedback cleared after successful approval')
  await expectVisible(approvalCard(failurePage).getByText('等待审批', { exact: true }), 'approval card still awaiting after successful submit')
  await failurePage.close()

  // 第二页：ApproveTool 永久挂起 → 软超时提示 + 结束本轮 → 卡片离开等待审批，不再卡死。
  const hangPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      ApproveTool: { mode: 'timeout', once: false },
    },
  }, undefined, 'approval-submit-hang')
  await hangPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(hangPage.getByText('全局回归小说'), 'workspace title before approval hang scenario')
  await sendApprovalPrompt(hangPage)

  await approvalCard(hangPage).getByRole('button', { name: '批准' }).click()
  await expectVisible(approvalCard(hangPage).getByText('提交中', { exact: true }), 'submitting badge while approval hangs')
  const slowHint = hangPage.getByRole('status').filter({ hasText: '提交已超过 6 秒没有回应' })
  await expectVisible(slowHint, 'soft timeout hint after 6 seconds without approval response')
  await expectVisible(approvalCard(hangPage).getByRole('button', { name: '结束本轮' }), 'end-turn escape hatch on approval hang')

  await approvalCard(hangPage).getByRole('button', { name: '结束本轮' }).click()
  const failedCard = hangPage.locator('.tool-card.failed').filter({ hasText: '确认删除角色' })
  await expectVisible(failedCard, 'approval card switched to failed after ending the turn')
  await expectVisible(failedCard.getByText('已结束本轮，审批未提交'), 'abandon reason surfaced on the failed card')
  await expectHidden(approvalCard(hangPage), 'no awaiting_approval card remains after ending the turn')
  await hangPage.screenshot({ path: path.join(outputDir, 'app-approval-end-turn.png'), fullPage: true })
  await hangPage.close()
}

function lifecycleFault(code, message, details) {
  return {
    mode: 'storage',
    code,
    message,
    details,
    retryable: true,
  }
}

async function expectErrorPersistsAfter(action, alert, expectVisible, description, page = null) {
  await action()
  if (page) {
    await page.waitForTimeout(75)
  }
  assert.equal(await alert.isVisible(), true, `Expected error to persist: ${description}`)
  await expectVisible(alert, description)
}

async function verifyLegacySaveExportErrorFeedback(context) {
  const {
    browser,
    url,
    consoleErrors,
    pageErrors,
    newAppPage,
    installClipboardSpy,
    sensitiveDiagnosticDetails,
    clickActivity,
    clickCardAction,
    waitForBridgeCall,
    waitForBridgeCallArg,
    waitForBridgeCallCountAfter,
    bridgeCallCount,
    errorAlert,
    expectVisible,
    assertNoSensitiveDiagnosticsVisible,
    assertCopyableDiagnostic,
    ensureChapterBlockExpanded,
    chapterButton,
    replaceEditorText,
    shortcutKey,
  } = context
  const exportPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      ExportNovel: {
        mode: 'storage',
        code: 'EXPORT_NOVEL_FAILED',
        message: '导出失败：Bearer export-error-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
    },
  }, undefined, 'export-error')
  await installClipboardSpy(exportPage)
  await exportPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(exportPage.getByText('全局回归小说'), 'workspace title before export failure')
  await clickActivity(exportPage, '章节')
  await exportPage.getByRole('button', { name: '导出作品' }).click()
  await expectVisible(exportPage.getByRole('heading', { name: '导出作品' }), 'export dialog before failure')
  const exportBefore = await bridgeCallCount(exportPage, 'ExportNovel')
  await exportPage.getByRole('button', { name: /Markdown/ }).click()
  await exportPage.locator('.fixed').getByRole('button', { name: '导出' }).click()
  await waitForBridgeCallCountAfter(exportPage, 'ExportNovel', exportBefore)
  const exportAlert = errorAlert(exportPage, '导出失败')
  await expectVisible(exportAlert, 'export error callout')
  await assertNoSensitiveDiagnosticsVisible(exportPage)
  await assertCopyableDiagnostic(exportPage, exportAlert, 'ExportNovel')
  await exportPage.close()

  const chapterSavePage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    allowSaveContent: true,
    faults: {
      SaveContent: {
        mode: 'storage',
        code: 'CONTENT_SAVE_FAILED',
        message: '保存失败：Bearer content-save-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
        once: false,
      },
    },
  }, undefined, 'content-save-error')
  await installClipboardSpy(chapterSavePage)
  await chapterSavePage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(chapterSavePage.getByText('全局回归小说'), 'workspace title before content save failure')
  await clickActivity(chapterSavePage, '章节')
  await ensureChapterBlockExpanded(chapterSavePage)
  await chapterButton(chapterSavePage, '雨夜线索').click()
  await expectVisible(chapterSavePage.locator('.monaco-editor').first(), 'editor before content save failure')
  const contentSaveBefore = await bridgeCallCount(chapterSavePage, 'SaveContent')
  await replaceEditorText(chapterSavePage, '错误反馈保存正文。\n\nBearer should redact from copied details.')
  await chapterSavePage.keyboard.press(shortcutKey('S'))
  await waitForBridgeCallCountAfter(chapterSavePage, 'SaveContent', contentSaveBefore)
  const contentSaveAlert = errorAlert(chapterSavePage, '保存失败')
  await expectVisible(contentSaveAlert, 'content save error callout')
  await assertNoSensitiveDiagnosticsVisible(chapterSavePage)
  await assertCopyableDiagnostic(chapterSavePage, contentSaveAlert, 'SaveContent')
  await expectErrorPersistsAfter(
    async () => {
      await replaceEditorText(chapterSavePage, '错误反馈保存正文。\n\n继续编辑时，保存失败提示仍应保留。')
    },
    contentSaveAlert,
    expectVisible,
    'content save error after editing content',
    chapterSavePage,
  )
  await chapterSavePage.close()

  const skillEditPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    allowSaveContent: true,
    faults: {
      SaveContent: {
        mode: 'storage',
        code: 'SKILL_EDIT_SAVE_FAILED',
        message: '保存技能失败：Bearer skill-edit-save-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
    },
  }, undefined, 'skill-edit-save-error')
  await installClipboardSpy(skillEditPage)
  await skillEditPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(skillEditPage.getByText('全局回归小说'), 'workspace title before skill edit save failure')
  await clickActivity(skillEditPage, '技能')
  await clickCardAction(skillEditPage.locator('aside'), '节奏控制', '编辑技能')
  await waitForBridgeCallArg(skillEditPage, 'GetContent', 1, 'skills/节奏控制.md')
  await skillEditPage.getByPlaceholder('简要描述此技能的功能和触发时机').fill('错误反馈技能保存路径。')
  const skillSaveBefore = await bridgeCallCount(skillEditPage, 'SaveContent')
  await skillEditPage.locator('main').getByRole('button', { name: '保存' }).click()
  await waitForBridgeCallCountAfter(skillEditPage, 'SaveContent', skillSaveBefore)
  const skillEditAlert = errorAlert(skillEditPage, '保存技能失败')
  await expectVisible(skillEditAlert, 'skill edit save error callout')
  await assertNoSensitiveDiagnosticsVisible(skillEditPage)
  await assertCopyableDiagnostic(skillEditPage, skillEditAlert, 'SaveContent')
  await skillEditPage.close()

  const extractPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      ExtractStyle: {
        mode: 'error',
        code: 'LEGACY_STYLE_EXTRACT_FAILED',
        message: '提取失败：Bearer legacy-style-extract-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
    },
  }, undefined, 'legacy-style-extract-error')
  await installClipboardSpy(extractPage)
  await extractPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(extractPage.getByText('全局回归小说'), 'workspace title before legacy style extract failure')
  await clickActivity(extractPage, '技能')
  await extractPage.locator('aside').getByTitle('提取写作风格').click()
  await expectVisible(extractPage.getByRole('heading', { name: '提取写作风格' }), 'legacy style extract dialog')
  await extractPage.getByPlaceholder('粘贴要模仿的文字样本...').fill('她停在门边，没有解释雨声。')
  const extractBefore = await bridgeCallCount(extractPage, 'ExtractStyle')
  await extractPage.getByRole('button', { name: '开始分析' }).click()
  await waitForBridgeCallCountAfter(extractPage, 'ExtractStyle', extractBefore)
  const extractAlert = errorAlert(extractPage, '提取失败')
  await expectVisible(extractAlert, 'legacy style extract error callout')
  await assertNoSensitiveDiagnosticsVisible(extractPage)
  await assertCopyableDiagnostic(extractPage, extractAlert, 'ExtractStyle')
  await extractPage.locator('.fixed').getByRole('button', { name: '✕' }).click()
  await expectErrorPersistsAfter(
    async () => { await extractPage.locator('aside').getByTitle('提取写作风格').click() },
    extractAlert,
    expectVisible,
    'legacy style extract error after reopening dialog',
    extractPage,
  )
  await extractPage.close()

  const extractSavePage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    allowSaveContent: true,
    faults: {
      SaveContent: {
        mode: 'storage',
        code: 'LEGACY_STYLE_SAVE_FAILED',
        message: '保存技能失败：Bearer legacy-style-save-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
    },
  }, undefined, 'legacy-style-save-error')
  await installClipboardSpy(extractSavePage)
  await extractSavePage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(extractSavePage.getByText('全局回归小说'), 'workspace title before legacy style save failure')
  await clickActivity(extractSavePage, '技能')
  await extractSavePage.locator('aside').getByTitle('提取写作风格').click()
  await extractSavePage.getByPlaceholder('粘贴要模仿的文字样本...').fill('她停在门边，没有解释雨声。')
  await extractSavePage.getByRole('button', { name: '开始分析' }).click()
  await expectVisible(extractSavePage.getByRole('button', { name: '保存技能' }), 'legacy style save button before failure')
  const extractSaveBefore = await bridgeCallCount(extractSavePage, 'SaveContent')
  await extractSavePage.getByRole('button', { name: '保存技能' }).click()
  await waitForBridgeCallCountAfter(extractSavePage, 'SaveContent', extractSaveBefore)
  const extractSaveAlert = errorAlert(extractSavePage, '保存技能失败')
  await expectVisible(extractSaveAlert, 'legacy style save error callout')
  await assertNoSensitiveDiagnosticsVisible(extractSavePage)
  await assertCopyableDiagnostic(extractSavePage, extractSaveAlert, 'SaveContent')
  await extractSavePage.close()
}
