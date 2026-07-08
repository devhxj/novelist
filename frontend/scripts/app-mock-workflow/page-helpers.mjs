import assert from 'node:assert/strict'

export async function replaceEditorText(page, content) {
  const editor = page.locator('.monaco-editor').first()
  await expectVisible(editor, 'content editor')
  await page.waitForFunction(() => typeof window.__novelistEditor?.setValue === 'function', null, { timeout: 12_000 })
  await page.evaluate((content) => window.__novelistEditor.setValue(content), content)
}

export async function insertEditorText(page, content) {
  const editor = page.locator('.monaco-editor').first()
  await expectVisible(editor, 'content editor')
  await page.waitForFunction(() => typeof window.__novelistEditor?.insertText === 'function', null, { timeout: 12_000 })
  await page.evaluate((content) => window.__novelistEditor.insertText(content), content)
}

export async function assertEditorContains(page, expectedText) {
  await page.waitForFunction(
    (expectedText) => window.__novelistEditor?.getValue?.().includes(expectedText),
    expectedText,
    { timeout: 12_000 },
  ).catch((error) => {
    throw new Error(`Expected editor to contain: ${expectedText}`, { cause: error })
  })
}

export async function assertEditorNotContains(page, unexpectedText) {
  await page.waitForFunction(
    (unexpectedText) => !window.__novelistEditor?.getValue?.().includes(unexpectedText),
    unexpectedText,
    { timeout: 12_000 },
  ).catch((error) => {
    throw new Error(`Expected editor not to contain: ${unexpectedText}`, { cause: error })
  })
}

export function shortcutKey(key) {
  return `${process.platform === 'darwin' ? 'Meta' : 'Control'}+${key}`
}

export async function waitForSaveContent(page, path, expectedText) {
  await page.waitForFunction(
    ({ path, expectedText }) => {
      return window.__appMockState.calls.some((call) =>
        call.method === 'SaveContent' &&
        call.args[0]?.path === path &&
        String(call.args[0]?.content ?? '').includes(expectedText))
    },
    { path, expectedText },
    { timeout: 12_000 },
  )
}

export async function waitForSaveContentAfter(page, path, expectedText, previousCount) {
  await page.waitForFunction(
    ({ path, expectedText, previousCount }) => {
      const saveCalls = window.__appMockState.calls.filter((call) => call.method === 'SaveContent')
      return saveCalls.length > previousCount &&
        window.__appMockState.contentByPath[path] === saveCalls.at(-1)?.args[0]?.content &&
        String(window.__appMockState.contentByPath[path] ?? '').includes(expectedText)
    },
    { path, expectedText, previousCount },
    { timeout: 12_000 },
  )
}

export async function assertStoredContent(page, path, expectedContent) {
  const actual = await page.evaluate((path) => window.__appMockState.contentByPath[path], path)
  assert.equal(actual, expectedContent)
}

export async function assertBridgeCallCount(page, method, expectedCount) {
  const actual = await bridgeCallCount(page, method)
  assert.equal(actual, expectedCount, `Expected ${expectedCount} ${method} calls, got ${actual}.`)
}

export async function assertNoBridgeCallArgValue(page, method, unexpectedValue, message) {
  const found = await page.evaluate(
    ({ method, unexpectedValue }) => window.__appMockState.calls.some((call) =>
      call.method === method &&
      (call.args ?? []).some((arg) => JSON.stringify(arg) === JSON.stringify(unexpectedValue))),
    { method, unexpectedValue },
  )
  assert.equal(found, false, message)
}

export async function bridgeCallCount(page, method) {
  return await page.evaluate(
    (method) => window.__appMockState.calls.filter((call) => call.method === method).length,
    method,
  )
}

export function settingsDialog(page) {
  return page.locator('.fixed').filter({ hasText: '设置' }).last()
}

export async function assertSavedLLMConfig(page, expected) {
  const actual = await page.evaluate(() => window.__appMockState.savedLLMConfig?.providers?.[0] ?? null)
  assert(actual, 'Expected LLM config to be saved.')
  assert.equal(actual.key, expected.providerKey)
  assert.equal(actual.api_key, expected.apiKey)
  assert.equal(actual.endpoint_type, expected.endpointType)
}

