import { useEffect, useRef, useState } from 'react';
import { Check, ChevronDown, FileText, LoaderCircle, RotateCcw, Search, Settings2, X } from 'lucide-react';
import './menu-manager.css';
export type MenuEntry = { id: string; title: string; source: string; scope: string; kind: string; enabled: boolean; canToggle: boolean; managed: boolean; detail: string };
type ContextMenu = { supported: boolean; available: boolean; installed: boolean; checked: boolean; detail: string };
type Props = { entries: MenuEntry[]; contextMenu: ContextMenu; loading: boolean; busy: boolean; detail: string; message: string; isError: boolean; send(message: { command: string; [key: string]: unknown }): void; close(): void };
export function MenuManager({ entries, contextMenu, loading, busy, detail, message, isError, send, close }: Props) {
  const [search, setSearch] = useState(''), [tab, setTab] = useState('editable'), [scope, setScope] = useState('all'), [kind, setKind] = useState('all');
  const [listPage, setListPage] = useState(0);
  const dialog = useRef<HTMLElement>(null);
  useEffect(() => { const opener = document.activeElement as HTMLElement | null; dialog.current?.focus(); return () => opener?.focus(); }, []);
  const changed = entries.filter(e => e.managed).length, editable = entries.filter(e => e.canToggle).length, query = search.trim().toLocaleLowerCase();
  const filtered = entries.filter(e => (tab === 'all' || (tab === 'changed' ? e.managed : e.canToggle)) && (scope === 'all' || (scope === 'specific' ? e.scope.startsWith('特定类型') : e.scope === scope)) && (kind === 'all' || e.kind === kind) && `${e.title} ${e.source} ${e.scope}`.toLocaleLowerCase().includes(query));
  const disabled = busy || loading;
  useEffect(() => setListPage(0), [query, tab, scope, kind]);
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
        <div className="menu-section-heading"><div><h3>这台电脑的菜单</h3><span>{loading ? '正在读取…' : `${entries.length} 个注册项目 · ${editable} 个可调整`}</span></div><button className="button subtle" disabled={disabled} onClick={() => { send({ command: 'systemMenus' }); send({ command: 'contextMenuStatus' }); }}>{loading ? <LoaderCircle className="spin" size={14} /> : <RotateCcw size={14} />}刷新</button></div>
        <div className="menu-tabs" role="tablist" aria-label="菜单范围">{[['editable', '可调整'], ['all', '全部项目'], ['changed', `归序停用 · ${changed}`]].map(([value, label]) => <button key={value} role="tab" aria-selected={tab === value} onClick={() => setTab(value)}>{label}</button>)}{changed > 0 && <button className="menu-restore" disabled={disabled} onClick={() => send({ command: 'restoreSystemMenus' })}>恢复归序停用的项目</button>}</div>
        <div className="menu-filters"><label className="search-box"><Search size={15} /><input aria-label="搜索右键菜单" placeholder="搜索菜单或软件名称" value={search} onChange={e => setSearch(e.target.value)} /></label><label><select aria-label="菜单出现位置" value={scope} onChange={e => setScope(e.target.value)}>{[['all', '所有位置'], ['所有文件', '所有文件'], ['文件与文件夹', '文件与文件夹'], ['文件夹', '文件夹'], ['空白处', '空白处'], ['磁盘', '磁盘'], ['specific', '特定文件类型']].map(([v, t]) => <option key={v} value={v}>{t}</option>)}</select><ChevronDown size={12} /></label><label><select aria-label="菜单层级" value={kind} onChange={e => setKind(e.target.value)}><option value="all">所有层级</option><option value="一级菜单">一级菜单</option><option value="更多选项">更多选项</option><option value="命令扩展">命令扩展</option></select><ChevronDown size={12} /></label></div>
        <p className="menu-explanation">停用只影响当前账户，原软件的注册信息会保留。一个组件可能生成多个菜单项；实际显示还取决于选中的文件。系统项目和部分一级菜单仅可查看，请在所属软件中调整。</p>
        {detail && <p className="menu-read-warning" role="status">{detail}</p>}
        <div className="menu-list" aria-busy={loading}>{loading ? <div className="menu-empty"><LoaderCircle className="spin" size={24} /><p>正在读取菜单注册信息…</p></div> : filtered.length === 0 ? <div className="menu-empty"><Settings2 size={25} /><p>{tab === 'changed' && !query ? '还没有由归序停用的菜单。' : '这个范围内没有符合条件的菜单。'}</p>{(query || scope !== 'all' || kind !== 'all') && <button onClick={() => { setSearch(''); setScope('all'); setKind('all'); }}>清除筛选</button>}</div> : visible.map(entry => <article className="system-menu-row" key={entry.id}><span className={`menu-dot ${entry.enabled ? 'enabled' : ''}`} /><div><strong>{entry.title}</strong><p>{entry.source}<span>·</span>{entry.scope}<span>·</span>{entry.kind}</p><small>{entry.detail}</small></div><span className="menu-row-state">{entry.managed ? '归序停用' : entry.enabled ? (entry.kind === '一级菜单' ? '已注册' : '已启用') : '已停用'}</span>{entry.canToggle ? <button className={`toggle ${entry.enabled ? 'on' : ''}`} role="switch" aria-label={`${entry.enabled ? '停用' : '恢复'} ${entry.title} ${entry.scope}`} aria-checked={entry.enabled} disabled={disabled} onClick={() => send({ command: 'toggleSystemMenu', id: entry.id, enabled: !entry.enabled })}><span /></button> : <span className="menu-readonly">仅查看</span>}</article>)}</div>
        <div className="menu-pagination"><span>{filtered.length} 个符合条件 · 第 {currentPage + 1} / {pages} 页</span><button disabled={currentPage === 0} onClick={() => setListPage(currentPage - 1)}>上一页</button><button disabled={currentPage >= pages - 1} onClick={() => setListPage(currentPage + 1)}>下一页</button></div>
      </div>
      <footer className={`menu-manager-footer ${isError ? 'error' : ''}`} role="status">{busy ? <LoaderCircle className="spin" size={15} /> : <Check size={15} />}<span>{message || '所有更改都会保留恢复记录。归序不会自动重启资源管理器。'}</span></footer>
    </section>
  </div>;
}
