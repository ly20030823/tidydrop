import { useEffect, useRef, useState } from 'react';
import { Check, ChevronDown, FileText, FolderOpen, LoaderCircle, RotateCcw, Search, Settings2, X } from 'lucide-react';
import './menu-manager.css';
import { groupMenus, menuTrail, softwareMenus } from './MenuGroups';
import type { MenuGroup, SoftwareMenus } from './MenuGroups';
export type MenuEntry = { id: string; title: string; source: string; scope: string; kind: string; enabled: boolean; canToggle: boolean; managed: boolean; detail: string;
  commandKey?: string; location?: string; category?: string; parentId?: string; rawName?: string; registration?: string; state?: string; clsid?: string; verb?: string };
export type MenuAnalysis = { name: string; locations: string[]; detail: string } | null;
type ContextMenu = { supported: boolean; available: boolean; installed: boolean; checked: boolean; detail: string };
type Props = { entries: MenuEntry[]; analysis?: MenuAnalysis; contextMenu: ContextMenu; loading: boolean; busy: boolean; detail: string; message: string; isError: boolean; send(message: { command: string; [key: string]: unknown }): void; close(): void };
type Actions = Pick<Props, 'send'> & { disabled: boolean };

function revealMenu(element: HTMLElement | null, start = false) {
  const body = element?.closest<HTMLElement>('.menu-manager-body');
  if (!element || !body) return;
  const target = element.getBoundingClientRect(), viewport = body.getBoundingClientRect();
  const top = start || target.height > viewport.height - 60 ? body.scrollTop + target.top - viewport.top - 12
    : target.bottom > viewport.bottom ? body.scrollTop + target.bottom - viewport.bottom + 16
    : target.top < viewport.top ? body.scrollTop + target.top - viewport.top - 12 : body.scrollTop;
  body.scrollTo({ top, behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
}

function MenuCommand({ group, all, disabled, send }: { group: MenuGroup; all: MenuGroup[] } & Actions) {
  const [expanded, setExpanded] = useState(false);
  const positions = useRef<HTMLDivElement>(null);
  useEffect(() => { if (expanded) revealMenu(positions.current); }, [expanded]);
  const active = group.entries.filter(e => e.enabled).length;
  const on = group.editable.length > 0 && group.editable.every(e => e.enabled);
  const trail = menuTrail(group, all);
  return <article className="menu-command">
    <button className="menu-command-toggle" aria-label={`展开 ${group.title} 的出现位置`} aria-expanded={expanded} onClick={() => setExpanded(!expanded)}>
      <span className={`menu-dot ${active > 0 ? 'enabled' : ''}`} /><span className="menu-command-name">{trail && <small>{trail}</small>}<strong>{group.title}</strong><small>{group.kind} · {group.entries.length} 个出现位置{active === 0 ? ' · 已停用' : active < group.entries.length ? ' · 部分启用' : ''}</small></span><ChevronDown size={15} />
    </button>
    {expanded && <div ref={positions} className="menu-positions" aria-label={`${group.title} 的出现位置`}>
      <div className="menu-positions-heading"><span>出现位置</span>{group.editable.length > 1 && <label>全部可调整位置<button className={`toggle menu-all-toggle ${on ? 'on' : ''}`} role="switch" aria-label={`${on ? '停用' : '恢复'} ${group.title} 的全部可调整位置`} aria-checked={on} disabled={disabled} onClick={() => send({ command: 'toggleSystemMenus', ids: group.editable.filter(e => e.enabled === on).map(e => e.id), enabled: !on })}><span /></button></label>}</div>
      {group.entries.map(entry => <div className="menu-registration" key={`${entry.id}:${entry.parentId}`}>
        <div className="menu-location-row"><span>{entry.scope}</span><small>{entry.state || (entry.enabled ? '已注册' : '已停用')}</small>{entry.canToggle ? <button className={`toggle menu-position-toggle ${entry.enabled ? 'on' : ''}`} role="switch" aria-label={`${entry.enabled ? '停用' : '恢复'} ${group.title} ${entry.scope}`} aria-checked={entry.enabled} disabled={disabled} onClick={() => send({ command: 'toggleSystemMenus', ids: [entry.id], enabled: !entry.enabled })}><span /></button> : <span className="menu-readonly">仅查看</span>}</div>
        <details className="menu-registration-info"><summary>来源与说明</summary><p>{entry.detail}</p>{entry.rawName && entry.rawName !== entry.title && <small>原始名称：{entry.rawName}</small>}{entry.registration && <><code>{entry.registration}</code><button className="menu-detail-toggle" onClick={() => send({ command: 'copyMenuRegistration', id: entry.id })}>复制来源位置</button></>}</details>
      </div>)}
    </div>}
  </article>;
}

function SoftwareSection({ software, all, query, disabled, send }: { software: SoftwareMenus; all: MenuGroup[]; query: string } & Actions) {
  const [expanded, setExpanded] = useState(false), [page, setPage] = useState(0);
  const section = useRef<HTMLElement>(null), reveal = useRef(false);
  useEffect(() => { if (expanded && reveal.current) { revealMenu(section.current, true); reveal.current = false; } }, [expanded]);
  useEffect(() => { setPage(0); if (query) setExpanded(true); }, [query]);
  const pages = Math.max(1, Math.ceil(software.commands.length / 30)), current = Math.min(page, pages - 1);
  return <section ref={section} className="menu-software">
    <button className="menu-software-toggle" aria-label={`展开 ${software.title} 的菜单`} aria-expanded={expanded} onClick={() => { reveal.current = !expanded; setExpanded(!expanded); }}>
      <span className="menu-software-symbol" aria-hidden="true">{software.title.slice(0, 1).toUpperCase()}</span><span><strong>{software.title}</strong><small>{software.commands.length} 条菜单 · {software.registrations} 个出现位置</small></span><ChevronDown size={17} />
    </button>
    {expanded && <div className="menu-software-commands" aria-label={`${software.title} 的菜单条目`}>
      {software.commands.slice(current * 30, (current + 1) * 30).map(group => <MenuCommand key={group.key} group={group} all={all} disabled={disabled} send={send} />)}
      {pages > 1 && <div className="menu-pagination"><span>菜单条目 · 第 {current + 1} / {pages} 页</span><button disabled={current === 0} onClick={() => setPage(current - 1)}>上一页</button><button disabled={current >= pages - 1} onClick={() => setPage(current + 1)}>下一页</button></div>}
    </div>}
  </section>;
}

export function MenuManager({ entries, analysis, contextMenu, loading, busy, detail, message, isError, send, close }: Props) {
  const [search, setSearch] = useState(''), [mode, setMode] = useState('software'), [onlyEditable, setOnlyEditable] = useState(false), [page, setPage] = useState(0);
  const dialog = useRef<HTMLElement>(null);
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null, overflow = document.documentElement.style.overflow;
    document.documentElement.style.overflow = 'hidden'; dialog.current?.focus();
    return () => { document.documentElement.style.overflow = overflow; opener?.focus(); };
  }, []);
  useEffect(() => { if (analysis) { setMode('software'); setSearch(''); setOnlyEditable(false); } }, [analysis]);
  const query = search.trim().toLocaleLowerCase(), all = groupMenus(entries), changed = entries.filter(e => e.managed).length;
  const available = all.filter(g => mode === 'changed' ? g.managed.length > 0 : g.entries.some(e => (e.category || 'context') === (mode === 'software' ? 'context' : mode)));
  // Search selects whole commands, never silently hides their other registrations.
  const software = softwareMenus(available, all).map(item => ({ ...item, commands: item.commands.filter(g =>
    (!onlyEditable || g.editable.length > 0) && (!analysis || g.entries.some(e => analysis.locations.some(l => l.toLowerCase() === e.location?.toLowerCase()))) &&
    (!query || item.title.toLocaleLowerCase().includes(query) || g.entries.some(e => `${e.title} ${e.source} ${e.scope} ${e.rawName || ''}`.toLocaleLowerCase().includes(query))))
  })).filter(item => item.commands.length > 0).map(item => ({ ...item, registrations: item.commands.reduce((count, g) => count + g.entries.length, 0) }));
  const disabled = busy || loading, pages = Math.max(1, Math.ceil(software.length / 16)), current = Math.min(page, pages - 1);
  useEffect(() => setPage(0), [query, mode, onlyEditable, analysis]);
  const selectMode = (value: string) => { setMode(value); setSearch(''); send({ command: 'clearMenuAnalysis' }); };
  const reset = () => { setSearch(''); setOnlyEditable(false); send({ command: 'clearMenuAnalysis' }); };
  return <div className="modal-backdrop menu-backdrop" onClick={() => { if (!busy) close(); }} onDrop={e => { e.preventDefault(); e.stopPropagation(); }}>
    <section ref={dialog} tabIndex={-1} className="menu-manager" role="dialog" aria-modal="true" aria-labelledby="menu-title" onClick={e => e.stopPropagation()} onKeyDown={e => {
      if (e.key === 'Escape' && !busy) { e.stopPropagation(); close(); }
      if (e.key === 'Tab') { const elements = Array.from(dialog.current?.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), summary') ?? []).filter(el => el.getClientRects().length > 0), first = elements[0], last = elements.at(-1); if (e.shiftKey && (document.activeElement === first || document.activeElement === dialog.current)) { e.preventDefault(); last?.focus(); } else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first?.focus(); } }
    }}>
      <header className="menu-manager-header"><span className="setting-icon"><Settings2 size={23} /></span><div><h2 id="menu-title">右键菜单管理</h2><p>选择软件，展开菜单条目，再调整它的出现位置。</p></div><button className="icon-button" aria-label="关闭右键菜单管理" disabled={busy} onClick={close}><X size={20} /></button></header>
      <div className="menu-manager-body">
        <section className="own-menu-card"><span className="setting-icon"><FileText size={20} /></span><div><h3>归序 · 复制文件路径 <span className="menu-state">{!contextMenu.checked ? '检查中' : contextMenu.installed ? '已注册' : '未启用'}</span></h3><p>文件和文件夹都能用，复制的路径不带引号。</p><small>{contextMenu.detail}</small></div><div className="own-menu-actions"><button className="button primary" aria-label={contextMenu.installed ? '移除复制路径菜单' : '添加复制路径菜单'} disabled={disabled || !contextMenu.checked || (!contextMenu.installed && (!contextMenu.supported || !contextMenu.available))} onClick={() => send({ command: contextMenu.installed ? 'removeContextMenu' : 'installContextMenu' })}>{contextMenu.installed ? '移除菜单' : '添加菜单'}</button>{contextMenu.installed && <button className="button subtle" disabled={disabled} onClick={() => send({ command: 'repairContextMenu' })}><RotateCcw size={13} />修复归序菜单</button>}</div></section>
        {!contextMenu.installed && contextMenu.available && <p className="menu-signature-note">首次添加需要管理员确认本机签名信任，对所有账户生效；菜单仅注册给当前账户。</p>}
        <div className="menu-section-heading"><div><h3>这台电脑的菜单</h3><span>{loading ? '正在读取…' : `${software.length} 个软件或来源`}</span></div><button className="button subtle" disabled={disabled} onClick={() => { send({ command: 'systemMenus' }); send({ command: 'contextMenuStatus' }); }}>{loading ? <LoaderCircle className="spin" size={14} /> : <RotateCcw size={14} />}刷新</button></div>
        <nav className="menu-tabs" aria-label="菜单查找方式">{[['software', '软件菜单'], ['new', '新建模板'], ['sendto', '发送到'], ['openwith', '打开方式'], ['changed', `归序停用 · ${changed}`]].map(([value, label]) => <button key={value} aria-current={mode === value ? 'page' : undefined} onClick={() => selectMode(value)}>{label}</button>)}{changed > 0 && <button className="menu-restore" disabled={disabled} onClick={() => send({ command: 'restoreSystemMenus' })}>恢复归序停用的项目</button>}</nav>
        <div className="menu-filters"><label className="search-box"><Search size={15} /><input aria-label="搜索右键菜单" placeholder="搜索软件或菜单名称，如 WPS、百度网盘" value={search} onChange={e => setSearch(e.target.value)} /></label><label className="menu-editable-filter"><input type="checkbox" checked={onlyEditable} onChange={e => setOnlyEditable(e.target.checked)} />只看可调整</label></div>
        <div className="menu-object-picker"><span>也可以按文件查找</span><button disabled={disabled} onClick={() => send({ command: 'analyzeMenuFile' })}><FileText size={12} />选择文件</button><button disabled={disabled} onClick={() => send({ command: 'analyzeMenuFolder' })}><FolderOpen size={12} />选择文件夹</button></div>
        {analysis && <p className="menu-analysis-note">与「{analysis.name}」相关的菜单。展开条目后仍显示它的所有注册位置。{analysis.detail}<button onClick={() => send({ command: 'clearMenuAnalysis' })}>取消对象筛选</button></p>}
        <div className="menu-layer-guide"><span><b>1</b>{['new', 'sendto', 'changed'].includes(mode) ? '软件或来源' : '软件'}</span><i>›</i><span><b>2</b>菜单条目</span><i>›</i><span><b>3</b>出现位置与开关</span></div>
        {['new', 'sendto', 'openwith'].includes(mode) && <p className="menu-explanation">{mode === 'new' ? '模板按账户范围列出。全部账户的模板需要管理员确认。' : mode === 'sendto' ? '隐藏发送到入口会保留快捷方式及其目标。' : '应用列表与文件类型推荐分别注册；隐藏入口不更改默认打开程序。'}</p>}
        {detail && <p className="menu-read-warning" role="status">{detail}</p>}
        <div className="menu-software-list" aria-busy={loading}>{loading && entries.length === 0 ? <div className="menu-empty"><LoaderCircle className="spin" size={24} /><p>正在读取菜单…</p></div> : software.length === 0 ? <div className="menu-empty"><Settings2 size={25} /><p>没有符合条件的菜单。</p>{(query || onlyEditable || analysis) && <button onClick={reset}>清除筛选</button>}</div> : software.slice(current * 16, (current + 1) * 16).map(item => <SoftwareSection key={`${mode}:${item.key}`} software={item} all={all} query={query} disabled={disabled} send={send} />)}</div>
        {pages > 1 && <div className="menu-pagination"><span>{software.length} 个软件或来源 · 第 {current + 1} / {pages} 页</span><button disabled={current === 0} onClick={() => setPage(current - 1)}>上一页</button><button disabled={current >= pages - 1} onClick={() => setPage(current + 1)}>下一页</button></div>}
      </div>
      <footer className={`menu-manager-footer ${isError ? 'error' : ''}`} role="status">{busy ? <LoaderCircle className="spin" size={15} /> : <Check size={15} />}<span>{message || '菜单按软件归在一起；只调整选择的位置，停用后可恢复。'}</span></footer>
    </section>
  </div>;
}
