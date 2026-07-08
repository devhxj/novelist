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
    expectHidden,
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
        message: '语料重建失败：Bearer reference-rebuild-token-abcdefghijklmnopqrstuvwxyz',
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
  await page.getByTitle('素材库').click()
  await expectVisible(page.getByRole('heading', { name: '语料库管理' }), 'reference error heading')

  await page.getByPlaceholder('参考书名').fill('错误反馈参考')
  await page.getByLabel('本地路径').fill('D:\\books\\reference-error.md')
  const createBefore = await bridgeCallCount(page, 'CreateReferenceAnchor')
  await page.getByTestId('reference-import-panel').getByRole('button', { name: '创建' }).click()
  await waitForBridgeCallCountAfter(page, 'CreateReferenceAnchor', createBefore)
  const createAlert = errorAlert(page, '参考锚点创建失败')
  await expectVisible(createAlert, 'reference create failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, createAlert, 'CreateReferenceAnchor')
  await page.getByPlaceholder('可选').fill('错误反馈作者仍在编辑')
  await expectVisible(createAlert, 'reference create error persists after unrelated form edit')
  await createAlert.getByRole('button', { name: '关闭错误提示' }).click()
  await expectHidden(createAlert, 'reference create error clears only after explicit close')

  const rebuildBefore = await bridgeCallCount(page, 'RebuildReferenceAnchor')
  await page.getByTitle('重建语料').first().click()
  await waitForBridgeCallCountAfter(page, 'RebuildReferenceAnchor', rebuildBefore)
  const rebuildAlert = errorAlert(page, '语料重建失败')
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
  await blueprintPanel.getByLabel('章节号').fill('2')
  await expectVisible(generateAlert, 'reference blueprint generate error persists after unrelated blueprint form edit')
  await blueprintPanel.getByLabel('章节号').fill('1')

  await page.evaluate(() => { window.__appMockState.clearFaultQueue('GenerateReferenceChapterBlueprint') })
  const generateSuccessBefore = await bridgeCallCount(page, 'GenerateReferenceChapterBlueprint')
  await blueprintPanel.getByRole('button', { name: /生成蓝图/ }).click()
  await waitForBridgeCallCountAfter(page, 'GenerateReferenceChapterBlueprint', generateSuccessBefore)
  await expectHidden(generateAlert, 'reference blueprint generate error clears after successful retry')
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
  await expectHidden(reviewAlert, 'reference blueprint review error clears after successful retry')
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
  await expectHidden(approveAlert, 'reference blueprint approve error clears after successful retry')
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
