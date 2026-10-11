import { useEffect, useMemo, useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { Archive, ArrowDownToLine, CalendarDays, ChevronRight, FolderOpen, LayoutGrid, RefreshCw, Search, ShieldCheck, Trash2, X } from 'lucide-react';
import { categoryInfo, Glyph } from './FileGlyph';

export type ArchiveItem = { id: string; fileName: string; path: string; category: string; date: string; isDirectory: boolean };
type Props = { library: ArchiveItem[]; rootFolder: string; busy: boolean; send(message: { command: string; [key: string]: unknown }): void; onOrganize(): void };

function dateLabel(value: string) {
  const date = new Date(`${value}T12:00:00`);
  const today = new Date();
  const yesterday = new Date(today);
  yesterday.setDate(today.getDate() - 1);
  const localDay = (day: Date) => `${day.getFullYear()}-${String(day.getMonth() + 1).padStart(2, '0')}-${String(day.getDate()).padStart(2, '0')}`;
  const relative = value === localDay(today) ? '今天' : value === localDay(yesterday) ? '昨天' : '';
  return { short: relative || `${date.getMonth() + 1}月${date.getDate()}日`, full: value, weekday: date.toLocaleDateString('zh-CN', { weekday: 'long' }) };
}

function ArchiveRow({ item, showDate, busy, send, selected, select, remove }: { item: ArchiveItem; showDate?: boolean; busy: boolean; send: Props['send']; selected: boolean; select(): void; remove(): void }) {
  return <div className="archive-row" data-category={item.category} data-date={item.date}>
    <input className="material-checkbox" type="checkbox" aria-label={`选择材料：${item.fileName}`} checked={selected} disabled={busy} onChange={select} />
    <Glyph category={item.isDirectory ? '文件夹' : item.category} small />
    <div className="archive-name"><strong title={item.path}>{item.fileName}</strong><small>{item.isDirectory ? '文件夹 · 内部结构完整保留' : `${item.category} · ${item.fileName.includes('.') ? item.fileName.split('.').at(-1)?.toUpperCase() : '无扩展名'}`}</small></div>
    {showDate && <span className="row-date" title="收纳日期">{item.date}</span>}
    <button className="button subtle reveal-button" title="打开所在文件夹，并选中这份材料" aria-label={`打开位置：${item.fileName}`} disabled={busy} onClick={() => send({ command: 'reveal', id: item.id })}><FolderOpen size={14} /><span>打开位置</span></button>
    <button className="button subtle remove-material" title="移入 Windows 回收站" aria-label={`删除材料：${item.fileName}`} disabled={busy} onClick={remove}><Trash2 size={14} /><span>删除</span></button>
  </div>;
}

function RecycleConfirmation({ items, busy, cancel, confirm }: { items: ArchiveItem[]; busy: boolean; cancel(): void; confirm(): void }) {
  const dialog = useRef<HTMLDivElement>(null);
  const cancelButton = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    const overflow = document.documentElement.style.overflow;
    document.documentElement.style.overflow = 'hidden';
    cancelButton.current?.focus();
    function keydown(event: KeyboardEvent) {
      if (event.key === 'Escape') { event.preventDefault(); cancel(); }
      if (event.key === 'Tab') {
        const buttons = [...dialog.current!.querySelectorAll<HTMLButtonElement>('button')].filter(button => !button.disabled);
        const first = buttons[0], last = buttons.at(-1)!;
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
      }
    }
    document.addEventListener('keydown', keydown);
    return () => { document.removeEventListener('keydown', keydown); document.documentElement.style.overflow = overflow; if (previous?.isConnected) previous.focus(); };
  }, [cancel]);
  const folders = items.filter(item => item.isDirectory).length;
  return <div className="modal-backdrop recycle-backdrop" onMouseDown={event => { if (event.target === event.currentTarget) cancel(); }}>
    <div className="recycle-dialog" ref={dialog} role="dialog" aria-modal="true" aria-labelledby="recycle-title" aria-describedby="recycle-description">
      <span className="recycle-symbol"><Trash2 size={24} /></span>
      <h2 id="recycle-title">将{items.length === 1 ? '这份' : `这 ${items.length} 份`}材料移入回收站？</h2>
      <p id="recycle-description">材料会从归档空间移除。如需恢复，可在 Windows 回收站还原，再点击归序的「刷新」。</p>
      <ul>{items.slice(0, 5).map(item => <li key={item.id}><Glyph category={item.isDirectory ? '文件夹' : item.category} small /><span title={item.path}>{item.fileName}</span>{item.isDirectory && <small>整个文件夹</small>}</li>)}{items.length > 5 && <li className="more-materials">还有 {items.length - 5} 份材料</li>}</ul>
      {folders > 0 && <p className="folder-removal-note">包含 {folders} 个文件夹，里面的所有文件会一起移入回收站。</p>}
      <div className="recycle-actions"><button ref={cancelButton} className="button subtle" onClick={cancel}>取消</button><button className="button danger" aria-label="确认移入回收站" disabled={busy} onClick={confirm}><Trash2 size={15} />移入回收站</button></div>
    </div>
  </div>;
}

