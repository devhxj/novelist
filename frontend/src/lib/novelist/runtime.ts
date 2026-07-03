import { bridge, novelist } from './bridge'

export const runtimeBridgeMethods = {
  windowMinimize: 'runtime.window.minimize',
  windowToggleMaximize: 'runtime.window.toggleMaximize',
  windowIsMaximized: 'runtime.window.isMaximized',
  appQuit: 'runtime.app.quit',
  shellOpenExternal: 'runtime.shell.openExternal',
} as const

export function minimize(): Promise<void> {
  return bridge.invoke<void>(runtimeBridgeMethods.windowMinimize)
}

export function toggleMaximize(): Promise<void> {
  return bridge.invoke<void>(runtimeBridgeMethods.windowToggleMaximize)
}

export function isMaximized(): Promise<boolean> {
  return bridge.invoke<boolean>(runtimeBridgeMethods.windowIsMaximized)
}

export function quit(): Promise<void> {
  return bridge.invoke<void>(runtimeBridgeMethods.appQuit)
}

export function openExternal(url: string): Promise<void> {
  return bridge.invoke<void>(runtimeBridgeMethods.shellOpenExternal, {
    url: normalizeHttpsUrl(url),
  })
}

export const runtime = {
  window: {
    minimize,
    toggleMaximize,
    isMaximized,
  },
  app: {
    quit,
  },
  shell: {
    openExternal,
  },
} as const

export function WindowMinimise(): Promise<void> {
  return minimize()
}

export function WindowToggleMaximise(): Promise<void> {
  return toggleMaximize()
}

export function WindowIsMaximised(): Promise<boolean> {
  return isMaximized()
}

export function Quit(): Promise<void> {
  return quit()
}

export function BrowserOpenURL(url: string): Promise<void> {
  return openExternal(url)
}

export function installNovelistRuntimeGlobal(): void {
  if (typeof window === 'undefined') {
    return
  }

  window.novelist = {
    ...novelist,
    ...window.novelist,
    window: runtime.window,
    app: runtime.app,
    shell: runtime.shell,
  }
}

installNovelistRuntimeGlobal()

function normalizeHttpsUrl(url: string): string {
  if (typeof url !== 'string' || url.trim() === '') {
    throw new TypeError('External URL must be a non-empty string.')
  }

  let parsed: URL
  try {
    parsed = new URL(url)
  } catch (error) {
    throw new TypeError('External URL must be an absolute https:// URL.', { cause: error })
  }

  if (parsed.protocol !== 'https:') {
    throw new TypeError('External URL must use https://.')
  }

  return parsed.toString()
}
