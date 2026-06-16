import Markdown from '@/components/Markdown'
import { splitFrontmatter } from './types'

interface Props {
  content: string
}

export default function SkillPreview({ content }: Props) {
  if (!content) {
    return (
      <div className="flex items-center justify-center h-full">
        <p className="text-sm text-muted-foreground">暂无内容</p>
      </div>
    )
  }

  const { meta, body } = splitFrontmatter(content)

  return (
    <div className="overflow-auto h-full">
      {Object.keys(meta).length > 0 && (
        <div className="px-6 pt-6 pb-4">
          <table className="border bg-muted/20 w-full text-sm">
            <tbody>
              {meta.name && (
                <tr className="border-b">
                  <td className="px-4 py-2.5 text-muted-foreground whitespace-nowrap w-20">名称</td>
                  <td className="px-4 py-2.5 text-foreground font-semibold">{meta.name}</td>
                </tr>
              )}
              {meta.description && (
                <tr className="border-b">
                  <td className="px-4 py-2.5 text-muted-foreground whitespace-nowrap w-20">简介</td>
                  <td className="px-4 py-2.5 text-foreground">{meta.description}</td>
                </tr>
              )}
              {meta.category && (
                <tr>
                  <td className="px-4 py-2.5 text-muted-foreground whitespace-nowrap w-20">分类</td>
                  <td className="px-4 py-2.5 text-foreground">{meta.category}</td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}
      <div className="px-6 py-4">
        <Markdown content={body} />
      </div>
    </div>
  )
}
