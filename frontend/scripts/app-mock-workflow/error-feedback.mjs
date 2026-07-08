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
    expectVisible,
    assertNoSensitiveDiagnosticsVisible,
    assertCopyableDiagnostic,
    ensureChapterBlockExpanded,
    chapterButton,
    dispatchNovelImportDrop,
  } = context

  await verifyStyleSampleLibraryErrorFeedback(context)
  await verifyMetadataCrudErrorFeedback(context)
  await verifyLegacySaveExportErrorFeedback(context)

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
  await page.getByRole('button', { name: '完成' }).click()

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

  const patternBefore = await bridgeCallCount(page, 'StartNarrativePatternExtraction')
  await clickActivity(page, '叙事模式')
  await expectVisible(page.getByRole('heading', { name: '叙事模式' }), 'narrative pattern heading')
  await page.getByRole('button', { name: '开始抽取' }).click()
  await waitForBridgeCallCountAfter(page, 'StartNarrativePatternExtraction', patternBefore)
  const patternAlert = errorAlert(page, '叙事模式抽取失败')
  await expectVisible(patternAlert, 'narrative pattern error callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, patternAlert, 'StartNarrativePatternExtraction')

  await clickActivity(page, '风格素材')
  await expectVisible(page.getByRole('heading', { name: /风格素材/ }), 'style sample heading')

  const createStyleSampleBefore = await bridgeCallCount(page, 'CreateStyleSample')
  await page.getByRole('button', { name: '新建样本' }).click()
  await page.locator('form').getByLabel('样本名称').fill('错误反馈样本')
  await page.locator('form').getByLabel('样本内容').fill('她停了一下，没有把话说完。')
  await page.locator('form').getByLabel('标签').fill('错误反馈;样本')
  await page.getByRole('button', { name: '保存样本' }).click()
  await waitForBridgeCallCountAfter(page, 'CreateStyleSample', createStyleSampleBefore)
  const createStyleSampleAlert = errorAlert(page, '保存风格样本失败')
  await expectVisible(createStyleSampleAlert, 'create style sample error callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, createStyleSampleAlert, 'CreateStyleSample')

  const updateStyleSampleBefore = await bridgeCallCount(page, 'UpdateStyleSample')
  await page.getByRole('button', { name: '编辑 全局雨夜节奏' }).click()
  await waitForBridgeCall(page, 'GetStyleSample')
  await page.locator('form').getByLabel('样本名称').fill('全局雨夜节奏错误修订')
  await page.getByRole('button', { name: '保存样本' }).click()
  await waitForBridgeCallCountAfter(page, 'UpdateStyleSample', updateStyleSampleBefore)
  const updateStyleSampleAlert = errorAlert(page, '保存风格样本失败')
  await expectVisible(updateStyleSampleAlert, 'update style sample error callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, updateStyleSampleAlert, 'UpdateStyleSample')

  const deleteStyleSampleBefore = await bridgeCallCount(page, 'DeleteStyleSample')
  await page.getByRole('button', { name: '删除 全局雨夜节奏' }).click()
  await waitForBridgeCallCountAfter(page, 'DeleteStyleSample', deleteStyleSampleBefore)
  const deleteStyleSampleAlert = errorAlert(page, '删除风格样本失败')
  await expectVisible(deleteStyleSampleAlert, 'delete style sample error callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, deleteStyleSampleAlert, 'DeleteStyleSample')

  const styleBefore = await bridgeCallCount(page, 'ExtractStyleSkillFromSamples')
  await page.getByRole('checkbox', { name: '选择样本 全局雨夜节奏' }).check()
  await page.getByLabel('技能名称').fill('错误风格技能')
  await page.getByRole('button', { name: '开始抽取' }).click()
  await waitForBridgeCallCountAfter(page, 'ExtractStyleSkillFromSamples', styleBefore)
  const styleAlert = errorAlert(page, '风格技能抽取失败')
  await expectVisible(styleAlert, 'style extraction error callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, styleAlert, 'ExtractStyleSkillFromSamples')
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
  await metadataPage.getByPlaceholder('下一章计划内容...').fill('错误反馈章节计划需要被诊断遮蔽。')
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

async function verifyStyleSampleLibraryErrorFeedback(context) {
  const {
    browser,
    url,
    consoleErrors,
    pageErrors,
    newAppPage,
    installClipboardSpy,
    sensitiveDiagnosticDetails,
    clickActivity,
    waitForBridgeCall,
    waitForBridgeCallCountAfter,
    bridgeCallCount,
    errorAlert,
    expectVisible,
    assertNoSensitiveDiagnosticsVisible,
    assertCopyableDiagnostic,
  } = context
  const searchPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      SearchStyleSamples: {
        mode: 'storage',
        code: 'STYLE_SAMPLE_SEARCH_FAILED',
        message: '加载风格素材失败：Bearer style-sample-search-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
        once: false,
      },
    },
  }, undefined, 'style-sample-search-error')
  await installClipboardSpy(searchPage)
  await searchPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(searchPage.getByText('全局回归小说'), 'workspace title before style sample search failure')
  await clickActivity(searchPage, '风格素材')
  await waitForBridgeCall(searchPage, 'SearchStyleSamples')
  const searchAlert = errorAlert(searchPage, '加载风格素材失败')
  await expectVisible(searchAlert, 'style sample search error callout')
  await assertNoSensitiveDiagnosticsVisible(searchPage)
  await assertCopyableDiagnostic(searchPage, searchAlert, 'SearchStyleSamples')
  await searchPage.close()

  const detailPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      GetStyleSample: {
        mode: 'storage',
        code: 'STYLE_SAMPLE_DETAIL_FAILED',
        message: '加载样本详情失败：Bearer style-sample-detail-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
        once: false,
      },
    },
  }, undefined, 'style-sample-detail-error')
  await installClipboardSpy(detailPage)
  await detailPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(detailPage.getByText('全局回归小说'), 'workspace title before style sample detail failure')
  await clickActivity(detailPage, '风格素材')
  await expectVisible(detailPage.getByText('全局雨夜节奏').first(), 'style sample card before detail failure')
  const detailBefore = await bridgeCallCount(detailPage, 'GetStyleSample')
  await detailPage.getByRole('button', { name: '查看样本 全局雨夜节奏' }).click()
  await waitForBridgeCallCountAfter(detailPage, 'GetStyleSample', detailBefore)
  const detailAlert = errorAlert(detailPage, '加载样本详情失败')
  await expectVisible(detailAlert, 'style sample detail error callout')
  await assertNoSensitiveDiagnosticsVisible(detailPage)
  await assertCopyableDiagnostic(detailPage, detailAlert, 'GetStyleSample')
  await detailPage.close()
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

