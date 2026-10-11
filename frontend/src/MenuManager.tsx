import { useEffect, useRef, useState } from 'react';
import { Check, ChevronDown, FileText, LoaderCircle, RotateCcw, Search, Settings2, X } from 'lucide-react';
import './menu-manager.css';
import { groupMenus, scopeSummary } from './MenuGroups';
import type { MenuGroup } from './MenuGroups';
export type MenuEntry = { id: string; title: string; source: string; scope: string; kind: string; enabled: boolean; canToggle: boolean; managed: boolean; detail: string };
type ContextMenu = { supported: boolean; available: boolean; installed: boolean; checked: boolean; detail: string };
type Props = { entries: MenuEntry[]; contextMenu: ContextMenu; loading: boolean; busy: boolean; detail: string; message: string; isError: boolean; send(message: { command: string; [key: string]: unknown }): void; close(): void };
function MenuGroupRow({ group, disabled, send }: { group: MenuGroup; disabled: boolean; send: Props['send'] }) {
  const [expanded, setExpanded] = useState(false);
  const locations = group.scopes.map(scope => ({ scope, entries: group.entries.filter(e => e.scope === scope) }));
  const active = group.entries.filter(e => e.enabled).length;
  const editableOn = group.editable.length > 0 && group.editable.every(e => e.enabled);
  const label = active === group.entries.length ? (group.kind === '一级菜单' ? '已注册' : '已启用') : active === 0 ? '已停用' : '部分启用';
  const change = () => {
    const enabled = !editableOn;
    const ids = group.editable.filter(e => e.enabled !== enabled).map(e => e.id);
    send({ command: 'toggleSystemMenus', ids, enabled });
  };
  return <article className="menu-function"><div className="system-menu-row"><span className={`menu-dot ${active > 0 ? 'enabled' : ''}`} /><div><strong>{group.title}</strong><p>{group.source}<span>·</span>{group.kind}<span>·</span>{scopeSummary(group.scopes)}</p><button className="menu-detail-toggle" aria-expanded={expanded} onClick={() => setExpanded(!expanded)}>{expanded ? '收起位置' : '出现位置'}<ChevronDown size={11} /></button></div><span className="menu-row-state">{label}</span>{group.editable.length > 0 ? <button className={`toggle ${editableOn ? 'on' : ''}`} role="switch" aria-label={`${editableOn ? '停用' : '恢复'} ${group.title}`} aria-checked={editableOn} disabled={disabled} onClick={change}><span /></button> : <span className="menu-readonly">仅查看</span>}</div>
    {expanded && <div className="menu-function-details"><p>{group.kind === '一级菜单' ? '这个软件动态生成菜单命令，当前只能查看；请在所属软件设置中调整。' : '相同名称可能对应多个文件类型。这里可逐个调整位置，不会移除所属软件。'}</p>{locations.map(location => {
      const editable = location.entries.filter(e => e.canToggle), on = editable.length > 0 && editable.every(e => e.enabled);
      const enabled = location.entries.filter(e => e.enabled).length;
      return <div className="menu-location-row" key={location.scope}><span>{location.scope}</span><small>{enabled === location.entries.length ? '已启用' : enabled === 0 ? '已停用' : '部分启用'}{editable.length === 0 ? ' · 仅查看' : ''}</small>{editable.length > 0 && <button className={`toggle ${on ? 'on' : ''}`} role="switch" aria-label={`${on ? '停用' : '恢复'} ${group.title} ${location.scope}`} aria-checked={on} disabled={disabled} onClick={() => send({ command: 'toggleSystemMenus', ids: editable.filter(e => e.enabled === on).map(e => e.id), enabled: !on })}><span /></button>}</div>;
    })}</div>}
  </article>;
}
export function MenuManager({ entries, contextMenu, loading, busy, detail, message, isError, send, close }: Props) {
  const [search, setSearch] = useState(''), [tab, setTab] = useState('software'), [scope, setScope] = useState('all'), [kind, setKind] = useState('all');
  const [onlyEditable, setOnlyEditable] = useState(false);
  const [listPage, setListPage] = useState(0);
  const dialog = useRef<HTMLElement>(null);
  useEffect(() => { const opener = document.activeElement as HTMLElement | null; dialog.current?.focus(); return () => opener?.focus(); }, []);
  const groups = groupMenus(entries), changed = groups.filter(g => g.managed.length > 0).length;
  const query = search.trim().toLocaleLowerCase();
  const ordinary = (e: MenuEntry) => e.source !== 'Windows' && e.source !== '未识别的软件';
  const softwareGroups = groups.filter(g => g.entries.some(ordinary));
  const softwareCount = new Set(softwareGroups.map(g => g.source)).size;
  const filtered = groupMenus(entries.filter(e => (tab === 'changed' ? e.managed : tab === 'other' ? !ordinary(e) : ordinary(e))
    && (!onlyEditable || e.canToggle) && (scope === 'all' || (scope === 'specific' ? e.scope.startsWith('特定类型') : e.scope === scope))
    && (kind === 'all' || e.kind === kind) && `${e.title} ${e.source} ${e.scope}`.toLocaleLowerCase().includes(query)));
  const disabled = busy || loading;
  useEffect(() => setListPage(0), [query, tab, scope, kind, onlyEditable]);
  const pages = Math.max(1, Math.ceil(filtered.length / 50)), currentPage = Math.min(listPage, pages - 1);
  const visible = filtered.slice(currentPage * 50, (currentPage + 1) * 50);
  return <div className="modal-backdrop menu-backdrop" onClick={() => { if (!busy) close(); }} onDrop={e => { e.preventDefault(); e.stopPropagation(); }}>
    <section ref={dialog} tabIndex={-1} className="menu-manager" role="dialog" aria-modal="true" aria-labelledby="menu-title" onClick={e => e.stopPropagation()} onKeyDown={e => {
      if (e.key === 'Escape' && !busy) { e.stopPropagation(); close(); }
      if (e.key === 'Tab') {
        const elements = Array.from(dialog.current?.querySelectorAll<HTMLElement>('button:not(:disabled), input, select') ?? []), first = elements[0], last = elements.at(-1);
        if (e.shiftKey && (document.activeElement === first || document.activeElement === dialog.current)) { e.preventDefault(); last?.focus(); }
        else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first?.focus(); }
      }
    }}>
      <header className="menu-manager-header"><span className="setting-icon"><Settings2 size={23} /></span><div><h2 id="menu-title">右键菜单管理</h2><p>让常用的留下，让多余的收起来。</p></div><button className="icon-button" aria-label="关闭右键菜单管理" disabled={busy} onClick={close}><X size={20} /></button></header>
      <div className="menu-manager-body">
        <section className="own-menu-card"><span className="setting-icon"><FileText size={22} /></span><div><span className="eyebrow">归序 · Windows 11 一级菜单</span><h3>复制文件路径 <span className="menu-state">{!contextMenu.checked ? '检查中' : contextMenu.installed ? '已注册' : '未启用'}</span></h3><p>文件和文件夹都能用，多选每行一个路径，不带引号。</p><small>{contextMenu.detail}</small></div><div className="own-menu-actions"><button className="button primary" aria-label={contextMenu.installed ? '移除复制路径菜单' : '添加复制路径菜单'} disabled={disabled || !contextMenu.checked || (!contextMenu.installed && (!contextMenu.supported || !contextMenu.available))} onClick={() => send({ command: contextMenu.installed ? 'removeContextMenu' : 'installContextMenu' })}>{contextMenu.installed ? '移除菜单' : '添加菜单'}</button>{contextMenu.installed && <button className="button subtle" disabled={disabled} onClick={() => send({ command: 'repairContextMenu' })}><RotateCcw size={13} />修复归序菜单</button>}</div></section>
        <p className="menu-cache-note">桌面有、文件夹内没有？先点「修复归序菜单」，再关闭并重开文件夹窗口。仍不显示时退出并重新登录 Windows，让资源管理器重新加载组件。</p>
        {!contextMenu.installed && contextMenu.available && <p className="menu-signature-note">首次添加需要管理员确认本机签名信任，对所有账户生效；菜单仅注册给当前账户。</p>}
        <div className="menu-section-heading"><div><h3>这台电脑的菜单</h3><span>{loading ? '正在读取…' : `${softwareCount} 个软件 · ${softwareGroups.length} 项菜单功能`}</span></div><button className="button subtle" disabled={disabled} onClick={() => { send({ command: 'systemMenus' }); send({ command: 'contextMenuStatus' }); }}>{loading ? <LoaderCircle className="spin" size={14} /> : <RotateCcw size={14} />}刷新</button></div>
        <div className="menu-tabs" role="tablist" aria-label="菜单范围">{[['software', '软件菜单'], ['other', '系统与其他'], ['changed', `归序停用 · ${changed}`]].map(([value, label]) => <button key={value} role="tab" aria-selected={tab === value} onClick={() => setTab(value)}>{label}</button>)}{changed > 0 && <button className="menu-restore" disabled={disabled} onClick={() => send({ command: 'restoreSystemMenus' })}>恢复归序停用的项目</button>}</div>
        <div className="menu-filters"><label className="search-box"><Search size={15} /><input aria-label="搜索右键菜单" placeholder="搜索菜单或软件名称" value={search} onChange={e => setSearch(e.target.value)} /></label><label><select aria-label="菜单出现位置" value={scope} onChange={e => setScope(e.target.value)}>{[['all', '所有位置'], ['所有文件', '所有文件'], ['文件与文件夹', '文件与文件夹'], ['文件夹', '文件夹'], ['空白处', '空白处'], ['磁盘', '磁盘'], ['specific', '特定文件类型']].map(([v, t]) => <option key={v} value={v}>{t}</option>)}</select><ChevronDown size={12} /></label><label><select aria-label="菜单层级" value={kind} onChange={e => setKind(e.target.value)}><option value="all">所有层级</option><option value="一级菜单">一级菜单</option><option value="更多选项">更多选项</option><option value="命令扩展">命令扩展</option></select><ChevronDown size={12} /></label></div>
        <div className="menu-list-note"><p>同一功能在不同文件类型下的重复注册已合并。展开「出现位置」可单独调整；主开关作用于当前筛选下可调整的位置。</p><label><input type="checkbox" checked={onlyEditable} onChange={e => setOnlyEditable(e.target.checked)} />只看可调整</label></div>
        {detail && <p className="menu-read-warning" role="status">{detail}</p>}
        <div className="menu-list" aria-busy={loading}>{loading ? <div className="menu-empty"><LoaderCircle className="spin" size={24} /><p>正在识别软件和菜单功能…</p></div> : visible.length === 0 ? <div className="menu-empty"><Settings2 size={25} /><p>{tab === 'changed' && !query ? '还没有由归序停用的菜单。' : '这个范围内没有符合条件的菜单。'}</p>{(query || scope !== 'all' || kind !== 'all' || onlyEditable) && <button onClick={() => { setSearch(''); setScope('all'); setKind('all'); setOnlyEditable(false); }}>清除筛选</button>}</div> : visible.map(group => <MenuGroupRow key={group.key} group={group} disabled={disabled} send={send} />)}</div>
        <div className="menu-pagination"><span>{filtered.length} 项菜单功能 · 第 {currentPage + 1} / {pages} 页</span><button disabled={currentPage === 0} onClick={() => setListPage(currentPage - 1)}>上一页</button><button disabled={currentPage >= pages - 1} onClick={() => setListPage(currentPage + 1)}>下一页</button></div>
      </div>
      <footer className={`menu-manager-footer ${isError ? 'error' : ''}`} role="status">{busy ? <LoaderCircle className="spin" size={15} /> : <Check size={15} />}<span>{message || '所有更改都会保留恢复记录。归序不会自动重启资源管理器。'}</span></footer>
    </section>
  </div>;
}
