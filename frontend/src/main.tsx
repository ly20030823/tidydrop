import React, { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Archive, ArrowDown, ArrowDownToLine, ArrowRight, ArrowUpRight, Check, CheckCircle2, ChevronRight, CircleHelp, Clock3, Code2, File, FileText, Folder, FolderCheck, FolderOpen, Image, LayoutGrid, LoaderCircle, Monitor, Music2, Search, Settings2, RefreshCw, CalendarDays, ShieldCheck, Undo2, Video, X } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import './style.css';

type Settings = { rootFolder: string; dateFolders: boolean; autoOrganize: boolean };
type FileItem = { source: string; fileName: string; category: string; destination: string; status: string; canMove: boolean };
type History = { id: string; time: string; source: string; destination: string; fileName: string; ruleName: string; state: string; detail: string };
type ArchiveItem = { id: string; fileName: string; path: string; category: string; date: string; isDirectory: boolean };
type Snapshot = { settings: Settings; items: FileItem[]; history: History[]; library: ArchiveItem[]; busy: boolean; message: string; isError: boolean; pendingCount: number };
type Message = { command: string; [key: string]: unknown };
type WebView = { postMessage(message: Message): void; postMessageWithAdditionalObjects(message: Message, files: File[]): void; addEventListener(name: 'message', handler: (event: MessageEvent) => void): void; removeEventListener(name: 'message', handler: (event: MessageEvent) => void): void };
declare global { interface Window { chrome?: { webview?: WebView } } }
const bridge = window.chrome?.webview;
const isDemo = !bridge && new URLSearchParams(location.search).get('demo') === '1';
const empty: Snapshot = { settings: { rootFolder: '', dateFolders: false, autoOrganize: true }, items: [], history: [], library: [], busy: false, message: '', isError: false, pendingCount: 0 };
const demoRoot = 'D:\\我的材料';
const dayExample = new Date().toLocaleDateString('sv-SE');
const monthExample = `${new Date().getFullYear()}-${String(new Date().getMonth() + 1).padStart(2, '0')}`;
const demoHistory: History[] = [
  { id: 'demo1', time: new Date().toISOString(), source: 'C:\\Users\\桌面\\项目说明.docx', destination: `${demoRoot}\\文档\\项目说明.docx`, fileName: '项目说明.docx', ruleName: '文档', state: '已归档', detail: '' },
  { id: 'demo2', time: new Date().toISOString(), source: 'C:\\Users\\桌面\\参考照片.png', destination: `${demoRoot}\\图片\\参考照片.png`, fileName: '参考照片.png', ruleName: '图片', state: '已归档', detail: '' },
];
const categoryInfo: Record<string, { icon: LucideIcon; color: string; examples: string }> = {
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

function Glyph({ category, small = false }: { category: string; small?: boolean }) {
  const info = categoryInfo[category] ?? categoryInfo.其他;
  const Icon = info.icon;
  return <span className={`file-glyph ${info.color} ${small ? 'small' : ''}`}><Icon size={small ? 17 : 21} strokeWidth={1.7} /></span>;
}
function Toggle({ checked, onChange, disabled, label }: { checked: boolean; onChange(): void; disabled: boolean; label: string }) {
  return <button className={`toggle ${checked ? 'on' : ''}`} role="switch" aria-label={label} aria-checked={checked} disabled={disabled} onClick={onChange}><span /></button>;
}

function App() {
  const [snapshot, setSnapshot] = useState<Snapshot>(isDemo ? { ...empty, settings: { ...empty.settings, rootFolder: demoRoot }, history: demoHistory, library: demoHistory.map(h => ({ id: h.id, fileName: h.fileName, path: h.destination, category: h.ruleName, date: dayExample, isDirectory: false })) } : empty);
  const [page, setPage] = useState<'library' | 'organize' | 'history' | 'settings'>('library');
  const [view, setView] = useState<'date' | 'type'>('date');
  const [group, setGroup] = useState('');
  const [librarySearch, setLibrarySearch] = useState('');
  const [dragging, setDragging] = useState(false);
  const [search, setSearch] = useState('');
  const [help, setHelp] = useState(false);
  const { settings, busy, items, history, library } = snapshot;
  const completed = history.filter(h => h.state === '已归档').length;
  const pending = items.filter(i => i.canMove && i.status === '准备归档').length;

  useEffect(() => {
    const receive = (event: MessageEvent) => {
      if (event.data.type === 'state') setSnapshot(event.data.state);
      if (event.data.type === 'navigate') setPage(event.data.page);
    };
    bridge?.addEventListener('message', receive);
    bridge?.postMessage({ command: 'ready' });
    return () => bridge?.removeEventListener('message', receive);
  }, []);

  function send(message: Message) {
    if (bridge) { bridge.postMessage(message); return; }
    if (!isDemo) return;
    if (message.command === 'saveSettings') setSnapshot(s => ({ ...s, settings: { ...s.settings, ...(message.settings as Partial<Settings>) } }));
    else if (message.command === 'pickRoot') setSnapshot(s => ({ ...s, settings: { ...s.settings, rootFolder: demoRoot }, message: '已选择示例归档文件夹。此页面仅用于预览。' }));
    else if (message.command === 'undo') setSnapshot(s => ({ ...s, history: s.history.map(h => h.id === message.id ? { ...h, state: '已撤销' } : h), message: '预览：已演示撤销状态，没有移动真实文件。' }));
    else setSnapshot(s => ({ ...s, message: '这是界面预览。文件选择、桌面快捷方式和整理操作请在桌面程序中使用。' }));
  }
  function updateSettings(change: Partial<Settings>) { send({ command: 'saveSettings', settings: { ...settings, ...change } }); }
  function drop(event: React.DragEvent) {
    event.preventDefault(); event.stopPropagation(); setDragging(false);
    const files = Array.from(event.dataTransfer.files);
    if (bridge) {
      if (typeof bridge.postMessageWithAdditionalObjects === 'function') bridge.postMessageWithAdditionalObjects({ command: 'drop' }, files);
      else send({ command: 'dropUnsupported' });
    } else if (isDemo) setSnapshot(s => ({ ...s, message: `收到 ${files.length} 个项目（界面预览，不会移动文件）。` }));
  }
  const filtered = history.filter(h => `${h.fileName} ${h.source} ${h.destination}`.toLowerCase().includes(search.toLowerCase()));
  const nav = [ { key: 'library' as const, icon: Archive, label: '归档空间' }, { key: 'organize' as const, icon: FolderCheck, label: '整理文件' }, { key: 'history' as const, icon: Clock3, label: '整理记录' }, { key: 'settings' as const, icon: Settings2, label: '归档设置' } ];
  const title = page === 'library' ? '收下的每一份，都在这里。' : page === 'organize' ? '给每份文件，一个归处。' : page === 'history' ? '每一次整理，都有迹可循。' : '按你的习惯，整理。';
  const subtitle = page === 'library' ? '按收纳日期回顾，或按文件类型查找。切换视图，文件仍只保存一份。' : page === 'organize' ? '拖入文件，剩下的交给归序。' : page === 'history' ? '查看文件去向，也可以把它送回原来的位置。' : '只需选好一个总文件夹，我们会照顾好里面的分类。';

  return <div className="app-shell" onDragOver={event => event.preventDefault()} onDrop={drop}>
    <aside className="sidebar">
      <div className="brand"><span className="brand-mark"><FolderCheck size={26} strokeWidth={1.8} /></span><div><strong>归序</strong><span>TidyDrop</span></div></div>
      <div className="nav-caption">工作空间</div>
      <nav aria-label="主导航">{nav.map(({ key, icon: Icon, label }) => <button className={`nav-item ${page === key ? 'active' : ''}`} key={key} onClick={() => setPage(key)}><Icon size={19} strokeWidth={1.7} /><span>{label}</span>{key === 'history' && completed > 0 && <b>{completed}</b>}</button>)}</nav>
      <div className="sidebar-destination"><span className="destination-symbol"><Folder size={20} /></span><span className="eyebrow">你的归档空间</span><strong title={settings.rootFolder}>{settings.rootFolder ? settings.rootFolder.split(/[/\\]/).filter(Boolean).at(-1) || settings.rootFolder : '选择一个归处'}</strong><p>{settings.rootFolder ? '分类文件夹会自动建立' : '设置一次，之后拖入就好'}</p><button disabled={busy} onClick={() => send({ command: 'pickRoot' })}>{settings.rootFolder ? '更改文件夹' : '选择文件夹'}<ArrowUpRight size={15} /></button></div>
      <div className="sidebar-bottom"><div><ShieldCheck size={17} /><span>文件留在你的电脑上</span></div><span>TidyDrop <i>·</i> v0.3.1</span></div>
    </aside>
    <main>
      <div className="topbar"><span>少一点杂乱，多一点从容</span><div><span className="local-pill"><i />本地运行</span><button className="icon-button" aria-label="使用帮助" title="使用帮助" onClick={() => setHelp(true)}><CircleHelp size={20} /></button></div></div>
      <header className="page-header"><div><div className="eyebrow">{page === 'library' ? '你的材料，井然有序' : page === 'organize' ? '整理，从拖一下开始' : page === 'history' ? '整理记录' : '偏好设置'}</div><h1>{title}</h1><p>{subtitle}</p></div><button className="button subtle" disabled={busy} onClick={() => send({ command: 'shortcut' })}><Monitor size={16} />创建桌面入口</button></header>
      {isDemo && <div className="preview-note">界面预览 · 这里的示例操作不会移动真实文件</div>}
      {snapshot.message && <div className={`notice ${snapshot.isError ? 'error' : ''}`} role={snapshot.isError ? 'alert' : 'status'}>{busy ? <LoaderCircle className="spin" size={17} /> : snapshot.isError ? <CircleHelp size={17} /> : <CheckCircle2 size={17} />}<span>{snapshot.message}</span></div>}
      {page === 'library' && <section className="library-section">
        <div className="library-toolbar">
          <div className="view-switch" role="group" aria-label="归档视图">
            <button className={view === 'date' ? 'selected' : ''} aria-pressed={view === 'date'} onClick={() => { setView('date'); setGroup(''); }}><CalendarDays size={16} />按日期</button>
            <button className={view === 'type' ? 'selected' : ''} aria-pressed={view === 'type'} onClick={() => { setView('type'); setGroup(''); }}><LayoutGrid size={16} />按类型</button>
          </div>
          <label className="search-box"><Search size={16} /><input aria-label="搜索归档空间" placeholder="搜索名称或路径" value={librarySearch} onChange={e => setLibrarySearch(e.target.value)} /></label>
          <button className="button subtle" disabled={busy} onClick={() => send({ command: 'refreshLibrary' })}><RefreshCw size={14} />刷新</button>
          <button className="button primary" onClick={() => setPage('organize')}><ArrowDownToLine size={15} />收纳新材料</button>
        </div>
        <div className="library-layout">
          <div className="library-groups"><div className="group-caption">{view === 'date' ? '收纳日期 · 最近在前' : '文件类型'}</div>
            <button className={!group ? 'selected' : ''} onClick={() => setGroup('')}><span>全部材料</span><b>{library.length}</b></button>
            {Array.from(new Set(library.map(item => view === 'date' ? item.date : item.category))).sort((a, b) => view === 'date' ? b.localeCompare(a) : a.localeCompare(b, 'zh-CN')).map(key => <button key={key} className={group === key ? 'selected' : ''} onClick={() => setGroup(key)}><span>{key}{key === dayExample && view === 'date' ? ' · 今天' : ''}</span><b>{library.filter(item => (view === 'date' ? item.date : item.category) === key).length}</b></button>)}
          </div>
          <div className="library-files">
            {library.filter(item => (!group || (view === 'date' ? item.date : item.category) === group) && `${item.fileName} ${item.path}`.toLowerCase().includes(librarySearch.toLowerCase())).length === 0 ? <div className="empty-state"><Archive size={36} strokeWidth={1.3} /><h3>{library.length ? '没有匹配的材料' : '你的归档空间，准备好了'}</h3><p>{library.length ? '试试其他日期、类型或名称。' : '设置一个总文件夹，把文件或文件夹拖到桌面入口，就能在这里找到它。'}</p><button className="button subtle" onClick={() => setPage('organize')}>开始收纳<ArrowRight size={15} /></button></div> :
              Array.from(new Set(library.filter(item => (!group || (view === 'date' ? item.date : item.category) === group) && `${item.fileName} ${item.path}`.toLowerCase().includes(librarySearch.toLowerCase())).map(item => view === 'date' ? item.date : item.category))).sort((a, b) => view === 'date' ? b.localeCompare(a) : a.localeCompare(b, 'zh-CN')).map(key => <section className="library-group" key={key}><h3>{view === 'date' ? <CalendarDays size={16} /> : <LayoutGrid size={16} />}{key}<span>{view === 'date' ? '收纳的材料' : '分类材料'}</span></h3>{library.filter(item => (view === 'date' ? item.date : item.category) === key && (!group || key === group) && `${item.fileName} ${item.path}`.toLowerCase().includes(librarySearch.toLowerCase())).map(item => <div className="archive-row" key={item.path}><Glyph category={item.isDirectory ? '文件夹' : item.category} small /><div><strong title={item.fileName}>{item.fileName}</strong><small title={item.path}>{item.path}</small></div><span>{view === 'date' ? item.category : item.date}</span><button className="button subtle" title="在资源管理器中定位" disabled={busy} onClick={() => send({ command: 'reveal', id: item.id })}><FolderOpen size={14} />定位</button></div>)}</section>)
            }
          </div>
        </div>
        <div className="gentle-tip"><ShieldCheck size={15} />文件夹整包保留。新材料按收纳当天归档，旧版已收纳材料也会显示。</div>
      </section>}
      {page === 'organize' && <>
        <section className={`drop-zone ${dragging ? 'dragging' : ''}`} onDragEnter={e => { e.preventDefault(); setDragging(true); }} onDragLeave={e => { if (!e.currentTarget.contains(e.relatedTarget as Node)) setDragging(false); }} onDrop={drop}>
          <div className="drop-copy"><span className="drop-tag"><ArrowDownToLine size={14} />拖入即归档</span><h2>{dragging ? '放开，文件就到家了。' : busy ? '正在为文件找到归处…' : '把文件拖到这里'}</h2><p>文件或文件夹，一次收下。<br />也可以直接拖到桌面上的「归序」图标。</p><button className="button primary" disabled={busy} onClick={() => send({ command: 'pickFiles' })}>{busy ? <LoaderCircle size={16} className="spin" /> : <span className="plus">+</span>}选择文件<ArrowRight size={16} /></button><button className="button folder-pick" disabled={busy} onClick={() => send({ command: 'pickFolder' })}><Folder size={16} />选择文件夹</button></div>
          <div className="folder-illustration" aria-hidden="true"><div className="orbit orbit-one" /><div className="orbit orbit-two" /><div className="paper paper-back"><Image size={25} /></div><div className="paper paper-front"><FileText size={24} /><i /><i /><i /></div><div className="illustration-folder"><span><Check size={27} /></span></div><div className="floating-arrow"><ArrowDown size={20} /></div><span className="tiny-spark spark-one">✦</span><span className="tiny-spark spark-two">✦</span></div>
        </section>
        <section className="destination-bar"><span className="destination-icon"><FolderOpen size={20} /></span><div><span>归档位置</span><strong title={settings.rootFolder}>{settings.rootFolder || '先选一个总文件夹，让文件有处可去'}</strong></div><button disabled={busy} onClick={() => send({ command: settings.rootFolder ? 'openRoot' : 'pickRoot' })}>{settings.rootFolder ? '打开文件夹' : '选择文件夹'}<ArrowUpRight size={15} /></button><span className="bar-divider" /><button className="mode-chip" onClick={() => setPage('settings')}><i />{settings.autoOrganize ? '自动整理' : '先预览，再整理'}<ChevronRight size={14} /></button></section>
        {items.length > 0 ? <section className="results-section"><div className="section-heading"><div><h3>本次整理</h3><span>{items.length} 个项目{snapshot.pendingCount > 0 ? ` · ${snapshot.pendingCount} 个排队中` : ''}</span></div>{pending > 0 && <button disabled={busy} className="button primary" onClick={() => send({ command: 'organize' })}>整理 {pending} 个项目<ArrowRight size={15} /></button>}</div><div className="table-wrap"><table><thead><tr><th>文件</th><th>归档去向</th><th>状态</th></tr></thead><tbody>{items.map((item, index) => <tr key={`${item.source}-${index}`}><td><div className="file-name"><Glyph category={item.category} small /><span title={item.source}>{item.fileName}<small>{item.category || '保持原位'}</small></span></div></td><td className="path-cell" title={item.destination}>{item.destination}</td><td><span title={item.status} className={`status-badge ${item.status.startsWith('已归档') ? 'success' : item.status === '准备归档' ? 'waiting' : 'neutral'}`}>{item.status.startsWith('已归档') && <Check size={12} />}{item.status}</span></td></tr>)}</tbody></table></div></section> : <section className="categories-section"><div className="section-heading"><div><h3>文件会找到自己的位置</h3><span>按类型分类，目录自动建立</span></div><button onClick={() => setPage('settings')}>查看分类<ArrowRight size={15} /></button></div><div className="category-grid">{['文档', '图片', '视频', '音频', '压缩包', '其他'].map(category => <div className="category-card" key={category}><Glyph category={category} /><strong>{category}</strong><span>{categoryInfo[category].examples}</span></div>)}</div><div className="gentle-tip"><ShieldCheck size={15} />同名文件自动编号，整理记录随时可查。</div></section>}
      </>}
      {page === 'history' && <section className="history-section"><div className="section-heading"><div><h3>归档足迹</h3><span>{history.length} 条记录</span></div><label className="search-box"><Search size={16} /><input aria-label="搜索整理记录" placeholder="搜索文件或路径" value={search} onChange={e => setSearch(e.target.value)} /></label></div>{filtered.length > 0 ? <div className="table-wrap"><table className="history-table"><thead><tr><th>文件 / 时间</th><th>归档位置</th><th>状态</th><th aria-label="操作" /></tr></thead><tbody>{filtered.map(entry => <tr key={entry.id}><td><div className="file-name"><Glyph category={entry.ruleName} small /><span title={entry.source}>{entry.fileName}<small>{new Date(entry.time).toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })}</small></span></div></td><td className="path-cell" title={`原位置：${entry.source}\n归档位置：${entry.destination}`}>{entry.destination}</td><td><span className={`status-badge ${entry.state === '已归档' ? 'success' : 'neutral'}`} title={entry.detail}>{entry.state}</span></td><td>{entry.state === '已归档' && <button className="undo-button" disabled={busy} title={`恢复到 ${entry.source}`} onClick={() => send({ command: 'undo', id: entry.id })}><Undo2 size={15} />撤销</button>}</td></tr>)}</tbody></table></div> : <div className="empty-state"><Clock3 size={36} strokeWidth={1.3} /><h3>{search ? '还没找到这份文件' : '第一份整理记录，等你留下'}</h3><p>{search ? '试试其他文件名或路径。' : '拖入文件后，原位置和归档去向都会记在这里。'}</p></div>}<div className="gentle-tip"><ShieldCheck size={15} />撤销前会核对文件内容；文件已修改或原位置被占用时，会保留当前文件。</div></section>}
      {page === 'settings' && <div className="settings-page"><section className="settings-card"><div className="section-heading"><h3>整理方式</h3><span>设置会自动保存</span></div><div className="setting-row"><span className="setting-icon"><FolderOpen size={20} /></span><div><strong>归档总文件夹</strong><p title={settings.rootFolder}>{settings.rootFolder || '选择一个文件夹，其他交给我们'}</p></div><button className="button subtle" disabled={busy} onClick={() => send({ command: 'pickRoot' })}>更改位置<ArrowUpRight size={15} /></button></div><div className="setting-row"><span className="setting-icon"><FolderCheck size={20} /></span><div><strong>拖入后自动整理</strong><p>开启后，拖到桌面入口会在后台收纳；双击入口查看归档空间。关闭后打开预览。</p></div><Toggle label="拖入后自动整理" checked={settings.autoOrganize} disabled={busy} onChange={() => updateSettings({ autoOrganize: !settings.autoOrganize })} /></div><div className="setting-row"><span className="setting-icon"><CalendarDays size={20} /></span><div><strong>按收纳当天建立目录</strong><p>固定使用「归序 / 年月 / 日期 / 类型」，日期与类型视图共享这份存储。</p></div><span className="status-badge success">自动管理</span></div></section><section className="settings-card classification-card"><div className="section-heading"><div><h3>自动分类</h3><span>根据文件扩展名识别，仅建立用到的目录</span></div></div><div className="all-categories">{Object.entries(categoryInfo).map(([name, info]) => <div key={name}><Glyph category={name} small /><span><strong>{name}</strong><small>{info.examples}</small></span></div>)}</div><div className="folder-example"><Folder size={17} /><span>{settings.rootFolder || '归档总文件夹'}</span><ChevronRight size={14} /><span>归序</span><ChevronRight size={14} /><span>{monthExample}</span><ChevronRight size={14} /><span>{dayExample}</span><ChevronRight size={14} /><span>文档</span><ChevronRight size={14} /><span>项目说明.docx</span></div></section></div>}
      <footer className="main-footer"><span>每份文件，都值得被好好安放。</span><span><ShieldCheck size={14} />本地整理，安心归档</span></footer>
    </main>
    {help && <div className="modal-backdrop" onClick={() => setHelp(false)}><section className="help-modal" role="dialog" aria-modal="true" aria-labelledby="help-title" onClick={e => e.stopPropagation()}><button aria-label="关闭帮助" className="icon-button modal-close" onClick={() => setHelp(false)}><X size={19} /></button><span className="brand-mark"><FolderCheck size={26} /></span><h2 id="help-title">从桌面，到归处。</h2><p>第一次使用，只需这三步。</p><ol><li><b>1</b><div><strong>选择一个归档总文件夹</strong><p>软件建立按收纳日期与类型分层的目录。</p></div></li><li><b>2</b><div><strong>创建桌面入口</strong><p>程序文件夹要放在固定位置。</p></div></li><li><b>3</b><div><strong>把文件拖到图标上</strong><p>后台自动收纳，双击图标查看日期或类型视图。</p></div></li></ol><div className="help-note">文件夹整包收纳并保留内部结构。桌面拖入时后台整理；双击打开归档空间。包含链接或云端占位项的文件夹会保留原位。</div><button className="button primary" onClick={() => setHelp(false)}>开始使用<ArrowRight size={16} /></button></section></div>}
  </div>;
}

createRoot(document.getElementById('root')!).render(<App />);
