export async function verifyReferenceErrorFeedbackWorkflow(context) {
  const {
    browser,
    url,
    consoleErrors,
    pageErrors,
    newAppPage,
    sensitiveDiagnosticDetails,
    clickActivity,
    bridgeCallCount,
    waitForBridgeCallCountAfter,
    errorAlert,
    expectVisible,
    assertNoSensitiveDiagnosticsVisible,
    assertBridgeCallCount,
  } = context

  const details = sensitiveDiagnosticDetails()
  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      RegisterReferenceMaterializationSource: {
        mode: 'storage',
        code: 'REFERENCE_SOURCE_REGISTER_FAILED',
        message: '无法添加参考书籍：Bearer reference-register-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      AnalyzeReferenceChapterSplit: {
        mode: 'storage',
        code: 'REFERENCE_CHAPTER_SPLIT_FAILED',
        message: '自动章节分析失败：Bearer reference-split-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      ConfirmReferenceChapterSplit: {
        mode: 'storage',
        code: 'REFERENCE_CHAPTER_SPLIT_CONFIRM_FAILED',
        message: '章节边界确认失败：Bearer reference-split-confirm-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      EnqueueReferenceMaterialization: {
        mode: 'storage',
        code: 'REFERENCE_MATERIALIZATION_ENQUEUE_FAILED',
        message: '材料化未能启动：Bearer reference-enqueue-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
    },
  }, undefined, 'reference-error-feedback')
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'reference error workspace')
  await clickActivity(page, '素材库')

  const referenceBooks = page.getByTestId('reference-book-sidebar')
  const corpusWorkspace = page.getByTestId('reference-corpus-workspace')
  await expectVisible(referenceBooks.getByRole('heading', { name: '参考书籍' }), 'reference books sidebar heading')

  await referenceBooks.getByRole('button', { name: '添加参考书籍' }).click()
  await referenceBooks.getByLabel('参考书标题').fill('错误反馈参考')
  await referenceBooks.getByLabel('参考书文件路径').fill('D:\\books\\reference-error.md')
  const registerBefore = await bridgeCallCount(page, 'RegisterReferenceMaterializationSource')
  await referenceBooks.getByRole('button', { name: /^添加参考书$/ }).click()
  await waitForBridgeCallCountAfter(page, 'RegisterReferenceMaterializationSource', registerBefore)
  const registerAlert = errorAlert(page, '无法添加参考书籍')
  await expectVisible(registerAlert, 'reference register failure alert')
  await assertNoSensitiveDiagnosticsVisible(page)

  await referenceBooks.getByRole('button', { name: '选择《全局雨夜参考》' }).click()
  await expectVisible(corpusWorkspace.getByRole('heading', { name: '全局雨夜参考' }), 'selected materialization source')

  const analyzeBefore = await bridgeCallCount(page, 'AnalyzeReferenceChapterSplit')
  await corpusWorkspace.getByRole('button', { name: '自动分析前 50K' }).click()
  await waitForBridgeCallCountAfter(page, 'AnalyzeReferenceChapterSplit', analyzeBefore)
  const analyzeAlert = errorAlert(page, '自动章节分析失败')
  await expectVisible(analyzeAlert, 'chapter split analysis failure alert')
  await assertNoSensitiveDiagnosticsVisible(page)

  await page.evaluate(() => { window.__appMockState.clearFaultQueue('AnalyzeReferenceChapterSplit') })
  const analyzeRetryBefore = await bridgeCallCount(page, 'AnalyzeReferenceChapterSplit')
  await corpusWorkspace.getByRole('button', { name: '自动分析前 50K' }).click()
  await waitForBridgeCallCountAfter(page, 'AnalyzeReferenceChapterSplit', analyzeRetryBefore)
  await expectVisible(corpusWorkspace.getByRole('button', { name: '确认章节边界' }), 'chapter split confirmation after retry')

  const confirmBefore = await bridgeCallCount(page, 'ConfirmReferenceChapterSplit')
  await corpusWorkspace.getByRole('button', { name: '确认章节边界' }).click()
  await waitForBridgeCallCountAfter(page, 'ConfirmReferenceChapterSplit', confirmBefore)
  const confirmAlert = errorAlert(page, '章节边界确认失败')
  await expectVisible(confirmAlert, 'chapter split confirmation failure alert')
  await assertNoSensitiveDiagnosticsVisible(page)

  await page.evaluate(() => { window.__appMockState.clearFaultQueue('ConfirmReferenceChapterSplit') })
  const confirmRetryBefore = await bridgeCallCount(page, 'ConfirmReferenceChapterSplit')
  await corpusWorkspace.getByRole('button', { name: '确认章节边界' }).click()
  await waitForBridgeCallCountAfter(page, 'ConfirmReferenceChapterSplit', confirmRetryBefore)
  await corpusWorkspace.getByRole('button', { name: '10' }).click()

  const enqueueBefore = await bridgeCallCount(page, 'EnqueueReferenceMaterialization')
  await corpusWorkspace.getByRole('button', { name: '启动材料化' }).click()
  await waitForBridgeCallCountAfter(page, 'EnqueueReferenceMaterialization', enqueueBefore)
  const enqueueAlert = errorAlert(page, '材料化未能启动')
  await expectVisible(enqueueAlert, 'materialization enqueue failure alert')
  await assertNoSensitiveDiagnosticsVisible(page)

  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
  await page.close()
}
