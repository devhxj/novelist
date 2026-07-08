import fs from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { installConfigurableAppMockBridge } from './mock-bridge.mjs'
import { artifactRunName, parseRunConfig, sanitizeArtifactName } from './runtime.mjs'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
export const frontendRoot = path.resolve(__dirname, '..', '..')
export const repoRoot = path.resolve(frontendRoot, '..')
export const runConfig = parseRunConfig(process.argv.slice(2))
export const phaseOutputRoot = path.join(repoRoot, 'output', 'playwright', runConfig.phase)
export const outputDir = path.join(phaseOutputRoot, artifactRunName(runConfig))
export const diagnostics = {
  consoleErrors: [],
  consoleWarnings: [],
  pageErrors: [],
  failedRequests: [],
}
const openPages = new Set()
let pageSequence = 0

export async function newAppPage(
  browser,
  consoleErrors,
  pageErrors,
  bridgeOptions,
  viewport = { width: 1440, height: 1100 },
  pageLabel = 'page',
) {
  const context = await browser.newContext({ viewport })
  await context.tracing.start({ screenshots: true, snapshots: true, sources: true })

  const page = await context.newPage()
  const artifactLabel = `${String(++pageSequence).padStart(2, '0')}-${sanitizeArtifactName(pageLabel)}`
  openPages.add(page)
  page.setDefaultTimeout(runConfig.suite === 'stress' ? 60_000 : 12_000)
  page.on('console', (message) => {
    if (message.type() === 'error') {
      const text = message.text()
      if (!isIgnorableDevServerConsoleError(text)) {
        consoleErrors.push(text)
      }
    } else if (message.type() === 'warning') {
      diagnostics.consoleWarnings.push(message.text())
    }
  })
  page.on('pageerror', (error) => pageErrors.push(error.message))
  page.on('requestfailed', (request) => {
    if (!isIgnorableRequestFailure(request)) {
      diagnostics.failedRequests.push(`${request.method()} ${request.url()} ${request.failure()?.errorText ?? 'request failed'}`)
    }
  })
  if (bridgeOptions) {
    await page.addInitScript(installConfigurableAppMockBridge, bridgeOptions)
  }

  const originalClose = page.close.bind(page)
  page.close = async (options) => {
    if (page.isClosed()) {
      openPages.delete(page)
      return
    }
    try {
      await writePageDiagnostics(page, artifactLabel)
      await context.tracing.stop({ path: path.join(outputDir, 'traces', `${artifactLabel}.zip`) })
      await originalClose(options)
      await context.close()
    } finally {
      openPages.delete(page)
    }
  }

  return page
}

function isIgnorableDevServerConsoleError(text) {
  return /^WebSocket connection to 'ws:\/\/127\.0\.0\.1:\d+\/\?token=[^']+' failed: Error in connection establishment: net::ERR_NO_BUFFER_SPACE$/.test(text)
}

function isIgnorableRequestFailure(request) {
  const url = request.url()
  return url.startsWith('ws://127.0.0.1:') || url.includes('/@vite/client')
}

async function writePageDiagnostics(page, artifactLabel) {
  const bridgeStates = await page.evaluate(() => {
    const states = []
    if (window.__appMockState?.calls) {
      states.push({
        name: 'app',
        calls: window.__appMockState.calls,
        appliedFaults: window.__appMockState.appliedFaults ?? [],
      })
    }
    return states
  }).catch(() => [])

  for (const state of bridgeStates) {
    await fs.writeFile(
      path.join(outputDir, 'bridge-calls', `${artifactLabel}-${state.name}.json`),
      `${JSON.stringify({ calls: state.calls, appliedFaults: state.appliedFaults }, null, 2)}\n`,
      'utf8',
    )
  }
}

export function logStep(message) {
  console.log(`[app mock:${runConfig.suite}:${runConfig.target}] ${message}`)
}

export async function writeRunDiagnostics() {
  await fs.writeFile(
    path.join(outputDir, 'diagnostics.json'),
    `${JSON.stringify({
      suite: runConfig.suite,
      target: runConfig.target,
      grep: runConfig.grep,
      phase: runConfig.phase,
      artifactDirectory: path.relative(repoRoot, outputDir),
      consoleErrors: diagnostics.consoleErrors,
      consoleWarnings: diagnostics.consoleWarnings,
      pageErrors: diagnostics.pageErrors,
      failedRequests: diagnostics.failedRequests,
    }, null, 2)}\n`,
    'utf8',
  )
}

export async function closeOpenPages() {
  const pages = [...openPages]
  for (const page of pages) {
    await page.close().catch((error) => {
      diagnostics.pageErrors.push(`Failed to close diagnostic page: ${error instanceof Error ? error.message : String(error)}`)
    })
  }
}