export function ArchiveBrowser({ library, rootFolder, busy, send, onOrganize }: Props) {
  const [view, setView] = useState<'date' | 'type'>('date');
  const requestedView = useRef(view);
  const transition = useRef<ViewTransition | null>(null);
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; transition.current?.skipTransition(); };
  }, []);
  function changeView(next: 'date' | 'type') {
    if (requestedView.current === next) return;
    requestedView.current = next;
    transition.current?.skipTransition();
    document.documentElement.dataset.archiveDirection = next === 'type' ? 'left' : 'right';
    if (typeof document.startViewTransition !== 'function' || window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
      setView(next);
      return;
    }
    const active = document.startViewTransition(() => {
      // A newer click supersedes a snapshot that has not finished capturing yet.
      if (mounted.current && requestedView.current === next) flushSync(() => setView(next));
    });
    transition.current = active;
    void active.ready.catch(() => {});
    void active.finished.catch(() => {}).then(() => {
      if (transition.current === active) transition.current = null;
    });
  }
  const [selectedDate, setSelectedDate] = useState('');
  const [selectedCategory, setSelectedCategory] = useState('');
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [removing, setRemoving] = useState<ArchiveItem[]>([]);
  const cancelRemoval = useRef(() => setRemoving([])).current;
  useEffect(() => { setSelected(new Set()); setRemoving([]); }, [rootFolder, selectedDate, selectedCategory, search]);
  useEffect(() => {
    setSelected(current => new Set([...current].filter(id => library.some(item => item.id === id))));
    setRemoving(current => current.length && current.some(item => !library.some(entry => entry.id === item.id && entry.path === item.path)) ? [] : current);
  }, [library]);
  useEffect(() => { setSelectedDate(''); setSelectedCategory(''); setSearch(''); }, [rootFolder]);
  const dates = useMemo(() => [...new Set(library.map(item => item.date))].sort((a, b) => b.localeCompare(a)), [library]);
  const months = [...new Set(dates.map(date => date.slice(0, 7)))];
  const categories = Object.keys(categoryInfo).filter(category => library.some(item => item.category === category));
  const query = search.trim().toLocaleLowerCase();
  const searched = library.filter(item => `${item.fileName} ${item.path}`.toLocaleLowerCase().includes(query));
  const inDate = searched.filter(item => !selectedDate || item.date === selectedDate);
  const inCategory = searched.filter(item => !selectedCategory || item.category === selectedCategory);
  const visible = inDate.filter(item => !selectedCategory || item.category === selectedCategory).sort((a, b) => b.date.localeCompare(a.date) || a.fileName.localeCompare(b.fileName, 'zh-CN'));
  const visibleDates = dates.filter(date => visible.some(item => item.date === date));
  const hasFilters = Boolean(selectedDate || selectedCategory || search);
  const categoryCount = (category: string) => inDate.filter(item => item.category === category).length;
  const dateCount = (date: string) => inCategory.filter(item => item.date === date).length;
  function reset() { setSelectedDate(''); setSelectedCategory(''); setSearch(''); }
  function select(id: string) { setSelected(current => { const next = new Set(current); if (!next.delete(id)) next.add(id); return next; }); }
  const selectedItems = visible.filter(item => selected.has(item.id));
  const allSelected = visible.length > 0 && selectedItems.length === visible.length;
  function row(item: ArchiveItem, showDate = false) { return <ArchiveRow key={item.path} item={item} showDate={showDate} busy={busy} send={send} selected={selected.has(item.id)} select={() => select(item.id)} remove={() => setRemoving([item])} />; }

  const emptyState = <div className="empty-state"><Archive size={36} strokeWidth={1.3} /><h3>{library.length ? '这个范围里，还没有材料' : '你的归档空间，准备好了'}</h3><p>{library.length ? '换一个日期或类型，或者清除筛选查看全部材料。' : '把文件或文件夹拖到桌面入口，收下的材料会出现在这里。'}</p><button className="button subtle" onClick={library.length ? reset : onOrganize}>{library.length ? '清除筛选' : '开始收纳'}<ChevronRight size={15} /></button></div>;

  return <section className={`library-section view-${view}`}>
    {removing.length > 0 && <RecycleConfirmation items={removing} busy={busy} cancel={cancelRemoval} confirm={() => { send({ command: 'recycleMaterials', ids: removing.map(item => item.id), rootFolder }); setRemoving([]); setSelected(new Set()); }} />}
    <div className="library-toolbar">
      <div className="view-switch" role="group" aria-label="归档视图">
        <button className={view === 'date' ? 'selected' : ''} aria-pressed={view === 'date'} onClick={() => changeView('date')}><CalendarDays size={16} />按日期</button>
        <button className={view === 'type' ? 'selected' : ''} aria-pressed={view === 'type'} onClick={() => changeView('type')}><LayoutGrid size={16} />按类型</button>
      </div>
      <label className="search-box"><Search size={16} /><input aria-label="搜索归档空间" placeholder="搜索材料名称或路径" value={search} onChange={e => setSearch(e.target.value)} />{search && <button className="clear-search" aria-label="清空搜索" onClick={() => setSearch('')}><X size={13} /></button>}</label>
      <button className="button subtle" aria-label="刷新" title="更新材料清单并刷新当前显示" disabled={busy} onClick={() => send({ command: 'refreshLibrary' })}><RefreshCw size={14} />刷新</button>
      <button className="button primary" onClick={onOrganize}><ArrowDownToLine size={15} />收纳新材料</button>
    </div>
    {visible.length > 0 && <div className="material-selection"><label><input className="material-checkbox" type="checkbox" aria-label="全选当前结果" checked={allSelected} disabled={busy} onChange={() => setSelected(allSelected ? new Set() : new Set(visible.map(item => item.id)))} />全选当前结果</label><span>{selectedItems.length ? `已选 ${selectedItems.length} 份材料` : '勾选材料可批量删除'}</span><button className="button subtle" disabled={busy || selectedItems.length === 0} onClick={() => setRemoving(selectedItems)}><Trash2 size={14} />删除所选{selectedItems.length > 0 ? `（${selectedItems.length}）` : ''}</button></div>}
    <div className="archive-view-viewport">
    <div className={`archive-view-panel ${typeof document.startViewTransition === 'function' ? '' : 'view-fallback'}`} key={view}>
    <div className="browse-summary"><div><h2>{view === 'date' ? '按天回顾收下的材料' : '从分类里找到需要的材料'}</h2><p>{view === 'date' ? `共 ${library.length} 份材料，分布在 ${dates.length} 个收纳日` : '选择一张分类卡片，可跨日期查看同类材料'}</p></div>{hasFilters && <button className="reset-filters" onClick={reset}><X size={13} />清除筛选</button>}</div>
    {view === 'date' ? <div className="date-browser">
      <aside className="date-rail" aria-label="按收纳日筛选"><div className="group-caption">收纳日期</div><button className={`all-days ${!selectedDate ? 'selected' : ''}`} aria-pressed={!selectedDate} onClick={() => setSelectedDate('')}><CalendarDays size={15} /><span>全部日期</span><b>{inCategory.length}</b></button>
        {months.map(month => <div className="month-section" key={month}><h3>{month.replace('-', '年')}月</h3>{dates.filter(date => date.startsWith(month)).map(date => { const label = dateLabel(date); return <button key={date} className={selectedDate === date ? 'selected' : ''} aria-pressed={selectedDate === date} data-date={date} onClick={() => setSelectedDate(date)}><span><strong>{label.short}</strong><small>{label.weekday}</small></span><b>{dateCount(date)}</b></button>; })}</div>)}
      </aside>
      <div className="date-content"><div className="type-chips" role="group" aria-label="筛选文件类型"><button aria-pressed={!selectedCategory} className={!selectedCategory ? 'selected' : ''} onClick={() => setSelectedCategory('')}>全部类型 <b>{inDate.length}</b></button>{categories.map(category => <button aria-pressed={selectedCategory === category} className={selectedCategory === category ? 'selected' : ''} key={category} data-category={category} onClick={() => setSelectedCategory(category)}>{category} <b>{categoryCount(category)}</b></button>)}</div>
        <div className="results-caption" role="status">{selectedDate ? `${selectedDate} 收纳的` : '所有日期的'}{selectedCategory || '全部'}材料<span>{visible.length} 份</span></div>
        <div className="date-timeline">{visible.length ? visibleDates.map(date => { const dayItems = visible.filter(item => item.date === date); const label = dateLabel(date); return <section className="timeline-day library-group" key={date}><header><div className="day-stamp"><span>{date.slice(5, 7)}月</span><strong>{date.slice(8)}</strong></div><div><h3>{label.short}<small>{label.full} · {label.weekday}</small></h3><p>{[...new Set(dayItems.map(item => item.category))].map(category => `${category} ${dayItems.filter(item => item.category === category).length}`).join(' · ')}</p></div><span className="day-total">{dayItems.length} 份材料</span></header><div className="day-materials">{dayItems.map(item => row(item))}</div></section>; }) : emptyState}</div>
      </div>
    </div> : <>
      <div className="type-date-filter"><label><CalendarDays size={15} />收纳日期<select aria-label="筛选收纳日期" value={selectedDate} onChange={event => setSelectedDate(event.target.value)}><option value="">全部日期</option>{dates.map(date => <option key={date} value={date}>{date} · {dateLabel(date).short}</option>)}</select></label><span>日期和类型筛选会在切换视图时保留</span></div>
      <div className="type-card-grid" role="group" aria-label="选择文件类型">
        <button className={`type-card all-types ${!selectedCategory ? 'selected' : ''}`} aria-pressed={!selectedCategory} onClick={() => setSelectedCategory('')}><span className="file-glyph gray"><LayoutGrid size={21} /></span><span className="type-card-name"><strong>全部类型</strong><small>查看所有材料</small></span><b>{inDate.length}<small>份</small></b></button>
        {categories.map(category => <button className={`type-card ${selectedCategory === category ? 'selected' : ''}`} aria-pressed={selectedCategory === category} data-category={category} key={category} onClick={() => setSelectedCategory(category)}><Glyph category={category} /><span className="type-card-name"><strong>{category}</strong><small>{categoryInfo[category].examples}</small></span><b>{categoryCount(category)}<small>份</small></b></button>)}
      </div>
      <div className="type-results"><div className="type-results-heading"><div>{selectedCategory ? <Glyph category={selectedCategory} small /> : <LayoutGrid size={18} />}<h3>{selectedCategory || '全部材料'}</h3><span>{visible.length} 份 · {selectedDate || '跨所有日期'} · 最近收纳在前</span></div></div>
        {visible.length ? <div className="type-file-list"><div className="list-head"><span>材料</span><span>收纳日期</span><span>操作</span></div>{visible.map(item => row(item, true))}</div> : emptyState}
      </div>
    </>}
    <div className="gentle-tip"><ShieldCheck size={15} />两种视图查看同一份材料。打开位置会进入实际文件夹，并选中材料。</div>
    </div>
    </div>
  </section>;
}
