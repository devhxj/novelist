import { DiffEditor } from '@monaco-editor/react'
import '@/monacoSetup'

type DiffEditorProps = React.ComponentProps<typeof DiffEditor>

// U10：Diff 视图与正文编辑器共用同一个懒加载的 monaco 块——
// 本模块存在的意义就是把 '@/monacoSetup' 的副作用留在异步 chunk 里。
export default function ContentDiffEditor(props: DiffEditorProps) {
  return <DiffEditor {...props} />
}
