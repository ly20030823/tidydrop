import type { MenuEntry } from './MenuManager';
export type MenuGroup = { key: string; title: string; source: string; kind: string; entries: MenuEntry[]; scopes: string[]; editable: MenuEntry[]; managed: MenuEntry[] };
export function groupMenus(entries: MenuEntry[]): MenuGroup[] {
  const groups = new Map<string, MenuGroup>();
  for (const entry of entries) {
    const key = JSON.stringify([entry.source, entry.title, entry.kind]);
    let group = groups.get(key);
    if (!group) {
      group = { key, title: entry.title, source: entry.source, kind: entry.kind, entries: [], scopes: [], editable: [], managed: [] };
      groups.set(key, group);
    }
    group.entries.push(entry);
    if (!group.scopes.includes(entry.scope)) group.scopes.push(entry.scope);
    if (entry.canToggle) group.editable.push(entry);
    if (entry.managed) group.managed.push(entry);
  }
  return Array.from(groups.values()).sort((a, b) => a.source.localeCompare(b.source, 'zh-CN') || a.title.localeCompare(b.title, 'zh-CN'));
}
export function scopeSummary(scopes: string[]): string {
  const broad = scopes.filter(s => !s.startsWith('特定类型'));
  const types = scopes.filter(s => s.startsWith('特定类型')).map(s => s.replace('特定类型 · ', ''));
  return [...broad, ...(types.length ? [types.length <= 2 ? types.join('、') : `${types.slice(0, 2).join('、')}等 ${types.length} 类文件`] : [])].join('、');
}
