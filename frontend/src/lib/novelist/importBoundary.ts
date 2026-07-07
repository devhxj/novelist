import type { novelImport } from './types'

export type NovelImportKind = novelImport.StartNovelImportInput['import_kind']

export interface NovelImportDropAccepted {
  ok: true
  sourcePath: string
  sourceDisplayName: string
  importKind: NovelImportKind
}

export interface NovelImportDropRejected {
  ok: false
  message: string
}

export type NovelImportDropResult = NovelImportDropAccepted | NovelImportDropRejected

interface FileWithPath extends File {
  path?: string
}

export function buildStartNovelImportInput(sourcePath: string): novelImport.StartNovelImportInput {
  const sourceDisplayName = fileNameFromPath(sourcePath)
  const importKind = importKindFromFileName(sourceDisplayName)
  if (!importKind) {
    throw new Error('仅支持 EPUB、TXT 或 Markdown 文件')
  }

  return {
    task_id: createImportTaskId(),
    source_path: sourcePath,
    source_display_name: sourceDisplayName,
    import_kind: importKind,
    requested_title: titleFromDisplayName(sourceDisplayName),
    commit_message: `Import ${sourceDisplayName}`,
  }
}

export function parseNovelImportDrop(dataTransfer: DataTransfer): NovelImportDropResult {
  if (hasUrlPayload(dataTransfer)) {
    return { ok: false, message: '不能拖入 URL' }
  }

  if (hasDirectoryItem(dataTransfer)) {
    return { ok: false, message: '不能拖入文件夹' }
  }

  const droppedFiles = Array.from(dataTransfer.files ?? []) as FileWithPath[]
  const filePaths = droppedFiles.map(file => file.path ?? '').filter(Boolean)
  const localPaths = uniqueStrings(filePaths.length > 0
    ? filePaths
    : [...extractFileUriPaths(dataTransfer), ...extractPlainTextPaths(dataTransfer)])

  if (droppedFiles.length === 0 && localPaths.length === 0) {
    return { ok: false, message: '没有可导入的文件' }
  }

  if (droppedFiles.length > 0 && localPaths.length === 0) {
    return { ok: false, message: '无法读取本地文件路径，请使用选择文件' }
  }

  const unsupported = localPaths.filter(path => !importKindFromFileName(path))
  if (unsupported.length > 0) {
    return { ok: false, message: '仅支持 EPUB、TXT 或 Markdown 文件' }
  }

  if (localPaths.length !== 1) {
    return { ok: false, message: '一次只能导入一个文件' }
  }

  const sourcePath = localPaths[0]
  if (!isLocalAbsolutePath(sourcePath)) {
    return { ok: false, message: '只能导入本地文件路径' }
  }

  const sourceDisplayName = fileNameFromPath(sourcePath)
  const importKind = importKindFromFileName(sourceDisplayName)
  if (!importKind) {
    return { ok: false, message: '仅支持 EPUB、TXT 或 Markdown 文件' }
  }

  return { ok: true, sourcePath, sourceDisplayName, importKind }
}

export function titleFromDisplayName(displayName: string): string {
  return displayName.replace(/\.(epub|txt|md|markdown)$/i, '').trim() || displayName
}

function createImportTaskId(): string {
  if (typeof crypto.randomUUID === 'function') {
    return `import-${crypto.randomUUID()}`
  }

  return `import-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`
}

function fileNameFromPath(sourcePath: string): string {
  return sourcePath.split(/[\\/]/).filter(Boolean).at(-1) ?? sourcePath
}

function importKindFromFileName(value: string): NovelImportKind | null {
  const lower = value.toLowerCase()
  if (lower.endsWith('.epub')) return 'epub'
  if (lower.endsWith('.txt')) return 'txt'
  if (lower.endsWith('.md') || lower.endsWith('.markdown')) return 'markdown'
  return null
}

function hasUrlPayload(dataTransfer: DataTransfer): boolean {
  const uriList = dataTransfer.getData('text/uri-list').trim()
  if (uriList
    .split(/\r?\n/)
    .some(line => {
      const value = line.trim()
      return value && !value.startsWith('#') && !value.toLowerCase().startsWith('file:')
    })) {
    return true
  }

  return extractPlainTextValues(dataTransfer).some(looksLikeNonFileUrl)
}

function hasDirectoryItem(dataTransfer: DataTransfer): boolean {
  return Array.from(dataTransfer.items ?? []).some(item => {
    const entry = item.webkitGetAsEntry?.()
    return entry?.isDirectory === true
  })
}

function extractPlainTextPaths(dataTransfer: DataTransfer): string[] {
  return extractPlainTextValues(dataTransfer)
    .filter(value => !looksLikeNonFileUrl(value))
    .map(value => value.toLowerCase().startsWith('file:') ? fileUriToPath(value) : value)
    .filter(Boolean)
}

function extractFileUriPaths(dataTransfer: DataTransfer): string[] {
  return dataTransfer
    .getData('text/uri-list')
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(value => value && value.toLowerCase().startsWith('file:'))
    .map(fileUriToPath)
    .filter(Boolean)
}

function fileUriToPath(value: string): string {
  try {
    const url = new URL(value)
    if (url.protocol !== 'file:') return ''
    const decodedPath = decodeURIComponent(url.pathname)
    if (url.hostname) {
      return `\\\\${url.hostname}${decodedPath.replaceAll('/', '\\')}`
    }

    if (/^\/[a-z]:\//i.test(decodedPath)) {
      return decodedPath.slice(1).replaceAll('/', '\\')
    }

    return decodedPath
  } catch {
    return ''
  }
}

function looksLikeUrl(value: string): boolean {
  return /^[a-z][a-z0-9+.-]*:\/\//i.test(value) ||
    value.toLowerCase().startsWith('file:')
}

function looksLikeNonFileUrl(value: string): boolean {
  return looksLikeUrl(value) && !value.toLowerCase().startsWith('file:')
}

function extractPlainTextValues(dataTransfer: DataTransfer): string[] {
  return dataTransfer
    .getData('text/plain')
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(Boolean)
}

function isLocalAbsolutePath(value: string): boolean {
  if (looksLikeUrl(value)) return false
  return /^[a-z]:[\\/]/i.test(value) ||
    /^\\\\[^\\]/.test(value) ||
    value.startsWith('/')
}

function uniqueStrings(values: string[]): string[] {
  return [...new Set(values)]
}
