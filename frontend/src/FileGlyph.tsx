import { Archive, ArrowUpRight, Code2, File, FileText, Folder, Image, LayoutGrid, Monitor, Music2, Video } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';

export const categoryInfo: Record<string, { icon: LucideIcon; color: string; examples: string }> = {
  文档: { icon: FileText, color: 'blue', examples: 'PDF · Word · Excel' },
  图片: { icon: Image, color: 'amber', examples: 'JPG · PNG · SVG' },
  视频: { icon: Video, color: 'purple', examples: 'MP4 · MOV · MKV' },
  音频: { icon: Music2, color: 'pink', examples: 'MP3 · WAV · FLAC' },
  压缩包: { icon: Archive, color: 'orange', examples: 'ZIP · RAR · 7Z' },
  安装包: { icon: Monitor, color: 'teal', examples: 'EXE · MSI · MSIX' },
  设计素材: { icon: LayoutGrid, color: 'purple', examples: 'PSD · AI · DWG' },
  代码与数据: { icon: Code2, color: 'blue', examples: 'JS · PY · JSON' },
  快捷方式: { icon: ArrowUpRight, color: 'teal', examples: 'LNK · URL' },
  文件夹: { icon: Folder, color: 'teal', examples: '保留内部结构' },
  其他: { icon: File, color: 'gray', examples: '其余类型' },
};

export function Glyph({ category, small = false }: { category: string; small?: boolean }) {
  const info = categoryInfo[category] ?? categoryInfo.其他;
  const Icon = info.icon;
  return <span className={`file-glyph ${info.color} ${small ? 'small' : ''}`}><Icon size={small ? 17 : 21} strokeWidth={1.7} /></span>;
}