export async function assertSavedEmbeddingConfig(page, expected) {
  const actual = await page.evaluate(() => window.__appMockState.savedEmbeddingConfig ?? null)
  assert(actual, 'Expected embedding config to be saved.')
  for (const [key, expectedValue] of Object.entries(expected)) {
    assert.deepEqual(actual[key], expectedValue, `Expected saved embedding ${key} to equal ${JSON.stringify(expectedValue)}.`)
  }
}

export async function assertNoSavedEmbeddingConfig(page) {
  const actual = await page.evaluate(() => window.__appMockState.savedEmbeddingConfig)
  assert.equal(actual, null, 'Expected embedding config not to be saved.')
}

export async function assertLastBridgeCallInput(page, method, expected) {
  const actual = await page.evaluate((method) => {
    const call = window.__appMockState.calls.filter((item) => item.method === method).at(-1)
    return call?.args?.[0] ?? null
  }, method)
  assert(actual, `Expected ${method} to be called.`)
  for (const [key, expectedValue] of Object.entries(expected)) {
    assert.deepEqual(actual[key], expectedValue, `Expected ${method}.${key} to equal ${JSON.stringify(expectedValue)}.`)
  }
}

export async function assertButtonDisabled(locator, description) {
  await locator.waitFor({ state: 'visible', timeout: 12_000 }).catch((error) => {
    throw new Error(`Expected visible button before disabled check: ${description}`, { cause: error })
  })
  const disabled = await locator.isDisabled()
  assert.equal(disabled, true, `Expected disabled button: ${description}.`)
}

export async function assertSettingsCallsUseMockCredentials(page) {
  const leakedLiveCredentialOrEndpoint = await page.evaluate(() => {
    const liveCredentialPatterns = [
      /sk-[A-Za-z0-9_-]{12,}/,
      /sk-proj-[A-Za-z0-9_-]{12,}/,
      /AIza[0-9A-Za-z_-]{12,}/,
      /xox[baprs]-[0-9A-Za-z-]{12,}/,
      /api\.openai\.com/i,
      /api\.anthropic\.com/i,
      /generativelanguage\.googleapis\.com/i,
      /dashscope\.aliyuncs\.com/i,
      /api\.siliconflow\.cn/i,
    ]
    return window.__appMockState.calls.some((call) =>
      liveCredentialPatterns.some((pattern) => pattern.test(JSON.stringify(call.args ?? []))))
  })
  assert.equal(leakedLiveCredentialOrEndpoint, false, 'Settings workflow must use mock credentials and non-live endpoints only.')
}

export async function assertSearchResultContainsRestrictedSourcePath(page) {
  const hasRestrictedPath = await page.evaluate(() =>
    window.__appMockState.calls
      .filter((call) => call.method === 'SearchAll')
      .some((call) => call.result?.some?.((item) => item.source_path === 'D:\\restricted\\reference-source.md')))
  assert.equal(hasRestrictedPath, true, 'Expected mocked search payload to include a restricted source path for leakage guardrail coverage.')
}

export async function expectVisible(locator, description) {
  await locator.waitFor({ state: 'visible', timeout: 12_000 }).catch((error) => {
    throw new Error(`Expected visible: ${description}`, { cause: error })
  })
}

export async function expectHidden(locator, description) {
  await locator.waitFor({ state: 'hidden', timeout: 12_000 }).catch((error) => {
    throw new Error(`Expected hidden: ${description}`, { cause: error })
  })
}

export async function assertDisabled(locator, description) {
  await locator.waitFor({ state: 'attached', timeout: 12_000 }).catch((error) => {
    throw new Error(`Expected attached before disabled check: ${description}`, { cause: error })
  })
  const disabled = await locator.isDisabled()
  assert.equal(disabled, true, `Expected disabled: ${description}`)
}

export async function waitForBridgeCallArg(page, method, argIndex, expectedValue) {
  await page.waitForFunction(
    ({ method, argIndex, expectedValue }) => {
      return window.__appMockState.calls.some((call) =>
        call.method === method && JSON.stringify(call.args[argIndex]) === JSON.stringify(expectedValue))
    },
    { method, argIndex, expectedValue },
    { timeout: 12_000 },
  )
}

