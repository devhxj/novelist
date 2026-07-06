import Editor, { type OnMount } from '@monaco-editor/react'
import '@/monacoSetup'

type MountedEditor = Parameters<OnMount>[0]

declare global {
  interface Window {
    __novelistEditor?: {
      getValue: () => string
      setValue: (value: string) => void
    }
  }
}

interface Props {
  value: string
  onChange: (value: string | undefined) => void
  onMount: OnMount
  editorTheme?: string
}

export default function ContentEditor({ value, onChange, onMount, editorTheme }: Props) {
  function handleMount(editor: MountedEditor, monaco: Parameters<OnMount>[1]) {
    if (import.meta.env.DEV && '__appMockState' in window) {
      const controls = {
        getValue: () => editor.getValue(),
        setValue: (nextValue: string) => editor.setValue(nextValue),
      }
      window.__novelistEditor = controls
      editor.onDidDispose(() => {
        if (window.__novelistEditor === controls) {
          window.__novelistEditor = undefined
        }
      })
    }
    onMount(editor, monaco)
  }

  return (
    <Editor
      height="100%"
      language="plaintext"
      theme={editorTheme ?? 'light'}
      value={value}
      onChange={onChange}
      onMount={handleMount}
      options={{
        ariaLabel: '章节正文编辑器',
        minimap: { enabled: false },
        lineNumbers: 'off',
        scrollBeyondLastLine: false,
        fontSize: 17,
        lineHeight: 30,
        fontFamily: "'Noto Serif SC', 'Source Han Serif SC', serif",
        wordWrap: 'on',
        automaticLayout: true,
        unicodeHighlight: { nonBasicASCII: false, ambiguousCharacters: false, invisibleCharacters: false },
        suggestOnTriggerCharacters: false,
        quickSuggestions: false,
        wordBasedSuggestions: 'off',
      }}
    />
  )
}
