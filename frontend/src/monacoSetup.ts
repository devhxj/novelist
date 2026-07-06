import { loader } from '@monaco-editor/react'
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js'
import editorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker'

self.MonacoEnvironment = {
  getWorker() {
    return new editorWorker()
  },
}

loader.config({ monaco })

monaco.editor.defineTheme('novelist-light', {
  base: 'vs',
  inherit: true,
  rules: [
    { token: '', foreground: '2d3f34', background: 'f2f8ef' },
  ],
  colors: {
    'editor.background': '#f2f8ef',
    'editor.foreground': '#2d3f34',
    'editorLineNumber.foreground': '#78907f',
    'editorCursor.foreground': '#2f7d4c',
    'editor.selectionBackground': '#b8d9bf',
    'editor.inactiveSelectionBackground': '#d7ead9',
    'editor.lineHighlightBackground': '#e7f2e5',
    'editorIndentGuide.background1': '#d4e4d2',
    'editorIndentGuide.activeBackground1': '#9fbea5',
    'editorWidget.background': '#f8fbf5',
    'editorWidget.border': '#cbdcc8',
    'input.background': '#f8fbf5',
    'input.border': '#cbdcc8',
  },
})