export async function waitForBridgeCall(page, method) {
  await page.waitForFunction(
    (method) => window.__appMockState.calls.some((call) => call.method === method),
    method,
    { timeout: 12_000 },
  )
}

export async function waitForBridgeCallCountAfter(page, method, previousCount) {
  await page.waitForFunction(
    ({ method, previousCount }) =>
      window.__appMockState.calls.filter((call) => call.method === method).length > previousCount,
    { method, previousCount },
    { timeout: 12_000 },
  )
}

export async function assertActiveNovelId(page, expectedNovelId) {
  const actual = await page.evaluate(() => window.__appMockState.activeNovelId)
  assert.equal(actual, expectedNovelId)
}

export async function assertNovelDeleted(page, novelId) {
  const exists = await page.evaluate((novelId) =>
    window.__appMockState.novels.some((novel) => novel.id === novelId),
  novelId)
  assert.equal(exists, false, `Expected novel ${novelId} to be deleted.`)
}

export async function assertSelectedChapterPath(page, expectedPath) {
  const expectedTitle = expectedPath.endsWith('7.md')
    ? '新章验收-改名'
    : expectedPath.endsWith('2.md')
      ? '旧城门'
      : '雨夜线索'

  await page.waitForFunction(
    ({ expectedTitle }) => {
      return Array.from(document.querySelectorAll('aside button')).some((button) =>
        button.classList.contains('bg-primary/10') &&
        (button.textContent ?? '').includes(expectedTitle))
    },
    { expectedTitle },
    { timeout: 12_000 },
  ).catch((error) => {
    throw new Error(`Expected selected chapter for ${expectedPath}.`, { cause: error })
  })

  const activeClasses = await page.locator('aside').getByRole('button', { name: /第\d+章/ }).evaluateAll((buttons) =>
    buttons
      .map((button) => ({ text: button.textContent ?? '', className: button.getAttribute('class') ?? '' }))
      .filter((button) => button.className.includes('bg-primary/10')),
  )
  assert(activeClasses.some((button) => button.text.includes(expectedTitle)), `Expected selected chapter for ${expectedPath}.`)
}

export async function assertActiveTabTitle(page, expectedTitle) {
  const activeTabs = await page.locator('main').locator('div').evaluateAll((nodes) =>
    nodes
      .map((node) => ({ text: node.textContent ?? '', className: node.getAttribute('class') ?? '' }))
      .filter((node) => node.className.includes('border-t-blue-500')),
  )
  assert(activeTabs.some((tab) => tab.text.includes(expectedTitle)), `Expected active tab ${expectedTitle}.`)
}

export async function assertChapterTitle(page, novelId, chapterNumber, expectedTitle) {
  const actual = await page.evaluate(({ novelId, chapterNumber }) => {
    return window.__appMockState.chaptersByNovelId[String(novelId)]
      ?.find((chapter) => chapter.chapter_number === chapterNumber)
      ?.title ?? ''
  }, { novelId, chapterNumber })
  assert.equal(actual, expectedTitle)
}

export async function assertLastBinaryCall(page, method, expectedByteCount) {
  const actual = await page.evaluate((method) => {
    const call = window.__appMockState.calls.filter((item) => item.method === method).at(-1)
    const payload = call?.args.at(-1)
    return Array.isArray(payload) ? payload.length : 0
  }, method)
  assert.equal(actual, expectedByteCount, `Expected ${method} to receive ${expectedByteCount} bytes, got ${actual}.`)
}

export async function assertExportedNovels(page, expected) {
  const actual = await page.evaluate(() => window.__appMockState.exportedNovels)
  assert.deepEqual(actual, expected)
}

export async function assertSavedCover(page, expected) {
  const actual = await page.evaluate(() => window.__appMockState.savedCovers.at(-1))
  assert.deepEqual(actual, expected)
}

export async function assertSavedAvatar(page, expected) {
  const actual = await page.evaluate(() => window.__appMockState.savedAvatars.at(-1))
  assert.deepEqual(actual, expected)
}

