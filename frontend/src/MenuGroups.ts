import type { MenuEntry } from './MenuManager';
export type MenuGroup = { key: string; title: string; source: string; kind: string; entries: MenuEntry[]; scopes: string[]; editable: MenuEntry[]; managed: MenuEntry[] };
export function groupMenus(entries: MenuEntry[]): MenuGroup[] {
  const groups = new Map<string, MenuGroup>();
  for (const entry of entries) {
    // Display names never identify commands; unidentified registrations stay separate.
    const key = JSON.stringify([entry.commandKey || entry.id, entry.source, entry.kind, entry.parentId || '']);
    let group = groups.get(key);
    if (!group) { group = { key, title: entry.title, source: entry.source, kind: entry.kind, entries: [], scopes: [], editable: [], managed: [] }; groups.set(key, group); }
    group.entries.push(entry);
    if (!group.scopes.includes(entry.scope)) group.scopes.push(entry.scope);
    if (entry.canToggle) group.editable.push(entry);
    if (entry.managed) group.managed.push(entry);
  }
  return [...groups.values()].sort((a, b) => a.source.localeCompare(b.source, 'zh-CN') || a.title.localeCompare(b.title, 'zh-CN'));
}
export function scopeSummary(scopes: string[]): string {
  const broad = scopes.filter(s => !s.startsWith('特定类型'));
  const types = scopes.filter(s => s.startsWith('特定类型')).map(s => s.replace('特定类型 · ', ''));
  return [...broad, ...(types.length ? [types.length <= 2 ? types.join('、') : `${types.slice(0, 2).join('、')}等 ${types.length} 类文件`] : [])].join('、');
}
export function inScene(entry: MenuEntry, scene: string, extension = ''): boolean {
  const location = entry.location?.toLowerCase() || '';
  if (scene === 'all') return true;
  if (scene === 'file') return location === '*' || location === 'allfilesystemobjects';
  if (scene === 'folder') return ['folder', 'directory', 'allfilesystemobjects'].includes(location);
  if (scene === 'folder-bg') return location === 'directory\\background';
  if (scene === 'desktop-bg') return location === 'desktopbackground' || location === 'desktopbackground\\background';
  if (scene === 'drive') return location === 'drive' || location === 'folder';
  if (scene === 'type') return entry.scope.startsWith('特定类型') && (!extension.trim() || `${entry.scope} ${entry.location}`.toLowerCase().includes(extension.trim().toLowerCase().replace(/^\./, '')));
  return false;
}
export function withParents(matched: MenuEntry[], all: MenuEntry[]): MenuEntry[] {
  const result = new Map(matched.map(e => [JSON.stringify([e.id, e.parentId || '']), e]));
  for (const entry of matched) {
    let parent = entry.parentId;
    for (let depth = 0; parent && depth < 7; depth++) {
      const item = all.find(e => e.id === parent); if (!item) break;
      result.set(JSON.stringify([item.id, item.parentId || '']), item); parent = item.parentId;
    }
  }
  return [...result.values()];
}
