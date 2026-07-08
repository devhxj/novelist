import { spawn } from 'node:child_process'
import net from 'node:net'
import path from 'node:path'
import { chromium } from 'playwright'

export function startServer(port, target, frontendRoot) {
  if (target === 'dist') return startVitePreview(port, frontendRoot)
  return startVite(port, frontendRoot)
}

function startVite(port, frontendRoot) {
  const viteBin = path.join(frontendRoot, 'node_modules', 'vite', 'bin', 'vite.js')

  return spawn(process.execPath, [viteBin, '--host', '127.0.0.1', '--port', String(port)], {
    cwd: frontendRoot,
    env: {
      ...process.env,
      BROWSER: 'none',
      NODE_ENV: 'development',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  })
}

function startVitePreview(port, frontendRoot) {
  const viteBin = path.join(frontendRoot, 'node_modules', 'vite', 'bin', 'vite.js')

  return spawn(process.execPath, [viteBin, 'preview', '--host', '127.0.0.1', '--port', String(port), '--strictPort'], {
    cwd: frontendRoot,
    env: {
      ...process.env,
      BROWSER: 'none',
      NODE_ENV: 'production',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  })
}

export async function launchBrowser(logStep = console.log) {
  try {
    return await chromium.launch({ headless: true })
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    if (process.platform === 'win32' && message.includes("Executable doesn't exist")) {
      logStep('Playwright Chromium is not installed; falling back to Microsoft Edge')
      return chromium.launch({ channel: 'msedge', headless: true })
    }

    throw new Error(
      `${message}\nRun "npx playwright install chromium" from frontend/ if this machine has no browser fallback.`,
      { cause: error },
    )
  }
}

export async function waitForServer(url, child) {
  const logs = []
  child.stdout.on('data', (chunk) => logs.push(String(chunk)))
  child.stderr.on('data', (chunk) => logs.push(String(chunk)))

  const startedAt = Date.now()
  while (Date.now() - startedAt < 30_000) {
    if (child.exitCode !== null) {
      throw new Error(`Vite exited before becoming ready:\n${logs.join('')}`)
    }

    try {
      const response = await fetch(url)
      if (response.ok) return
    } catch {
      // keep polling
    }
    await delay(200)
  }

  throw new Error(`Timed out waiting for Vite:\n${logs.join('')}`)
}

export function stopProcess(child) {
  if (child.exitCode === null && child.signalCode === null) {
    child.kill()
  }
}

export async function getFreePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer()
    server.unref()
    server.on('error', reject)
    server.listen(0, '127.0.0.1', () => {
      const address = server.address()
      server.close(() => {
        if (typeof address === 'object' && address?.port) {
          resolve(address.port)
        } else {
          reject(new Error('Unable to reserve a local port.'))
        }
      })
    })
  })
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

export function parseRunConfig(args) {
  const config = {
    suite: 'full',
    target: 'vite',
    grep: '',
    stressSizeBytes: 10 * 1024 * 1024,
    stressChapterCount: 250,
  }

  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index]
    if (arg.startsWith('--suite=')) {
      config.suite = arg.slice('--suite='.length)
    } else if (arg.startsWith('--target=')) {
      config.target = arg.slice('--target='.length)
    } else if (arg.startsWith('--grep=')) {
      config.grep = arg.slice('--grep='.length)
    } else if (arg === '--grep') {
      config.grep = args[index + 1] ?? ''
      index += 1
    } else if (arg.startsWith('--stress-size-mb=')) {
      config.stressSizeBytes = Math.max(1, Number.parseInt(arg.slice('--stress-size-mb='.length), 10)) * 1024 * 1024
    } else if (arg.startsWith('--stress-chapters=')) {
      config.stressChapterCount = Math.max(1, Number.parseInt(arg.slice('--stress-chapters='.length), 10))
    }
  }

  if (!['smoke', 'full', 'stress', 'usability'].includes(config.suite)) {
    throw new Error(`Unsupported app mock suite: ${config.suite}`)
  }
  if (!['vite', 'dist'].includes(config.target)) {
    throw new Error(`Unsupported app mock target: ${config.target}`)
  }
  config.grep = normalizeGrepTag(config.grep)
  if (config.grep && !['@startup', '@diagnostics', '@surface', '@writing', '@reference-anchor', '@pattern', '@git', '@update', '@time', '@layout', '@error'].includes(config.grep)) {
    throw new Error(`Unsupported app mock grep: ${config.grep}`)
  }
  if (!Number.isFinite(config.stressSizeBytes) || config.stressSizeBytes <= 0) {
    throw new Error('Stress size must be a positive number of megabytes.')
  }
  if (!Number.isFinite(config.stressChapterCount) || config.stressChapterCount <= 0) {
    throw new Error('Stress chapter count must be a positive number.')
  }

  return config
}

export function makeTagFilter(grep) {
  return (tag) => !grep || grep === tag
}

function normalizeGrepTag(value) {
  const tag = String(value ?? '').trim()
  if (!tag) return ''
  return tag.startsWith('@') ? tag : `@${tag}`
}

export function artifactRunName(config) {
  const grepSuffix = config.grep ? `-${sanitizeArtifactName(config.grep.replace(/^@/, ''))}` : ''
  return `${config.suite}-${config.target}${grepSuffix}`
}

export function sanitizeArtifactName(value) {
  return String(value)
    .replace(/[^a-z0-9._-]+/gi, '-')
    .replace(/^-+|-+$/g, '')
    || 'page'
}