export async function verifyReferenceErrorFeedbackWorkflow(context) {
  const {
    browser,
    url,
    consoleErrors,
    pageErrors,
    newAppPage,
    installClipboardSpy,
    sensitiveDiagnosticDetails,
    bridgeCallCount,
    waitForBridgeCallCountAfter,
    errorAlert,
    expectVisible,
    assertNoSensitiveDiagnosticsVisible,
    assertCopyableDiagnostic,
    assertBridgeCallCount,
  } = context

  const details = sensitiveDiagnosticDetails()
  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      CreateReferenceAnchor: {
        mode: 'storage',
        code: 'REFERENCE_ANCHOR_CREATE_FAILED',
        message: '参考锚点创建失败：Bearer reference-create-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      RebuildReferenceAnchor: {
        mode: 'storage',
        code: 'REFERENCE_ANCHOR_REBUILD_FAILED',
        message: '锚点重建失败：Bearer reference-rebuild-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      SearchReferenceMaterials: {
        mode: 'storage',
        code: 'REFERENCE_MATERIAL_SEARCH_FAILED',
        message: '参考材料搜索失败：Bearer reference-search-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      GenerateReferenceChapterBlueprint: {
        mode: 'storage',
        code: 'REFERENCE_BLUEPRINT_GENERATE_FAILED',
        message: '章节蓝图生成失败：Bearer reference-blueprint-generate-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      ReviewReferenceChapterBlueprint: {
        mode: 'storage',
        code: 'REFERENCE_BLUEPRINT_REVIEW_FAILED',
        message: '蓝图评审失败：Bearer reference-blueprint-review-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      ApproveReferenceChapterBlueprint: {
        mode: 'storage',
        code: 'REFERENCE_BLUEPRINT_APPROVE_FAILED',
        message: '蓝图批准失败：Bearer reference-blueprint-approve-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      BindReferenceBlueprintMaterials: {
        mode: 'storage',
        code: 'REFERENCE_BLUEPRINT_BIND_FAILED',
        message: '蓝图材料绑定失败：Bearer reference-blueprint-bind-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
    },
  }, undefined, 'reference-error-feedback')
  await installClipboardSpy(page)
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'reference error workspace')
  await page.getByTitle('参考锚定').click()
  await expectVisible(page.getByRole('heading', { name: /参考锚定/ }), 'reference error heading')

  await page.getByPlaceholder('参考书名').fill('错误反馈参考')
  await page.getByLabel('本地路径').fill('D:\\books\\reference-error.md')
  const createBefore = await bridgeCallCount(page, 'CreateReferenceAnchor')
  await page.getByTestId('reference-import-panel').getByRole('button', { name: '创建' }).click()
  await waitForBridgeCallCountAfter(page, 'CreateReferenceAnchor', createBefore)
  const createAlert = errorAlert(page, '参考锚点创建失败')
  await expectVisible(createAlert, 'reference create failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, createAlert, 'CreateReferenceAnchor')

  const rebuildBefore = await bridgeCallCount(page, 'RebuildReferenceAnchor')
  await page.getByTitle('重建').first().click()
  await waitForBridgeCallCountAfter(page, 'RebuildReferenceAnchor', rebuildBefore)
  const rebuildAlert = errorAlert(page, '锚点重建失败')
  await expectVisible(rebuildAlert, 'reference rebuild failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, rebuildAlert, 'RebuildReferenceAnchor')

  const searchBefore = await bridgeCallCount(page, 'SearchReferenceMaterials')
  await page.getByRole('button', { name: /浏览 全局雨夜参考 的材料/ }).click()
  await waitForBridgeCallCountAfter(page, 'SearchReferenceMaterials', searchBefore)
  const searchAlert = errorAlert(page, '参考材料搜索失败')
  await expectVisible(searchAlert, 'reference material search failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, searchAlert, 'SearchReferenceMaterials')

  await page.getByRole('button', { name: /打开高级模式/ }).click()
  const blueprintPanel = page.getByTestId('reference-blueprint-panel')
  await blueprintPanel.getByLabel('章节号').fill('1')
  await blueprintPanel.getByLabel('标题').fill('错误反馈蓝图')
  const generateBefore = await bridgeCallCount(page, 'GenerateReferenceChapterBlueprint')
  await blueprintPanel.getByRole('button', { name: /生成蓝图/ }).click()
  await waitForBridgeCallCountAfter(page, 'GenerateReferenceChapterBlueprint', generateBefore)
  const generateAlert = errorAlert(page, '章节蓝图生成失败')
  await expectVisible(generateAlert, 'reference blueprint generate failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, generateAlert, 'GenerateReferenceChapterBlueprint')

  await page.evaluate(() => { window.__appMockState.clearFaultQueue('GenerateReferenceChapterBlueprint') })
  const generateSuccessBefore = await bridgeCallCount(page, 'GenerateReferenceChapterBlueprint')
  await blueprintPanel.getByRole('button', { name: /生成蓝图/ }).click()
  await waitForBridgeCallCountAfter(page, 'GenerateReferenceChapterBlueprint', generateSuccessBefore)
  await expectVisible(page.getByText('章节蓝图已生成'), 'reference blueprint generated after clearing fault')

  const detail = page.getByTestId('reference-blueprint-detail')
  const reviewBefore = await bridgeCallCount(page, 'ReviewReferenceChapterBlueprint')
  await detail.getByRole('button', { name: /评审/ }).click()
  await waitForBridgeCallCountAfter(page, 'ReviewReferenceChapterBlueprint', reviewBefore)
  const reviewAlert = errorAlert(page, '蓝图评审失败')
  await expectVisible(reviewAlert, 'reference blueprint review failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, reviewAlert, 'ReviewReferenceChapterBlueprint')

  await page.evaluate(() => { window.__appMockState.clearFaultQueue('ReviewReferenceChapterBlueprint') })
  const reviewSuccessBefore = await bridgeCallCount(page, 'ReviewReferenceChapterBlueprint')
  await detail.getByRole('button', { name: /评审/ }).click()
  await waitForBridgeCallCountAfter(page, 'ReviewReferenceChapterBlueprint', reviewSuccessBefore)
  await expectVisible(page.getByText('蓝图评审已完成'), 'reference blueprint reviewed after clearing fault')

  const approveBefore = await bridgeCallCount(page, 'ApproveReferenceChapterBlueprint')
  await detail.getByRole('button', { name: /批准/ }).click()
  await waitForBridgeCallCountAfter(page, 'ApproveReferenceChapterBlueprint', approveBefore)
  const approveAlert = errorAlert(page, '蓝图批准失败')
  await expectVisible(approveAlert, 'reference blueprint approve failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, approveAlert, 'ApproveReferenceChapterBlueprint')

  await page.evaluate(() => { window.__appMockState.clearFaultQueue('ApproveReferenceChapterBlueprint') })
  const approveSuccessBefore = await bridgeCallCount(page, 'ApproveReferenceChapterBlueprint')
  await detail.getByRole('button', { name: /批准/ }).click()
  await waitForBridgeCallCountAfter(page, 'ApproveReferenceChapterBlueprint', approveSuccessBefore)
  await expectVisible(page.getByText('蓝图已批准'), 'reference blueprint approved after clearing fault')

  const bindBefore = await bridgeCallCount(page, 'BindReferenceBlueprintMaterials')
  await detail.getByRole('button', { name: /绑定/ }).click()
  await waitForBridgeCallCountAfter(page, 'BindReferenceBlueprintMaterials', bindBefore)
  const bindAlert = errorAlert(page, '蓝图材料绑定失败')
  await expectVisible(bindAlert, 'reference blueprint bind failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, bindAlert, 'BindReferenceBlueprintMaterials')

  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
  await page.close()
}
