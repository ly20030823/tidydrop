import type { MenuEntry } from './MenuManager';
export type MenuGroup = { key: string; title: string; source: string; kind: string; entries: MenuEntry[]; scopes: string[]; editable: MenuEntry[]; managed: MenuEntry[] };
export type SoftwareMenus = { key: string; title: string; commands: MenuGroup[]; registrations: number };
export function groupMenus(entries: MenuEntry[]): MenuGroup[] {
  const groups = new Map<string, MenuGroup>();
  const byId = new Map(entries.map(e => [e.id, e]));
  const parentIdentity = (entry: MenuEntry) => {
    const chain: string[] = [], visited = new Set<string>();
    let parent = entry.parentId;
    while (parent && chain.length < 7 && !visited.has(parent)) {
      visited.add(parent);
      const item = byId.get(parent);
      chain.push(item?.commandKey || parent); parent = item?.parentId;
    }
    return chain;
  };
  for (const entry of entries) {
    // Display names never identify commands; unidentified registrations stay separate.
    const key = JSON.stringify([entry.commandKey || entry.id, entry.source, entry.kind, parentIdentity(entry)]);
    let group = groups.get(key);
    if (!group) { group = { key, title: entry.title, source: entry.source, kind: entry.kind, entries: [], scopes: [], editable: [], managed: [] }; groups.set(key, group); }
    group.entries.push(entry);
    if (!group.scopes.includes(entry.scope)) group.scopes.push(entry.scope);
    if (entry.canToggle) group.editable.push(entry);
    if (entry.managed) group.managed.push(entry);
  }
  return [...groups.values()].sort((a, b) => a.source.localeCompare(b.source, 'zh-CN') || a.title.localeCompare(b.title, 'zh-CN'));
}
// A child belongs to the software of its containing menu. Keep the registered
// provider on its entry, but show every command at the same second UI level.
export function softwareMenus(groups: MenuGroup[], all: MenuGroup[] = groups): SoftwareMenus[] {
  const byId = new Map(all.flatMap(g => g.entries).map(e => [e.id, e]));
  const software = new Map<string, SoftwareMenus>();
  for (const group of groups) {
    let owner = group.entries[0];
    const visited = new Set<string>();
    for (let depth = 0; owner.parentId && depth < 7 && !visited.has(owner.id); depth++) {
      visited.add(owner.id); const parent = byId.get(owner.parentId); if (!parent) break; owner = parent;
    }
    const title = owner.source || '未识别的软件';
    let item = software.get(title);
    if (!item) { item = { key: title, title, commands: [], registrations: 0 }; software.set(title, item); }
    item.commands.push(group); item.registrations += group.entries.length;
  }
  return [...software.values()].sort((a, b) => a.title.localeCompare(b.title, 'zh-CN'));
}
export function menuTrail(group: MenuGroup, all: MenuGroup[]): string {
  const byId = new Map(all.flatMap(g => g.entries).map(e => [e.id, e]));
  const titles: string[] = [], visited = new Set<string>();
  let parent = group.entries[0].parentId;
  while (parent && titles.length < 7 && !visited.has(parent)) {
    visited.add(parent); const entry = byId.get(parent); if (!entry) break;
    titles.unshift(entry.title); parent = entry.parentId;
  }
  return titles.join(' › ');
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
