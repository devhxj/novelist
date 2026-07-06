import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'path'

export default defineConfig({
  base: './',
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: 5173,
    strictPort: true,
  },
  css: {
    transformer: 'postcss',
  },
  build: {
    cssMinify: 'esbuild',
    outDir: 'dist',
    assetsDir: 'assets',
    // Mermaid's parser is emitted as one lazy-loaded async chunk. The app shell,
    // workspace, editor, and regular vendor chunks stay under the 500KB budget.
    chunkSizeWarningLimit: 650,
    rolldownOptions: {
      output: {
        codeSplitting: {
          maxSize: 450 * 1024,
          groups: [
            {
              name: 'react-vendor',
              test: /node_modules[\\/](react|react-dom|scheduler)[\\/]/,
              priority: 20,
            },
            {
              name: 'monaco',
              test: /node_modules[\\/](@monaco-editor|monaco-editor)[\\/]/,
              priority: 18,
            },
            {
              name: 'markdown-vendor',
              test: /node_modules[\\/](react-markdown|remark-|rehype-|unified|micromark|mdast-|hast-|vfile|property-information|space-separated-tokens|comma-separated-tokens|highlight\.js|katex)[\\/]/,
              priority: 16,
            },
            {
              name: 'mermaid',
              test: /node_modules[\\/](mermaid|cytoscape|d3-|dagre|khroma|roughjs)[\\/]/,
              priority: 15,
            },
            {
              name: 'graph-vendor',
              test: /node_modules[\\/](@antv|@ant-design)[\\/]/,
              priority: 14,
            },
            {
              name: 'vendor',
              test: /node_modules[\\/]/,
              priority: 1,
              maxSize: 450 * 1024,
            },
          ],
        },
      },
    },
  },
})
