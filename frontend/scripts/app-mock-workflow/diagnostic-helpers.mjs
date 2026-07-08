import assert from 'node:assert/strict'
import { assertErrorCalloutAccessibility } from './accessibility-helpers.mjs'

export function sensitiveDiagnosticDetails() {
  return {
    api_key: 'sk-proj-errorabcdefghijklmnopqrstuvwxyz1234567890',
    authorization: 'Bearer detail-error-token-abcdefghijklmnopqrstuvwxyz',
    source_text: '敏感源文本'.repeat(300),
    nested: {
      password: 'open-sesame-secret',
      token: 'detail-token-abcdefghijklmnopqrstuvwxyz',
    },
  }
}

export async function installClipboardSpy(page) {
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: {
        async writeText(text) {
          window.__appMockClipboardText = String(text)
        },
      },
    })
  })
}

export function errorAlert(page, text) {
  return page.getByRole('alert').filter({ hasText: text }).first()
}

export async function assertCopyableDiagnostic(page, alert, expectedBridgeMethod) {
  await page.evaluate(() => { window.__appMockClipboardText = '' })
  await assertErrorCalloutAccessibility(page, alert, `${expectedBridgeMethod} error callout`)
  const copyButton = alert.getByRole('button', { name: '复制错误诊断' })
  await copyButton.click()
  await page.waitForFunction(() => typeof window.__appMockClipboardText === 'string' && window.__appMockClipboardText.length > 0)
  assert.equal(await alert.isVisible(), true, `${expectedBridgeMethod} error callout should remain visible after copy feedback rerender`)
  const copied = await page.evaluate(() => window.__appMockClipboardText)
  const diagnostic = JSON.parse(copied)

  assert.equal(diagnostic.bridge_method, expectedBridgeMethod)
  assert.equal(typeof diagnostic.timestamp, 'string')
  assert(copied.includes('[REDACTED]'), 'copied diagnostics should include redaction markers')
  assert(copied.includes('[REDACTED_SOURCE_TEXT]'), 'copied diagnostics should redact source text')
  assertNoSensitiveDiagnosticText(copied, `copied ${expectedBridgeMethod} diagnostics`)
}

export async function assertNoSensitiveDiagnosticsVisible(page) {
  const bodyText = await page.locator('body').textContent()
  assertNoSensitiveDiagnosticText(bodyText ?? '', 'visible error feedback')
}

export function assertNoSensitiveDiagnosticText(text, label) {
  const forbidden = [
    'sk-proj-errorabcdefghijklmnopqrstuvwxyz1234567890',
    'live-error-token-abcdefghijklmnopqrstuvwxyz',
    'open-error-token-abcdefghijklmnopqrstuvwxyz',
    'update-check-token-abcdefghijklmnopqrstuvwxyz',
    'update-settings-token-abcdefghijklmnopqrstuvwxyz',
    'novel-create-token-abcdefghijklmnopqrstuvwxyz',
    'novel-update-token-abcdefghijklmnopqrstuvwxyz',
    'novel-delete-token-abcdefghijklmnopqrstuvwxyz',
    'style-sample-search-token-abcdefghijklmnopqrstuvwxyz',
    'style-sample-detail-token-abcdefghijklmnopqrstuvwxyz',
    'style-sample-create-token-abcdefghijklmnopqrstuvwxyz',
    'style-sample-update-token-abcdefghijklmnopqrstuvwxyz',
    'style-sample-delete-token-abcdefghijklmnopqrstuvwxyz',
    'export-error-token-abcdefghijklmnopqrstuvwxyz',
    'content-save-token-abcdefghijklmnopqrstuvwxyz',
    'skill-edit-save-token-abcdefghijklmnopqrstuvwxyz',
    'legacy-style-extract-token-abcdefghijklmnopqrstuvwxyz',
    'legacy-style-save-token-abcdefghijklmnopqrstuvwxyz',
    'reader-create-token-abcdefghijklmnopqrstuvwxyz',
    'reader-quick-reveal-token-abcdefghijklmnopqrstuvwxyz',
    'reader-update-token-abcdefghijklmnopqrstuvwxyz',
    'reader-delete-token-abcdefghijklmnopqrstuvwxyz',
    'preference-create-token-abcdefghijklmnopqrstuvwxyz',
    'preference-update-token-abcdefghijklmnopqrstuvwxyz',
    'preference-delete-token-abcdefghijklmnopqrstuvwxyz',
    'story-arc-create-token-abcdefghijklmnopqrstuvwxyz',
    'story-arc-update-token-abcdefghijklmnopqrstuvwxyz',
    'story-arc-delete-token-abcdefghijklmnopqrstuvwxyz',
    'arc-node-create-token-abcdefghijklmnopqrstuvwxyz',
    'arc-node-quick-token-abcdefghijklmnopqrstuvwxyz',
    'arc-node-update-token-abcdefghijklmnopqrstuvwxyz',
    'arc-node-delete-token-abcdefghijklmnopqrstuvwxyz',
    'timeline-plan-token-abcdefghijklmnopqrstuvwxyz',
    'timeline-create-token-abcdefghijklmnopqrstuvwxyz',
    'timeline-quick-token-abcdefghijklmnopqrstuvwxyz',
    'timeline-update-token-abcdefghijklmnopqrstuvwxyz',
    'timeline-delete-token-abcdefghijklmnopqrstuvwxyz',
    'model-test-token-abcdefghijklmnopqrstuvwxyz',
    'model-save-token-abcdefghijklmnopqrstuvwxyz',
    'model-discovery-token-abcdefghijklmnopqrstuvwxyz',
    'embedding-test-token-abcdefghijklmnopqrstuvwxyz',
    'embedding-save-token-abcdefghijklmnopqrstuvwxyz',
    'git-author-save-token-abcdefghijklmnopqrstuvwxyz',
    'reference-create-token-abcdefghijklmnopqrstuvwxyz',
    'reference-rebuild-token-abcdefghijklmnopqrstuvwxyz',
    'reference-search-token-abcdefghijklmnopqrstuvwxyz',
    'reference-blueprint-generate-token-abcdefghijklmnopqrstuvwxyz',
    'reference-blueprint-review-token-abcdefghijklmnopqrstuvwxyz',
    'reference-blueprint-approve-token-abcdefghijklmnopqrstuvwxyz',
    'reference-blueprint-bind-token-abcdefghijklmnopqrstuvwxyz',
    'rename-error-token-abcdefghijklmnopqrstuvwxyz',
    'import-error-token-abcdefghijklmnopqrstuvwxyz',
    'style-error-token-abcdefghijklmnopqrstuvwxyz',
    'detail-error-token-abcdefghijklmnopqrstuvwxyz',
    'open-sesame-secret',
    '敏感源文本敏感源文本敏感源文本',
  ]
  for (const value of forbidden) {
    assert(!text.includes(value), `${label} leaked sensitive diagnostic text: ${value}`)
  }
}