export async function expectInputValue(locator, expectedValue) {
  const actual = await locator.inputValue()
  assert.equal(actual, expectedValue)
}

export async function expectSelectedValue(locator, expectedValue) {
  const actual = await locator.inputValue()
  assert.equal(actual, expectedValue)
}

export async function clickCardAction(root, cardText, actionTitle) {
  const card = root.locator('.group').filter({ hasText: cardText }).first()
  await expectVisible(card, `${cardText} card`)
  await card.getByTitle(actionTitle).click({ force: true })
}

export async function assertCreatedReferenceAnchor(page, expected) {
  const actual = await page.evaluate(() => window.__appMockState.createdReferenceAnchors.at(-1))
  assert.equal(actual?.title, expected.title)
  assert.equal(actual?.source_path, expected.sourcePath)
  assert.equal(actual?.source_kind, expected.sourceKind)
}

export async function assertOnlyTemporaryFixturePaths(page, allowedFixtureRoot) {
  const unexpectedAbsolutePaths = await page.evaluate((allowedFixtureRoot) => {
    const normalize = (value) => String(value).replaceAll('\\', '/')
    const allowedRoot = normalize(allowedFixtureRoot).replace(/\/+$/, '')
    const isAbsolutePath = (value) => /^[A-Za-z]:[\\/]/.test(value) || value.startsWith('/')
    const strings = []

    const collectStrings = (value) => {
      if (typeof value === 'string') {
        strings.push(value)
        return
      }
      if (Array.isArray(value)) {
        for (const item of value) collectStrings(item)
        return
      }
      if (value && typeof value === 'object') {
        for (const item of Object.values(value)) collectStrings(item)
      }
    }

    for (const call of window.__appMockState.calls) {
      collectStrings(call.args)
    }

    return strings.filter((value) => {
      if (!isAbsolutePath(value)) return false
      const normalized = normalize(value)
      return normalized !== allowedRoot && !normalized.startsWith(`${allowedRoot}/`)
    })
  }, allowedFixtureRoot)

  assert.deepEqual(
    unexpectedAbsolutePaths,
    [],
    `Expected absolute file path bridge arguments to stay under ${allowedFixtureRoot}.`,
  )
}

export async function dispatchNovelImportDrop(page, payload) {
  await page.evaluate((payload) => {
    const target = document.querySelector('[data-testid="novel-import-dropzone"]')
    if (!target) throw new Error('Novel import dropzone was not found.')

    const event = new Event('drop', { bubbles: true, cancelable: true })
    if (payload.kind === 'files') {
      const dataTransfer = new DataTransfer()
      for (const dropped of payload.files ?? []) {
        const file = new File(['mock import fixture'], dropped.name, { type: dropped.type ?? '' })
        Object.defineProperty(file, 'path', {
          configurable: true,
          enumerable: false,
          value: dropped.path,
        })
        dataTransfer.items.add(file)
      }
      Object.defineProperty(event, 'dataTransfer', { value: dataTransfer })
    } else if (payload.kind === 'url') {
      const data = {
        'text/plain': payload.url,
        'text/uri-list': payload.url,
      }
      Object.defineProperty(event, 'dataTransfer', {
        value: {
          files: [],
          items: [],
          getData(type) {
            return data[type] ?? ''
          },
        },
      })
    } else if (payload.kind === 'fileUriText') {
      const data = {
        'text/plain': payload.uri,
        'text/uri-list': '',
      }
      Object.defineProperty(event, 'dataTransfer', {
        value: {
          files: [],
          items: [],
          getData(type) {
            return data[type] ?? ''
          },
        },
      })
    } else if (payload.kind === 'directory') {
      Object.defineProperty(event, 'dataTransfer', {
        value: {
          files: [],
          items: [
            {
              kind: 'file',
              webkitGetAsEntry() {
                return { isDirectory: true }
              },
            },
          ],
          getData() {
            return ''
          },
        },
      })
    } else {
      Object.defineProperty(event, 'dataTransfer', {
        value: {
          files: [],
          items: [],
          getData() {
            return ''
          },
        },
      })
    }

    target.dispatchEvent(event)
  }, payload)
}
