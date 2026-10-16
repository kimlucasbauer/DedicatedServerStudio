import React, { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { Check, CircleAlert, LoaderCircle, X } from 'lucide-react';

const ToastContext = createContext(() => {});
const NotificationsContext = createContext({ items: [], unreadCount: 0, markRead: () => {}, markAllRead: () => {}, dismiss: () => {} });
const ConfirmContext = createContext(async () => false);

export function createClientId() {
  const cryptoApi = globalThis.crypto;
  if (typeof cryptoApi?.randomUUID === 'function') return cryptoApi.randomUUID();
  const bytes = new Uint8Array(16);
  if (typeof cryptoApi?.getRandomValues === 'function') cryptoApi.getRandomValues(bytes);
  else for (let index = 0; index < bytes.length; index += 1) bytes[index] = Math.floor(Math.random() * 256);
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export function ToastProvider({ children }) {
  const [items, setItems] = useState(() => {
    try {
      const saved = JSON.parse(localStorage.getItem('dss-notifications-v1') || '[]');
      return Array.isArray(saved) ? saved.slice(0, 100) : [];
    } catch { return []; }
  });
  const [visualItems, setVisualItems] = useState([]);
  const itemsRef = useRef(items);
  const recentMessages = useRef(new Map());
  const visualTimers = useRef(new Map());
  const update = useCallback(mutator => setItems(current => {
    const next = mutator(current);
    itemsRef.current = next;
    return next;
  }), []);
  const dismiss = useCallback(id => update(current => current.filter(item => item.id !== id)), [update]);
  const dismissVisual = useCallback(id => {
    const timer = visualTimers.current.get(id);
    if (timer) clearTimeout(timer);
    visualTimers.current.delete(id);
    setVisualItems(current => current.filter(item => item.id !== id));
  }, []);
  const markRead = useCallback(id => update(current => current.map(item => item.id === id ? { ...item, read: true } : item)), [update]);
  const markAllRead = useCallback(() => update(current => current.map(item => ({ ...item, read: true }))), [update]);
  const toast = useCallback((message, kind = 'success', options = {}) => {
    const key = options.key;
    if (kind === 'dismiss') {
      if (key) { dismiss(key); dismissVisual(key); }
      return;
    }
    const text = String(message ?? '');
    const now = Date.now();
    const identity = `${kind}:${text}`;
    const id = key || createClientId();
    const recentDuplicate = now - (recentMessages.current.get(identity) || 0) < 1400;
    if (recentDuplicate && !key) return;
    recentMessages.current.set(identity, now);
    const previous = itemsRef.current.find(item => item.id === id);
    const repeated = !key && itemsRef.current.find(item => item.kind === kind && item.message === text && now - item.updatedAt < 10000);
    const item = {
      id: repeated?.id || id,
      kind,
      title: options.title || (kind === 'error' ? 'A operação precisa de atenção' : kind === 'progress' ? 'Em andamento' : 'Concluído'),
      message: text,
      detail: options.detail || '',
      modDetails: Array.isArray(options.modDetails) ? options.modDetails.slice(0, 24).map(item => ({
        provider: String(item?.provider || ''), packageId: String(item?.packageId || ''), name: String(item?.name || '')
      })).filter(item => item.provider && item.packageId) : [],
      progress: Number.isFinite(options.progress) ? Math.max(0, Math.min(100, options.progress)) : null,
      progressText: options.progressText || '',
      createdAt: previous?.createdAt || repeated?.createdAt || now,
      updatedAt: now,
      read: repeated ? false : previous?.read || false
    };
    update(current => [item, ...current.filter(existing => existing.id !== item.id)].slice(0, 100));
    const previousTimer = visualTimers.current.get(item.id);
    if (previousTimer) clearTimeout(previousTimer);
    setVisualItems(current => [item, ...current.filter(existing => existing.id !== item.id)].slice(0, 4));
    const duration = Number.isFinite(options.durationMs) ? options.durationMs : kind === 'error' ? 9000 : kind === 'progress' ? 0 : 6500;
    if (duration > 0) visualTimers.current.set(item.id, setTimeout(() => dismissVisual(item.id), duration));
    else visualTimers.current.delete(item.id);
  }, [dismiss, dismissVisual, update]);
  useEffect(() => {
    try { localStorage.setItem('dss-notifications-v1', JSON.stringify(items)); } catch { /* Storage can be unavailable in restricted browser contexts. */ }
  }, [items]);
  useEffect(() => {
    const onRequestError = event => {
      const message = event.detail?.message;
      if (message) toast(message, 'error', { title: 'Falha na solicitação' });
    };
    window.addEventListener('dss-api-error', onRequestError);
    return () => window.removeEventListener('dss-api-error', onRequestError);
  }, [toast]);
  const notificationValue = { items, unreadCount: items.filter(item => !item.read).length, markRead, markAllRead, dismiss };
  useEffect(() => () => { for (const timer of visualTimers.current.values()) clearTimeout(timer); }, []);
  return <ToastContext.Provider value={toast}><NotificationsContext.Provider value={notificationValue}>{children}<div className="toast-stack" aria-live="polite" aria-relevant="additions text">{visualItems.map(item => <article key={item.id} className={`toast ${item.kind}`}>
    <span className="toast-icon">{item.kind === 'error' ? <CircleAlert size={18}/> : item.kind === 'progress' ? <LoaderCircle size={18} className="spin"/> : <Check size={18}/>}</span>
    <span className="toast-copy"><strong>{item.title}</strong><span>{item.message}</span>{item.detail && <small>{item.detail}</small>}{item.progress !== null && <span className="toast-progress-wrap"><i className="toast-progress"><i style={{ width: `${item.progress}%` }}/></i>{item.progressText && <small className="toast-progress-text">{item.progressText}</small>}</span>}</span>
    <button type="button" aria-label="Fechar aviso" onClick={() => dismissVisual(item.id)}><X size={16}/></button>
  </article>)}</div></NotificationsContext.Provider></ToastContext.Provider>;
}
export const useToast = () => useContext(ToastContext);
export const useNotifications = () => useContext(NotificationsContext);

export function ConfirmProvider({ children }) {
  const [dialog, setDialog] = useState(null);
  const resolver = useRef(null);
  const confirm = options => new Promise(resolve => { resolver.current = resolve; setDialog(options); });
  const settle = value => { resolver.current?.(value); resolver.current = null; setDialog(null); };
  useEffect(() => {
    if (!dialog) return;
    const key = event => { if (event.key === 'Escape') settle(false); };
    window.addEventListener('keydown', key); return () => window.removeEventListener('keydown', key);
  }, [dialog]);
  return <ConfirmContext.Provider value={confirm}>{children}{dialog && <div className="modal-backdrop" onMouseDown={event => { if (event.target === event.currentTarget) settle(false); }}><section className="modal-card" role="dialog" aria-modal="true"><header className="modal-head"><div><h2>{dialog.title}</h2>{dialog.description && <p>{dialog.description}</p>}</div><button className="icon-btn" aria-label="Fechar" onClick={() => settle(false)}><X size={18}/></button></header>{dialog.content && <div className="modal-content">{dialog.content}</div>}<footer className="modal-actions"><button className="button secondary" onClick={() => settle(false)}>{dialog.cancelLabel || 'Cancelar'}</button>{dialog.confirmHoldMs ? <LongPressButton variant={dialog.danger ? 'danger' : 'primary'} className="confirm-longpress" durationMs={dialog.confirmHoldMs} disabled={dialog.confirmDisabled} title={`Segure por ${dialog.confirmHoldMs / 1000} segundos para confirmar`} onLongPress={() => settle(true)}>{dialog.confirmLabel || 'Confirmar'}</LongPressButton> : <button className={`button ${dialog.danger ? 'danger' : 'primary'}`} disabled={dialog.confirmDisabled} onClick={() => settle(true)}>{dialog.confirmLabel || 'Confirmar'}</button>}</footer></section></div>}</ConfirmContext.Provider>;
}
export const useConfirm = () => useContext(ConfirmContext);

export function Button({ children, variant = 'secondary', size, className = '', ...props }) {
  return <button className={`button ${variant} ${size || ''} inline-flex items-center justify-center gap-2 rounded-xl px-4 py-2.5 font-semibold transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-dss-green disabled:opacity-50 ${className}`} {...props}>{children}</button>;
}
export function Card({ children, className = '', ...props }) { return <section className={`panel-card min-w-0 rounded-2xl border border-dss-line bg-dss-800 p-5 ${className}`} {...props}>{children}</section>; }
export function PageHeading({ eyebrow, title, description, actions, back }) {
  return <div className="page-heading flex flex-wrap items-end justify-between gap-4"><div>{back && <Link className="back-link" to={back.href}>{back.label}</Link>}{eyebrow && <div className="eyebrow">{eyebrow}</div>}<h1>{title}</h1>{description && <p>{description}</p>}</div>{actions && <div className="heading-actions">{actions}</div>}</div>;
}
export function Empty({ title, description, action }) { return <div className="empty-state"><span className="empty-orb">✳</span><h3>{title}</h3>{description && <p>{description}</p>}{action}</div>; }
export function Spinner({ label = 'Carregando…' }) { return <div className="loading-state"><LoaderCircle className="spin" size={24}/><span>{label}</span></div>; }

export function LongPressButton({ children, onLongPress, disabled, className = '', durationMs = 1600, title, variant = 'secondary' }) {
  const timer = useRef(); const done = useRef(false); const [holding, setHolding] = useState(false);
  const clear = () => { clearTimeout(timer.current); timer.current = null; setHolding(false); };
  useEffect(() => () => clearTimeout(timer.current), []);
  const begin = event => {
    if (disabled || (event.pointerType === 'mouse' && event.button !== 0)) return;
    event.preventDefault(); done.current = false; setHolding(true);
    event.currentTarget.setPointerCapture?.(event.pointerId);
    timer.current = setTimeout(async () => {
      timer.current = null;
      done.current = true;
      try { await onLongPress?.(); } finally { setHolding(false); }
    }, durationMs);
  };
  const release = () => { if (!done.current) clear(); };
  const help = title || `Segure por ${Number.isInteger(durationMs / 1000) ? durationMs / 1000 : (durationMs / 1000).toFixed(1).replace('.', ',')} segundos para confirmar`;
  return <button type="button" disabled={disabled} title={help} aria-label={help} style={{'--longpress-duration': `${durationMs}ms`}} className={`button ${variant} longpress ${className} ${holding ? 'holding' : ''}`} onPointerDown={begin} onPointerUp={release} onPointerCancel={clear} onPointerLeave={release} onLostPointerCapture={release} onContextMenu={event => event.preventDefault()} onClick={event => { if (!done.current) event.preventDefault(); }}><span className="longpress-progress" aria-hidden="true"/><span>{children}</span></button>;
}
