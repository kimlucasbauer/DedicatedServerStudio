import React, { createContext, useCallback, useContext, useEffect, useId, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Link, NavLink, Route, Routes, useLocation, useNavigate, useParams } from 'react-router-dom';
import {
  Activity, ArrowDownToLine, ArrowLeft, ArrowRight, Ban, Bell, Check, ChevronDown, ChevronLeft, ChevronRight, CircleAlert, Clock3, Code2, Globe2,
  Copy, Cpu, Download, ExternalLink, FileClock, FileCog, Gamepad2, HardDrive, History, Home,
  ImagePlus, LayoutDashboard, List, LoaderCircle, LogOut, Menu, Network, Package, Play, Plus, RefreshCw, Search, Server,
  Lock, Power, Settings, ShieldAlert, ShieldCheck, SlidersHorizontal, Square, Terminal, Trash2, Unlock, Upload, UserCheck, UserMinus, UserRound, Users, X
} from 'lucide-react';
import { api, bytes, fmtDate, humanDuration, json, streamApi } from './api.js';
import { Button, Card, ConfirmProvider, createClientId, Empty, LongPressButton, PageHeading, Spinner, ToastProvider, useConfirm, useNotifications, useToast } from './ui.jsx';
import SteamCommandOutput from './SteamCommandOutput.jsx';
import { copyFullLog, downloadFullLog } from './consoleLogs.js';
import { observeTranslations, translateText } from './i18n.js';

const DssContext = createContext(null);
const useDss = () => useContext(DssContext);
async function copyTextToClipboard(text) {
  if (navigator.clipboard?.writeText && window.isSecureContext) {
    try {
      await navigator.clipboard.writeText(text);
      return;
    } catch { /* Try the legacy command, which also works on HTTP LAN addresses. */ }
  }
  const textarea = document.createElement('textarea');
  textarea.value = text;
  textarea.setAttribute('readonly', '');
  textarea.setAttribute('aria-hidden', 'true');
  textarea.style.position = 'fixed';
  textarea.style.left = '-10000px';
  textarea.style.top = '0';
  textarea.style.opacity = '0';
  document.body.appendChild(textarea);
  let copied = false;
  try {
    textarea.focus({ preventScroll: true });
    textarea.select();
    textarea.setSelectionRange(0, textarea.value.length);
    copied = document.execCommand('copy');
  } finally { textarea.remove(); }
  if (!copied) throw new Error('O navegador bloqueou a cópia. Selecione e copie o JSON manualmente.');
}
const GAME_IMAGE = 'https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/892970/header.jpg';
const providerNames = { thunderstore: 'Thunderstore', hexium: 'Hexium', nexus: 'Nexus Mods', curseforge: 'CurseForge', bepinex: 'BepInEx' };
let catalogSessionCache = {};
let catalogRequestSequence = {};
const configDraftCache = new Map();
const activeConfigFiles = new Map();
const configDraftListeners = new Set();
const configDraftKey = (profileId, file) => `${profileId}:${file}`;
function notifyConfigDrafts() { for (const listener of configDraftListeners) listener(); }
function hasUnsavedProfileConfig(profileId) {
  const prefix = `${profileId}:`;
  return [...configDraftCache.entries()].some(([key, draft]) => key.startsWith(prefix) && draft.content !== draft.saved);
}
function clearProfileConfigDrafts(profileId) {
  const prefix = `${profileId}:`;
  for (const key of configDraftCache.keys()) if (key.startsWith(prefix)) configDraftCache.delete(key);
  activeConfigFiles.delete(profileId);
  notifyConfigDrafts();
}
function useProfileConfigDirty(profileId) {
  const [, setRevision] = useState(0);
  useEffect(() => {
    const update = () => setRevision(revision => revision + 1);
    configDraftListeners.add(update);
    return () => configDraftListeners.delete(update);
  }, []);
  return hasUnsavedProfileConfig(profileId);
}
if (typeof window !== 'undefined') window.addEventListener('beforeunload', event => {
  if ([...configDraftCache.values()].some(draft => draft.content !== draft.saved)) { event.preventDefault(); event.returnValue = ''; }
});
function BrandWords() {
  return <span className="brand-name" aria-label="Dedicated Server Studio"><b><em>D</em>EDICATED</b><b><em>S</em>ERVER</b><span className="studio-word"><em>S</em>tudio</span></span>;
}

function BrandLockup({ sidebar = false, login = false, updateAvailable = false, updateKnown = false, checkingUpdate = false, appVersion = '', latestVersion = '', onOpenUpdate }) {
  return <div className={`brand-lockup ${sidebar ? 'sidebar-brand-lockup' : ''} ${login ? 'login-brand-lockup' : ''}`}>
    <Link to="/" className={`brand-home-link ${login ? 'login-brand-link' : ''}`} aria-label="Dedicated Server Studio"><img src={login ? '/brand/dss-monogram.svg' : '/brand/dss-terminal.svg'} alt=""/>{!login&&<BrandWords/>}</Link>
    {sidebar && <button type="button" className={`brand-update-button ${updateAvailable ? 'available' : updateKnown ? 'current' : ''} ${checkingUpdate ? 'checking' : ''}`} onClick={onOpenUpdate} disabled={!updateKnown || checkingUpdate} aria-haspopup="dialog" aria-label={updateAvailable ? `Atualização do DSS disponível: versão ${latestVersion}` : checkingUpdate ? 'Verificando atualizações do DSS' : updateKnown ? `DSS atualizado · versão ${appVersion}` : 'Ver versão do DSS'} title={updateAvailable ? 'Ver detalhes da atualização do DSS' : checkingUpdate ? 'Verificando atualizações do DSS' : updateKnown ? 'Ver detalhes da versão do DSS' : 'Versão do DSS'}>
      {checkingUpdate ? <LoaderCircle size={16} className="spin"/> : updateAvailable ? <Download size={16}/> : updateKnown ? <Check size={16}/> : <Download size={16}/>}<i aria-hidden="true"/>
    </button>}
  </div>;
}

function ProviderIcon({ provider }) {
  const id = String(provider || '').toLowerCase().replace(/[^a-z]/g, '');
  const label = providerNames[id] || providerNames[id.replace('mods', '')] || provider || 'Provedor desconhecido';
  const logos = {
    thunderstore: 'https://thunderstore.io/favicon.ico',
    hexium: 'https://valheim.hexium.gg/favicon.ico',
    nexus: 'https://www.nexusmods.com/favicon.ico',
    curseforge: 'https://www.curseforge.com/favicon.ico',
    bepinex: 'https://raw.githubusercontent.com/BepInEx/BepInEx/master/assets/logos/logo.svg'
  };
  const logo = Object.entries(logos).find(([key]) => id.includes(key))?.[1];
  return logo ? <img className="provider-glyph" src={logo} alt={label} title={label} loading="lazy" decoding="async"/> : <span className="provider-fallback" title={label}>{label}</span>;
}

function installedProviderInfo(mod) {
  const rawProvider = String(mod.sourceProvider || mod.provider || '').trim().toLowerCase().replace(/[^a-z]/g, '');
  const provider = mod.isFramework || rawProvider === 'bepinex' || rawProvider === 'ts'
    ? 'thunderstore'
    : rawProvider === 'hex' ? 'hexium' : rawProvider === 'nexusmods' ? 'nexus' : rawProvider;
  const packageId = String(mod.packageId || '');
  let projectUrl = String(mod.projectUrl || '').trim();
  if (!/^https:\/\//i.test(projectUrl)) {
    projectUrl = '';
    if (provider === 'thunderstore') {
      const separator = packageId.indexOf('-');
      if (separator > 0 && separator < packageId.length - 1) {
        projectUrl = `https://thunderstore.io/c/valheim/p/${encodeURIComponent(packageId.slice(0, separator))}/${encodeURIComponent(packageId.slice(separator + 1))}/`;
      }
    } else if (provider === 'nexus' && /^\d+$/.test(packageId)) {
      projectUrl = `https://www.nexusmods.com/valheim/mods/${packageId}`;
    } else if (provider === 'curseforge' && packageId) {
      projectUrl = `https://www.curseforge.com/valheim/mods/${encodeURIComponent(packageId)}`;
    }
  }
  return { provider, projectUrl };
}

function App() {
  const [auth, setAuth] = useState({ loading: true, setupRequired: false, authenticated: false });
  const [data, setData] = useState({ dashboard: null, games: [], instances: [] });
  const [initialDataReady, setInitialDataReady] = useState(false);
  const [initialDataError, setInitialDataError] = useState('');
  const [profileImageUrls, setProfileImageUrls] = useState({});
  const [language, setLanguage] = useState(() => document.documentElement.lang || 'en-US');
  const [error, setError] = useState('');
  const toast = useToast();
  const navigate = useNavigate();
  const load = useCallback(async (withGames = false, requestOptions = {}) => {
    const dashboard = await api('/api/dashboard', requestOptions);
    if (dashboard.uiLanguage) { document.documentElement.lang = dashboard.uiLanguage; setLanguage(dashboard.uiLanguage); }
    const games = withGames || !data.games.length ? await api('/api/games', requestOptions) : data.games;
    setData({ dashboard, games, instances: dashboard.instances || [] });
    return dashboard;
  }, [data.games]);
  const setProfileImageUrl = useCallback((profileId, imageUrl) => {
    setProfileImageUrls(current => {
      if (!imageUrl) {
        const next = { ...current };
        delete next[profileId];
        return next;
      }
      return { ...current, [profileId]: imageUrl };
    });
  }, []);
  useEffect(() => {
    document.documentElement.lang = language;
    return observeTranslations(document.body, language);
  }, [language]);
  useEffect(() => {
    let live = true;
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 15000);
    fetch('/api/auth/status', { credentials: 'same-origin', signal: controller.signal }).then(r => r.json()).then(async status => {
      if (!live) return;
      let user = null;
      if (status.authenticated) {
        try { user = await api('/api/auth/me', { suppressFailureToast: true }); } catch {}
      }
      if (!live) return;
      if (user?.uiLanguage) setLanguage(user.uiLanguage);
      setAuth({ loading: false, ...status, user });
      if (status.authenticated) {
        try { await load(true); if (live) setInitialDataReady(true); }
        catch (error) { if (live) setInitialDataError(error.message || 'Não foi possível carregar os dados iniciais do DSS.'); }
      }
    }).catch(() => live && setAuth({ loading: false, setupRequired: false, authenticated: false, unavailable: true })).finally(() => window.clearTimeout(timeout));
    const expired = () => setAuth(current => ({ ...current, authenticated: false }));
    window.addEventListener('dss-auth-expired', expired);
    return () => { live = false; window.clearTimeout(timeout); controller.abort(); window.removeEventListener('dss-auth-expired', expired); };
  }, []);
  useEffect(() => {
    if (!auth.authenticated || auth.user?.role !== 'admin') return;
    const timer = setInterval(() => load(false, { suppressFailureToast: true }).catch(() => {}), 8000);
    return () => clearInterval(timer);
  }, [auth.authenticated, load]);
  const ctx = useMemo(() => ({ ...data, auth, toast, refresh: load, setAuth, setData, language, setLanguage, profileImageUrls, setProfileImageUrl }), [data, auth, toast, load, language, profileImageUrls, setProfileImageUrl]);
  if (auth.loading) return <div className="boot-screen"><Spinner label="Iniciando Dedicated Server Studio"/></div>;
  if (!auth.authenticated) return <DssContext.Provider value={ctx}><AuthScreen unavailable={auth.unavailable} onDone={async () => { const status = await fetch('/api/auth/status').then(r => r.json()); const user = status.authenticated ? await api('/api/auth/me', { suppressFailureToast: true }) : null; if (user?.uiLanguage) setLanguage(user.uiLanguage); setInitialDataReady(false); setInitialDataError(''); setAuth({ loading: false, ...status, user }); if (status.authenticated) { try { await load(true); setInitialDataReady(true); } catch (error) { setInitialDataError(error.message || 'Não foi possível carregar os dados iniciais do DSS.'); } } }}/></DssContext.Provider>;
  if (!initialDataReady) return <main className="boot-screen"><section className="auth-card"><BrandLockup login/><Spinner label="Carregando dados do painel antes de restaurar a página…"/>{initialDataError&&<><div className="inline-error"><CircleAlert size={16}/>{initialDataError}</div><Button variant="primary" className="w-full" onClick={async()=>{setInitialDataError('');try{await load(true);setInitialDataReady(true);}catch(error){setInitialDataError(error.message||'Não foi possível carregar os dados iniciais do DSS.');}}}>Tentar novamente</Button></>}</section></main>;
  return <DssContext.Provider value={ctx}><ConfirmProvider><AppShell/></ConfirmProvider></DssContext.Provider>;
}

function AuthScreen({ onDone, unavailable = false }) {
  const [setup, setSetup] = useState(false); const [username, setUsername] = useState('admin'); const [password, setPassword] = useState(''); const [confirm, setConfirm] = useState(''); const [busy, setBusy] = useState(false); const [error, setError] = useState(''); const [statusUnavailable, setStatusUnavailable] = useState(false);
  useEffect(() => { if (unavailable) { setError('Não foi possível conectar ao serviço DSS. Verifique se o programa está em execução e tente novamente.'); setStatusUnavailable(true); } }, [unavailable]);
  useEffect(() => {
    let active = true;
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 15000);
    fetch('/api/auth/status', { signal: controller.signal }).then(r => r.json()).then(status => { if (active) { setSetup(status.setupRequired); setStatusUnavailable(false); setError(''); } }).catch(() => {
      if (!active) return;
      setError('Não foi possível conectar ao serviço DSS. Verifique se o programa está em execução e tente novamente.');
      setStatusUnavailable(true);
    }).finally(() => window.clearTimeout(timeout));
    return () => { active = false; window.clearTimeout(timeout); controller.abort(); };
  }, []);
  async function submit(event) {
    event.preventDefault(); setError(''); setStatusUnavailable(false);
    if (setup && password !== confirm) return setError('As senhas não coincidem.');
    setBusy(true);
    try { await api(setup ? '/api/auth/setup' : '/api/auth/login', { method: 'POST', body: json({ username, password }) }); await onDone(); }
    catch (e) { setError(e.message); } finally { setBusy(false); }
  }
  return <main className="auth-screen"><form className="auth-card" onSubmit={submit}><BrandLockup login/><div className="eyebrow">PAINEL DE SERVIDORES</div><h1>{setup ? 'Prepare seu painel' : 'Bem-vindo de volta'}</h1><p>{setup ? 'Crie uma senha forte para proteger o acesso local e remoto.' : 'Entre para administrar seus servidores e perfis.'}</p>{!setup && <label>Usuário<input autoFocus required autoComplete="username" value={username} onChange={e => setUsername(e.target.value)}/></label>}<label>{setup ? 'Senha do administrador' : 'Senha'}<input autoFocus={setup} type="password" minLength={setup ? 11 : 1} required autoComplete={setup ? 'new-password' : 'current-password'} placeholder={setup ? 'Mínimo de 11 caracteres' : 'Sua senha do painel'} value={password} onChange={e => setPassword(e.target.value)}/></label>{setup && <label>Confirme a senha<input type="password" minLength={11} required autoComplete="new-password" value={confirm} onChange={e => setConfirm(e.target.value)}/></label>}{error && <div className="inline-error"><CircleAlert size={16}/>{error}</div>}{statusUnavailable&&<Button type="button" className="w-full" onClick={() => window.location.reload()}><RefreshCw size={15}/> Tentar conectar novamente</Button>}<Button variant="primary" className="w-full" disabled={busy}>{busy ? 'Aguarde…' : setup ? 'Criar painel seguro →' : 'Entrar →'}</Button><div className="auth-foot"><ShieldCheck size={16}/> Seus segredos ficam protegidos neste computador.</div></form></main>;
}

function NotificationCenter() {
  const { language } = useDss();
  const { items, unreadCount, markRead, markAllRead, dismiss } = useNotifications();
  const [open, setOpen] = useState(false);
  const root = useRef(null);
  useEffect(() => {
    if (!open) return;
    const outside = event => { if (!root.current?.contains(event.target)) setOpen(false); };
    const keydown = event => { if (event.key === 'Escape') setOpen(false); };
    document.addEventListener('pointerdown', outside);
    window.addEventListener('keydown', keydown);
    return () => { document.removeEventListener('pointerdown', outside); window.removeEventListener('keydown', keydown); };
  }, [open]);
  const time = value => new Intl.DateTimeFormat(language || 'pt-BR', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(value));
  return <div className="notification-center" ref={root}>
    <button type="button" className="icon-btn notification-btn" aria-label={`Notificações${unreadCount ? `, ${unreadCount} não lidas` : ''}`} aria-expanded={open} onClick={() => setOpen(value => !value)}><Bell size={17}/>{unreadCount > 0 && <i>{unreadCount > 99 ? '99+' : unreadCount}</i>}</button>
    {open && <section className="notification-menu" aria-label="Central de notificações">
      <header><div><h2>Notificações</h2><small>{unreadCount ? `${unreadCount} não lida(s)` : 'Tudo em dia'}</small></div><div>{unreadCount > 0 && <button type="button" onClick={markAllRead}><Check size={14}/> Marcar lidas</button>}{items.some(item => item.read) && <button type="button" onClick={() => items.filter(item => item.read).forEach(item => dismiss(item.id))}><Trash2 size={14}/> Limpar lidas</button>}</div></header>
      <div className="notification-list" aria-live="polite">{items.length ? items.map(item => <article key={item.id} className={`notification-item ${item.kind} ${item.read ? 'read' : 'unread'} ${item.modDetails?.length ? 'has-mod-details' : ''}`}>
        <button type="button" className="notification-item-content" aria-label={item.modDetails?.length ? `${item.message} Abrir detalhes do mod.` : item.message} onClick={() => { markRead(item.id); if (item.modDetails?.length) window.dispatchEvent(new CustomEvent('dss-open-mod-details', { detail: { mods: item.modDetails } })); }}>
          <span className="notification-item-icon">{item.kind === 'error' ? <CircleAlert size={17}/> : item.kind === 'progress' ? <LoaderCircle size={17} className="spin"/> : <Check size={17}/>}</span>
          <span className="notification-item-copy"><span className="notification-item-title"><b>{translateText(item.title, language)}</b><time>{time(item.updatedAt || item.createdAt)}</time></span><span>{translateText(item.message, language)}</span>{item.detail && <small>{translateText(item.detail, language)}</small>}{item.progress !== null && <span className="notification-progress"><i style={{ width: `${item.progress}%` }}/></span>}{item.progressText && <small>{translateText(item.progressText, language)}</small>}</span>
        </button>
        <button type="button" className="notification-dismiss" aria-label="Excluir notificação" title="Excluir notificação" onClick={() => dismiss(item.id)}><X size={15}/></button>
      </article>) : <div className="notification-empty"><Bell size={22}/><b>Sem notificações</b><span>As atualizações e avisos do DSS aparecerão aqui.</span></div>}</div>
    </section>}
  </div>;
}

function AppShell() {
  const { auth, setAuth, language, dashboard } = useDss(); const [mobileOpen, setMobileOpen] = useState(false); const [accountMenuOpen,setAccountMenuOpen]=useState(false); const [myProfileOpen,setMyProfileOpen]=useState(false); const [elevationOpen,setElevationOpen]=useState(false); const [elevationStatus,setElevationStatus]=useState(null); const [elevationBusy,setElevationBusy]=useState(false); const [elevationError,setElevationError]=useState(''); const [appVersion,setAppVersion]=useState('…'); const [appStartupId,setAppStartupId]=useState(''); const [appUpdate,setAppUpdate]=useState(null); const [appUpdateChecking,setAppUpdateChecking]=useState(false); const [appUpdateOpen,setAppUpdateOpen]=useState(false); const [appUpdateBusy,setAppUpdateBusy]=useState(false); const [appUpdateProgress,setAppUpdateProgress]=useState(null); const [appUpdateError,setAppUpdateError]=useState(''); const accountMenuRef=useRef(null); const elevationStartupIdRef=useRef(null); const updateResumeRef=useRef(false); const location = useLocation();
  useEffect(() => {
    let active = true;
    fetch('/api/health', { cache:'no-store' }).then(response => response.ok ? response.json() : null).then(status => {
      if (active && status?.version) { setAppVersion(status.version); setAppStartupId(status.startupId || ''); }
    }).catch(() => {});
    return () => { active = false; };
  }, []);
  useEffect(() => {
    if (!auth.authenticated || auth.user?.role !== 'admin') { setAppUpdate(null); return; }
    let active = true;
    setAppUpdateChecking(true);
    api('/api/app/update', { suppressFailureToast:true }).then(result => { if (active) setAppUpdate(result); }).catch(() => {}).finally(() => { if (active) setAppUpdateChecking(false); });
    return () => { active = false; };
  }, [auth.authenticated, auth.user?.role]);
  useEffect(() => {
    if (!auth.authenticated || auth.user?.role !== 'admin' || !appStartupId || updateResumeRef.current) return;
    let operationId = '';
    try { operationId = sessionStorage.getItem('dss-update-operation') || ''; } catch {}
    if (!operationId) return;
    updateResumeRef.current = true;
    let active = true;
    let previousStartupId = '';
    let targetVersion = '';
    try {
      previousStartupId = sessionStorage.getItem('dss-update-startup') || '';
      targetVersion = sessionStorage.getItem('dss-update-target') || '';
    } catch {}
    setAppUpdateBusy(true);
    setAppUpdateOpen(true);
    const deadline = Date.now() + 45 * 60 * 1000;
    const wait = milliseconds => new Promise(resolve => window.setTimeout(resolve, milliseconds));
    const showRestartProgress = () => setAppUpdateProgress(current => current ? ({
      ...current,
      phase:'restarting',
      percent:Math.max(80, Number(current.percent) || 0),
      message:'O DSS está reiniciando para aplicar a versão validada.'
    }) : current);
    async function resumeUpdate() {
      while (active && Date.now() < deadline) {
        await wait(1200);
        try {
          const progress = await api(`/api/app/update/progress?operationId=${encodeURIComponent(operationId)}`, { suppressFailureToast:true });
          if (!active) return;
          setAppUpdateProgress(progress);
          targetVersion = progress.latestVersion || targetVersion;
          if (progress.phase === 'failed') {
            setAppUpdateError(progress.error || 'A atualização falhou. Consulte os logs do DSS para ver os detalhes.');
            sessionStorage.removeItem('dss-update-operation');
            sessionStorage.removeItem('dss-update-startup');
            sessionStorage.removeItem('dss-update-target');
            setAppUpdateBusy(false);
            return;
          }
        } catch { showRestartProgress(); /* O painel fica indisponível enquanto o processo é trocado. */ }
        try {
          const response = await fetch('/api/health', { cache:'no-store' });
          if (response.ok) {
            const health = await response.json();
            if (health.version === targetVersion && health.startupId && health.startupId !== previousStartupId && appVersion !== '…') {
              sessionStorage.removeItem('dss-update-operation');
              sessionStorage.removeItem('dss-update-startup');
              sessionStorage.removeItem('dss-update-target');
              window.location.reload();
              return;
            }
          }
        } catch { /* O DSS está reiniciando. */ }
      }
      if (active) {
        sessionStorage.removeItem('dss-update-operation');
        sessionStorage.removeItem('dss-update-startup');
        sessionStorage.removeItem('dss-update-target');
        setAppUpdateError('O painel ainda não confirmou a nova versão. Consulte logs/dss-update-*.log na pasta do DSS antes de tentar abrir o programa novamente.');
        setAppUpdateBusy(false);
      }
    }
    void resumeUpdate();
    return () => { active = false; };
  }, [auth.authenticated, auth.user?.role, appStartupId]);
  useEffect(() => {
    if (!auth.authenticated) return;
    let active = true;
    api('/api/app/elevation/status').then(status => {
      if (!active) return;
      setElevationStatus(status);
      elevationStartupIdRef.current = status.startupId;
      if (status.isAdministrator || auth.user?.role !== 'admin' || !status.startupId) return;
      const promptKey = `dss-elevation-prompt-${status.startupId}`;
      try { if (sessionStorage.getItem(promptKey) === '1') return; } catch { /* Continue without session storage. */ }
      try { sessionStorage.setItem(promptKey, '1'); } catch { /* The prompt remains scoped to this app process. */ }
      setElevationOpen(true);
    }).catch(() => {});
    return () => { active = false; };
  }, [auth.authenticated, auth.user?.role]);
  useEffect(() => {
    if (!accountMenuOpen) return;
    const outside=event=>{if(!accountMenuRef.current?.contains(event.target))setAccountMenuOpen(false);};
    const escape=event=>{if(event.key==='Escape')setAccountMenuOpen(false);};
    document.addEventListener('pointerdown',outside);window.addEventListener('keydown',escape);
    return()=>{document.removeEventListener('pointerdown',outside);window.removeEventListener('keydown',escape);};
  },[accountMenuOpen]);
  const title = location.pathname.startsWith('/games/') ? translateText('Perfil de servidor', language) : ({ '/': 'Home', '/dashboard': 'Dashboard', '/usuarios': 'Usuários e acessos', '/historico': 'Histórico', '/configuracoes': 'Configurações', '/configuracoes/preferencias': 'Preferências', '/integracoes': 'Integrações' })[location.pathname] || 'Dedicated Server Studio';
  const remoteAccess = dashboard?.clientAccess?.isLocal === false;
  const remoteAccessLabel = dashboard?.clientAccess?.network === 'lan' ? 'Conectado pela rede local' : dashboard?.clientAccess?.network === 'public' ? 'Conectado pela internet' : 'Conectado pela rede';
  const hostAddress = window.location.hostname;
  async function logout() { setAccountMenuOpen(false); await api('/api/auth/logout', { method: 'POST', body: '{}' }).catch(() => {}); setAuth(current => ({ ...current, authenticated: false })); }
  async function installDssUpdate() {
    if (!appUpdate?.updateAvailable || !appUpdate.packageAvailable) return;
    setAppUpdateBusy(true); setAppUpdateError('');
    const showRestartProgress = () => setAppUpdateProgress(current => current ? ({
      ...current,
      phase:'restarting',
      percent:Math.max(80, Number(current.percent) || 0),
      message:'O DSS está reiniciando para aplicar a versão validada.'
    }) : current);
    try {
      const accepted = await api('/api/app/update/install', { method:'POST', body:'{}' });
      const operationId = accepted.operationId;
      if (!operationId) throw new Error('O DSS não iniciou uma operação de atualização válida.');
      sessionStorage.setItem('dss-update-operation', operationId);
      sessionStorage.setItem('dss-update-startup', appStartupId || '');
      sessionStorage.setItem('dss-update-target', appUpdate.latestVersion || '');
      setAppUpdateProgress(accepted);
      const deadline = Date.now() + 45 * 60 * 1000;
      while (Date.now() < deadline) {
        await new Promise(resolve => setTimeout(resolve, 1200));
        try {
          const progress = await api(`/api/app/update/progress?operationId=${encodeURIComponent(operationId)}`, { suppressFailureToast:true });
          setAppUpdateProgress(progress);
          if (progress.phase === 'failed') {
            setAppUpdateError(progress.error || 'A atualização falhou. Consulte os logs do DSS para ver os detalhes.');
            sessionStorage.removeItem('dss-update-operation');
            sessionStorage.removeItem('dss-update-target');
            return;
          }
        } catch { showRestartProgress(); /* O endpoint fica indisponível enquanto o painel está reiniciando. */ }
        try {
          const response = await fetch('/api/health', { cache:'no-store' });
          if (!response.ok) continue;
          const health = await response.json();
          if (health.version === appUpdate.latestVersion && (!appStartupId || health.startupId !== appStartupId)) { sessionStorage.removeItem('dss-update-operation'); sessionStorage.removeItem('dss-update-startup'); sessionStorage.removeItem('dss-update-target'); window.location.reload(); return; }
        } catch { /* The app is restarting. */ }
      }
      sessionStorage.removeItem('dss-update-operation');
      sessionStorage.removeItem('dss-update-startup');
      sessionStorage.removeItem('dss-update-target');
      setAppUpdateError('O painel ainda não confirmou a nova versão. Consulte logs/dss-update-*.log na pasta do DSS antes de tentar abrir o programa novamente.');
    } catch (error) { setAppUpdateError(error.message || 'Não foi possível atualizar o DSS.'); }
    finally { setAppUpdateBusy(false); }
  }
  async function restartWithElevation(enabled) {
    setElevationBusy(true);
    setElevationError('');
    try {
      await api(`/api/app/elevation/${enabled ? 'enable' : 'disable'}`, { method:'POST', body:'{}' });
      const previousStartupId = elevationStartupIdRef.current;
      const waitForRestart = async () => {
        const deadline = Date.now() + 90000;
        while (Date.now() < deadline) {
          await new Promise(resolve => setTimeout(resolve, 600));
          try {
            const response = await fetch('/api/health', { cache:'no-store' });
            if (response.ok) {
              const status = await response.json();
              if (status.startupId && status.startupId !== previousStartupId) {
                if (!enabled) { try { sessionStorage.setItem(`dss-elevation-prompt-${status.startupId}`, '1'); } catch { /* The mode switch itself has already completed. */ } }
                window.location.reload();
                return;
              }
            }
          } catch { /* The elevated process is still starting. */ }
        }
        setElevationError('O painel não voltou a responder. Verifique a janela de inicialização e tente novamente.');
        setElevationBusy(false);
      };
      void waitForRestart();
    } catch (error) {
      setElevationError(error.message || 'Não foi possível alterar o modo de execução.');
      setElevationBusy(false);
    }
  }
  const navItems = [[Home, 'Home', '/'], [LayoutDashboard, 'Dashboard', '/dashboard'], ...(auth.user?.role === 'admin' ? [[Users, 'Usuários e acessos', '/usuarios']] : []), [Settings, 'Configurações', '/configuracoes'], [History, 'Histórico', '/historico']];
  return <div className="app-shell flex min-h-screen bg-dss-950 text-dss-text">
    <aside className={`sidebar ${mobileOpen ? 'mobile-open' : ''}`}>
      <BrandLockup sidebar updateAvailable={Boolean(appUpdate?.updateAvailable)} updateKnown={Boolean(appUpdate)} checkingUpdate={appUpdateChecking} appVersion={appVersion} latestVersion={appUpdate?.latestVersion || ''} onOpenUpdate={() => { if (!appUpdate) return; setAppUpdateError(''); setAppUpdateOpen(true); }}/>
      <div className={'host-card ' + (remoteAccess ? 'remote' : 'local')} title={remoteAccess ? remoteAccessLabel + ' · ' + hostAddress : 'O navegador e o painel estão no mesmo computador'}><span className="host-icon">{remoteAccess ? <Network size={18}/> : <Home size={18}/>}</span><div className="host-copy"><div className="host-copy-head"><span className="host-kicker">{remoteAccess ? 'CONEXÃO AO PAINEL' : 'HOST DO PAINEL'}</span><span className="host-access-badge">{remoteAccess ? 'REMOTO' : 'LOCAL'}</span></div><b>{remoteAccess ? 'Servidor remoto' : 'Este computador'}</b><small><i className="status-dot"/>{remoteAccess ? remoteAccessLabel : 'Gerenciado localmente'}</small>{remoteAccess&&<code className="host-address">{hostAddress}</code>}</div></div>
      <span className="nav-label">PAINEL</span><nav className="side-nav">{navItems.map(([Icon, label, to]) => <NavLink key={to} to={to} end={to === '/'} className={({ isActive }) => `nav-item ${isActive ? 'active' : ''}`} onClick={() => setMobileOpen(false)}><Icon size={18}/>{label}</NavLink>)}</nav>
      <span className="nav-label ecosystem-label">ECOSSISTEMA</span><nav className="side-nav"><NavLink to="/integracoes" className={({ isActive }) => `nav-item ${isActive ? 'active' : ''}`} onClick={() => setMobileOpen(false)}><Activity size={18}/>Integrações</NavLink></nav>
      <div className="sidebar-bottom"><div className="safe-card"><ShieldCheck size={18}/><div><b>Protegido por padrão</b><p>O painel remoto só inicia após configurar sua senha.</p></div></div></div>
    </aside>
      <div className="main-area flex min-w-0 flex-1 flex-col"><header className="topbar"><div className="top-leading"><button className="icon-btn mobile-menu" onClick={() => setMobileOpen(!mobileOpen)} aria-label="Menu"><Menu size={20}/></button><div className="breadcrumb"><span>Dedicated Server Studio</span><b>/</b><strong>{title}</strong></div></div><div className="top-actions"><button type="button" className="connection-pill" onClick={()=>{setElevationError('');setElevationOpen(true);}} aria-label="Verificar ou alterar o modo de execução" aria-haspopup="dialog" title={elevationStatus?.isAdministrator?'Executando como administrador':'Executando sem privilégios de administrador'}><i className="pulse"/>{elevationStatus?.isAdministrator ? 'Administrador' : elevationStatus ? 'Sem admin · PowerShell' : 'Verificando modo'}<ChevronDown size={14}/></button><NotificationCenter/><div className="account-menu" ref={accountMenuRef}><button type="button" className="account-menu-trigger" aria-label="Abrir menu da conta" aria-haspopup="menu" aria-expanded={accountMenuOpen} onClick={()=>setAccountMenuOpen(current=>!current)}>{auth.user?.imageUrl?<img src={auth.user.imageUrl} alt=""/>:<span>{(auth.user?.username||'D').slice(0,1).toUpperCase()}</span>}</button>{accountMenuOpen&&<div className="account-menu-popover" role="menu"><div className="account-menu-user"><b>{auth.user?.username||'Usuário'}</b><small>{auth.user?.role==='admin'?'Administrador':'Acesso por perfil'}</small></div><button type="button" role="menuitem" onClick={()=>{setAccountMenuOpen(false);setMyProfileOpen(true);}}><UserRound size={16}/> Meu perfil</button><button type="button" role="menuitem" onClick={logout}><LogOut size={16}/> Sair</button></div>}</div></div></header>{elevationOpen&&<Modal className="admin-elevation-modal" title="Modo de execução do painel" description={elevationStatus?.isAdministrator ? 'O painel está aberto com permissões elevadas.' : 'O painel está aberto com as permissões padrão do Windows.'} close={()=>{if(!elevationBusy)setElevationOpen(false);}}><div className="admin-elevation-content">
        <section className="admin-elevation-card"><span className="admin-elevation-icon"><ShieldCheck size={22}/></span><div><b>{elevationStatus?.isAdministrator ? 'Permissões administrativas ativas' : 'Permissões padrão do Windows'}</b><p>{elevationStatus?.isAdministrator ? 'É possível cadastrar regras no Firewall. Cada servidor continua precisando de autorização individual.' : 'Para cadastrar regras no Firewall, reinicie como administrador. O Windows pedirá confirmação e talvez seja necessário entrar novamente depois.'}</p></div><span className={`state-badge ${elevationStatus?.isAdministrator ? 'running' : 'stopped'}`}>{elevationStatus?.isAdministrator ? 'ADMIN' : 'PADRÃO'}</span></section>
        <div className="notice admin-elevation-note"><ShieldCheck size={16}/><span>{auth.user?.role === 'admin' ? <>A preferência fica salva em <code>dss-config.json</code>, na pasta do programa. Esta tela não cria regras de rede; a autorização continua sendo feita por servidor.</> : 'Somente um administrador da conta pode alterar este modo de execução.'}</span></div>
        {elevationError&&<div className="inline-error"><CircleAlert size={16}/>{elevationError}</div>}
        {elevationBusy&&<div className="admin-elevation-progress"><LoaderCircle size={16} className="spin"/>{elevationStatus?.isAdministrator?'Reiniciando com as permissões padrão…':'Reabrindo o painel como administrador…'}</div>}
        <div className="modal-actions"><Button type="button" disabled={elevationBusy} onClick={()=>setElevationOpen(false)}>Fechar</Button>{auth.user?.role === 'admin' && <Button variant="primary" disabled={elevationBusy} onClick={()=>restartWithElevation(!elevationStatus?.isAdministrator)}><RefreshCw size={16}/>{elevationBusy?'Reiniciando…':elevationStatus?.isAdministrator?'Reiniciar no modo padrão':'Reiniciar como administrador'}</Button>}</div>
      </div></Modal>}{myProfileOpen&&<MyProfileModal user={auth.user} close={()=>setMyProfileOpen(false)} onUserUpdated={updates=>setAuth(current=>({...current,user:{...current.user,...updates}}))}/>}<div className="content" data-read-only={auth.user?.role === 'user' ? 'true' : undefined}><Routes>
      <Route path="/" element={<HomePage/>}/><Route path="/dashboard" element={<DashboardPage/>}/><Route path="/games/:gameId/servers" element={<ProfilesPage/>}/><Route path="/games/:gameId/servers/:profileId/*" element={<ProfileRoute/>}/><Route path="/usuarios" element={auth.user?.role==='admin'?<AdminUsersPage/>:<HomePage/>}/><Route path="/historico" element={<GlobalHistoryPage/>}/><Route path="/configuracoes" element={<SettingsPage preferences={auth.user?.role === 'user'}/>}/><Route path="/configuracoes/preferencias" element={<SettingsPage preferences/>}/><Route path="/integracoes" element={<IntegrationsPage/>}/><Route path="*" element={<HomePage/>}/>
      </Routes></div><footer className="app-footer"><span>DEDICATED SERVER STUDIO <em>DSS · {appVersion}</em></span><span><i className="status-dot"/> Dados neste computador</span></footer></div>
    {appUpdateOpen&&appUpdate&&<Modal className="dss-update-modal" title="Atualização do DSS" description={appUpdate.updateAvailable?`Uma nova versão está disponível: ${appUpdate.latestVersion}.`:'O DSS está atualizado.'} close={()=>{if(!appUpdateBusy)setAppUpdateOpen(false);}}>
      <section className="dss-update-version-card"><span className="dss-update-icon"><Download size={20}/></span><div><small>VERSÃO MAIS RECENTE</small><b>{appUpdate.latestVersion||appVersion}</b><span>Instalada: {appVersion}{appUpdate.publishedAt?` · publicada em ${fmtDate(appUpdate.publishedAt)}`:''}</span></div><span className={`state-badge ${appUpdate.updateAvailable?'running':'stopped'}`}>{appUpdate.updateAvailable?'ATUALIZAÇÃO':'ATUALIZADO'}</span></section>
      {appUpdate.updateAvailable&&appUpdate.releaseNotes&&<section className="dss-update-notes"><h3>{appUpdate.releaseName}</h3><ReleaseNotesMarkdown source={appUpdate.releaseNotes}/></section>}
      {appUpdate.updateAvailable&&!appUpdate.packageAvailable&&<div className="notice dss-update-notice"><CircleAlert size={16}/><span>A release ainda não tem o arquivo <code>DedicatedServerStudio-{appUpdate.latestVersion}-production.zip</code>.</span><a href={appUpdate.releasePageUrl} target="_blank" rel="noreferrer"><ExternalLink size={14}/> Abrir release</a></div>}
      {appUpdate.updateAvailable&&<div className="notice dss-update-notice"><ShieldCheck size={16}/><span>A atualização reinicia apenas o painel. Servidores com console persistente conectado continuam em execução e voltam a aparecer no console e na Gestão quando o painel retornar. Aguarde qualquer operação do SteamCMD terminar.</span></div>}
      {appUpdateError&&<div className="inline-error"><CircleAlert size={16}/>{appUpdateError}</div>}
      {appUpdateBusy&&<DssUpdateProgress progress={appUpdateProgress} version={appUpdate.latestVersion}/>}
      <div className="modal-actions"><Button disabled={appUpdateBusy} onClick={()=>setAppUpdateOpen(false)}>Agora não</Button>{appUpdate.updateAvailable&&appUpdate.packageAvailable&&<Button variant="primary" disabled={appUpdateBusy} onClick={installDssUpdate}><Download size={15}/>{appUpdateBusy?'Atualizando…':'Atualizar e reiniciar'}</Button>}</div>
    </Modal>}
  </div>;
}

const DSS_UPDATE_STEPS = [
  ['checking', 'Validando a atualização'], ['downloading', 'Baixando o pacote'], ['verifying', 'Verificando o pacote'],
  ['extracting', 'Extraindo o pacote'], ['validating', 'Validando os arquivos'], ['staging', 'Preparando a nova versão'],
  ['backing-up', 'Salvando a versão atual'], ['preflight', 'Validando a inicialização da nova versão'],
  ['waiting-for-shutdown', 'Nova versão validada; aguardando a parada do DSS'], ['stopping-host', 'Encerrando o host antigo'],
  ['stopping-launcher', 'Liberando o inicializador antigo'], ['installing', 'Instalando os arquivos preparados'], ['restarting', 'Reiniciando o DSS no PowerShell'],
  ['health-check', 'Confirmando que o DSS iniciou'], ['completed', 'Atualização concluída']
];

function DssUpdateProgress({ progress, version }) {
  const currentIndex = Math.max(0, DSS_UPDATE_STEPS.findIndex(([phase]) => phase === progress?.phase));
  const percent = Math.max(0, Math.min(100, Number(progress?.percent) || 0));
  const currentMessage = progress?.message || DSS_UPDATE_STEPS[currentIndex]?.[1] || 'Preparando a atualização';
  return <section className="dss-update-progress-panel" aria-live="polite">
    <div className="dss-update-progress-heading"><div><b><span>Atualizando para a versão</span> {version}</b><small>{currentMessage}</small></div><strong>{percent}%</strong></div>
    <div className="dss-update-progress-track" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow={percent}><i style={{width:`${percent}%`}}/></div>
    {progress?.downloadedBytes != null&&<small className="dss-update-bytes">{bytes(progress.downloadedBytes)}{progress.totalBytes!=null?` / ${bytes(progress.totalBytes)}`:''}</small>}
    <ol className="dss-update-steps">{DSS_UPDATE_STEPS.map(([phase,label],index)=>{
      const isCurrent=phase===progress?.phase,done=progress?.phase==='completed'||index<currentIndex;
      return <li key={phase} className={isCurrent?'current':done?'done':''}><span>{done?'✓':index+1}</span>{label}</li>;
    })}</ol>
  </section>;
}

function ReleaseNotesMarkdown({ source }) {
  const lines = String(source || '').replace(/\r\n?/g, '\n').split('\n');
  const blocks = [];
  for (let index = 0; index < lines.length;) {
    const line = lines[index];
    if (!line.trim()) { index++; continue; }
    if (/^```/.test(line.trim())) {
      const code = [];
      index++;
      while (index < lines.length && !/^```/.test(lines[index].trim())) code.push(lines[index++]);
      if (index < lines.length) index++;
      blocks.push(<pre key={`code-${index}`}><code>{code.join('\n')}</code></pre>);
      continue;
    }
    const heading = /^(#{1,3})\s+(.+)$/.exec(line);
    if (heading) {
      const Heading = `h${Math.min(4, heading[1].length + 1)}`;
      blocks.push(<Heading key={`heading-${index}`}>{renderMarkdownInline(heading[2], `heading-${index}`)}</Heading>);
      index++;
      continue;
    }
    if (/^\s*[-*+]\s+/.test(line)) {
      const items = [];
      while (index < lines.length && /^\s*[-*+]\s+/.test(lines[index])) items.push(lines[index++].replace(/^\s*[-*+]\s+/, ''));
      blocks.push(<ul key={`list-${index}`}>{items.map((item, itemIndex)=><li key={itemIndex}>{renderMarkdownInline(item, `list-${index}-${itemIndex}`)}</li>)}</ul>);
      continue;
    }
    if (/^\s*\d+[.)]\s+/.test(line)) {
      const items = [];
      while (index < lines.length && /^\s*\d+[.)]\s+/.test(lines[index])) items.push(lines[index++].replace(/^\s*\d+[.)]\s+/, ''));
      blocks.push(<ol key={`ordered-${index}`}>{items.map((item, itemIndex)=><li key={itemIndex}>{renderMarkdownInline(item, `ordered-${index}-${itemIndex}`)}</li>)}</ol>);
      continue;
    }
    const paragraph = [line];
    index++;
    while (index < lines.length && lines[index].trim() && !/^(#{1,3})\s+/.test(lines[index]) && !/^\s*[-*+]\s+/.test(lines[index]) && !/^\s*\d+[.)]\s+/.test(lines[index]) && !/^```/.test(lines[index].trim())) paragraph.push(lines[index++]);
    blocks.push(<p key={`paragraph-${index}`}>{renderMarkdownInline(paragraph.join(' '), `paragraph-${index}`)}</p>);
  }
  return <div className="dss-markdown">{blocks}</div>;
}

function renderMarkdownInline(text, keyPrefix) {
  const parts = String(text).split(/(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`|\[[^\]]+\]\([^)]+\))/g).filter(Boolean);
  return parts.map((part, index) => {
    const key = `${keyPrefix}-${index}`;
    if (part.startsWith('**') && part.endsWith('**')) return <strong key={key}>{part.slice(2, -2)}</strong>;
    if (part.startsWith('*') && part.endsWith('*')) return <em key={key}>{part.slice(1, -1)}</em>;
    if (part.startsWith('`') && part.endsWith('`')) return <code key={key}>{part.slice(1, -1)}</code>;
    const link = /^\[([^\]]+)\]\(([^)]+)\)$/.exec(part);
    if (link) {
      try { const url = new URL(link[2], window.location.origin); if (url.protocol === 'https:' || url.protocol === 'http:') return <a key={key} href={url.href} target="_blank" rel="noreferrer">{link[1]}</a>; } catch {}
    }
    return part;
  });
}

function MyProfileModal({ user, close, onUserUpdated }) {
  const toast=useToast();
  const [busy,setBusy]=useState(false);
  const [imageData,setImageData]=useState('');
  const [preview,setPreview]=useState('');
  const input=useRef(null);
  const visibleImage=preview||user?.imageUrl||'';

  async function chooseImage(file) {
    if(!file)return;
    if(!file.type.startsWith('image/')){toast('Escolha um arquivo de imagem.','error');return;}
    setBusy(true);
    try {
      const source=await new Promise((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(reader.result);reader.onerror=()=>reject(new Error('Não foi possível ler a imagem.'));reader.readAsDataURL(file);});
      const image=await new Promise((resolve,reject)=>{const item=new Image();item.onload=()=>resolve(item);item.onerror=()=>reject(new Error('O arquivo de imagem não pôde ser aberto.'));item.src=source;});
      const side=Math.min(image.width,image.height),x=Math.floor((image.width-side)/2),y=Math.floor((image.height-side)/2),canvas=document.createElement('canvas');
      canvas.width=512;canvas.height=512;canvas.getContext('2d').drawImage(image,x,y,side,side,0,0,512,512);
      const result=canvas.toDataURL('image/jpeg',.88);setImageData(result);setPreview(result);
    } catch(error) { toast(error.message,'error'); }
    finally { setBusy(false); }
  }
  async function saveImage() {
    if(!imageData)return;
    setBusy(true);
    try { const result=await api('/api/auth/me/image',{method:'PUT',body:json({imageData})});onUserUpdated({imageUrl:result.imageUrl});setImageData('');setPreview('');toast('Foto de perfil atualizada.'); }
    catch(error){toast(error.message,'error');}
    finally{setBusy(false);}
  }
  async function removeImage() {
    setBusy(true);
    try { await api('/api/auth/me/image',{method:'DELETE'});onUserUpdated({imageUrl:null});setImageData('');setPreview('');toast('Foto de perfil removida.'); }
    catch(error){toast(error.message,'error');}
    finally{setBusy(false);}
  }
  async function changePassword(event) {
    event.preventDefault();const form=event.currentTarget;const values=Object.fromEntries(new FormData(form));
    if(values.newPassword!==values.confirmPassword){toast('A confirmação da nova senha não confere.','error');return;}
    setBusy(true);
    try { const result=await api('/api/auth/me/password',{method:'PUT',body:json({currentPassword:values.currentPassword,newPassword:values.newPassword})});toast(result.message||'Senha atualizada.');form.reset(); }
    catch(error){toast(error.message,'error');}
    finally{setBusy(false);}
  }

  return <Modal title="Meu perfil" description="Gerencie sua foto e a senha da sua conta do DSS." close={close} className="my-profile-modal">
    <section className="my-profile-photo">
      <div className="my-profile-avatar">{visibleImage?<img src={visibleImage} alt="Prévia da foto de perfil"/>:<UserRound size={34}/>}</div>
      <div className="my-profile-photo-copy"><b>{user?.username||'Usuário'}</b><small>{user?.role==='admin'?'Administrador':'Acesso por perfil'} · A foto é recortada para caber no menu.</small><input ref={input} type="file" accept="image/*" hidden onChange={event=>{void chooseImage(event.target.files?.[0]);event.target.value='';}}/><div className="my-profile-photo-actions"><Button type="button" disabled={busy} onClick={()=>input.current?.click()}><ImagePlus size={15}/>{visibleImage?'Trocar foto':'Adicionar foto'}</Button>{imageData&&<Button type="button" variant="primary" disabled={busy} onClick={saveImage}>{busy?'Salvando…':'Salvar foto'}</Button>}{user?.imageUrl&&!imageData&&<Button type="button" variant="danger" disabled={busy} onClick={removeImage}>Remover foto</Button>}</div></div>
    </section>
    <form className="form-grid my-profile-password" onSubmit={changePassword}>
      <div className="my-profile-section-title"><Lock size={16}/><div><b>Alterar senha</b><small>Use ao menos 11 caracteres.</small></div></div>
      <Field className="full-field" label="Senha atual" name="currentPassword" type="password" autoComplete="current-password" required maxLength="256"/>
      <Field label="Nova senha" name="newPassword" type="password" autoComplete="new-password" required minLength="11" maxLength="256"/>
      <Field label="Confirmar nova senha" name="confirmPassword" type="password" autoComplete="new-password" required minLength="11" maxLength="256"/>
      <div className="modal-actions full-field"><Button type="button" onClick={close}>Fechar</Button><Button variant="primary" disabled={busy}>{busy?'Salvando…':'Atualizar senha'}</Button></div>
    </form>
  </Modal>;
}

function AdminUsersPage() {
  const [users,setUsers]=useState([]),[attempts,setAttempts]=useState([]),[loading,setLoading]=useState(true),[saving,setSaving]=useState(false),[modal,setModal]=useState(null);
  const toast=useToast(),confirm=useConfirm();
  const load=useCallback(async()=>{setLoading(true);try{const [usersResult,securityResult]=await Promise.all([api('/api/users'),api('/api/security/login-attempts')]);setUsers(usersResult.users||[]);setAttempts(securityResult.attempts||[]);}catch(error){toast(error.message,'error');}finally{setLoading(false);}},[toast]);
  useEffect(()=>{void load();},[load]);
  async function create(event){event.preventDefault();const values=Object.fromEntries(new FormData(event.currentTarget));setSaving(true);try{await api('/api/users',{method:'POST',body:json(values)});toast('Usuário criado.');setModal(null);await load();}catch(error){toast(error.message,'error');}finally{setSaving(false);}}
  async function changeRole(user,role){setSaving(true);try{await api('/api/users/'+user.id,{method:'PUT',body:json({role})});toast('Função atualizada.');await load();}catch(error){toast(error.message,'error');}finally{setSaving(false);}}
  async function unlockAttempt(attempt){if(!attempt.attemptedUserId)return;setSaving(true);try{await api('/api/users/'+attempt.attemptedUserId+'/unlock',{method:'POST',body:'{}'});toast(`Bloqueio de ${attempt.attemptedUsername} removido.`);await load();}catch(error){toast(error.message,'error');}finally{setSaving(false);}}
  async function remove(user){if(!await confirm({title:'Remover '+user.username+'?',description:'O usuário perderá o acesso aos perfis que foram compartilhados com ele.',confirmLabel:'Remover usuário',danger:true}))return;try{await api('/api/users/'+user.id,{method:'DELETE'});toast('Usuário removido.');await load();}catch(error){toast(error.message,'error');}}
  return <>
    <PageHeading eyebrow="ACESSO DO DSS" title="Usuários e acessos" description="Crie contas, defina funções e compartilhe cada perfil com as pessoas autorizadas." actions={<Button variant="primary" onClick={()=>setModal('create')}><Plus size={16}/> Novo usuário</Button>}/>
    <Card>
      <div className="panel-head"><div><h2>Contas do painel</h2><p>Administradores gerenciam usuários; os acessos compartilhados podem editar o perfil ou apenas iniciar e reiniciar seus servidores.</p></div><Button onClick={load} disabled={loading}><RefreshCw size={15} className={loading?'spin':''}/> Atualizar</Button></div>
      {loading?<Spinner label="Carregando usuários…"/>:users.length?<div className="dss-user-list">{users.map(user=><article className="dss-user-row" key={user.id}>
        {user.imageUrl?<img className="dss-user-avatar" src={user.imageUrl} alt=""/>:<span className="dss-user-avatar"><UserRound size={18}/></span>}
        <div className="dss-user-main"><b>{user.username}</b><small>{user.accessibleProfiles} perfil(is) compartilhado(s) · desde {fmtDate(user.createdAt)}</small>{user.isLocked&&<span className="login-lock-badge"><Lock size={12}/> Bloqueio permanente · {user.failedLoginCount} falhas</span>}</div>
        <label className="field-label">Função<select className="field" value={user.role} disabled={saving || user.username.toLowerCase()==='admin'} onChange={event=>changeRole(user,event.target.value)}><option value="user">Usuário</option><option value="admin">Administrador</option></select></label>
        <div className="dss-user-actions">{user.role!=='admin'&&<Button variant="danger" size="small" onClick={()=>remove(user)}><Trash2 size={15}/> Remover</Button>}</div>
      </article>)}</div>:<Empty title="Nenhuma conta cadastrada" description="Crie usuários e compartilhe perfis específicos para liberar o acesso."/>}
    </Card>
    <Card className="section-gap">
      <div className="panel-head"><div><h2>Tentativas de login recusadas</h2><p>Cada linha agrupa as falhas pelo usuário informado e pelo endereço de origem. O IP público aparece quando a conexão chega pela internet; na LAN ou via NAT pode ser privado.</p></div><span className="state-badge stopped">VISÍVEL SÓ PARA ADMINISTRADORES</span></div>
      {attempts.length?<div className="login-attempt-table-wrap"><table className="login-attempt-table"><thead><tr><th>Usuário informado</th><th>Dispositivo e endereço</th><th>Tentativas</th><th>Última tentativa</th><th>Ação</th></tr></thead><tbody>{attempts.map(attempt=><tr key={`${attempt.ipAddress}\u0000${attempt.attemptedUsername}`}>
        <td><div className="login-attempt-identity"><span className="login-attempt-icon"><ShieldAlert size={15}/></span><div><b>{attempt.attemptedUsername||'(vazio)'}</b><small>{attempt.accountLocked?'Conta bloqueada':'Acesso recusado'}</small></div></div></td>
        <td><div className="login-attempt-origin"><b>{attempt.deviceName||'Dispositivo não identificado'}</b><code title="Endereço observado pelo servidor">{attempt.ipAddress}</code></div></td>
        <td><span className="login-attempt-count">{Number(attempt.attemptCount||0).toLocaleString()} <small>{attempt.attemptCount===1?'tentativa':'tentativas'}</small></span>{attempt.duringLockoutCount>0&&<small className="login-attempt-after-lock">{attempt.duringLockoutCount} após bloqueio</small>}</td>
        <td><time className="login-attempt-date">{fmtDate(attempt.lastAttemptAt)}</time></td>
        <td>{attempt.accountLocked&&attempt.attemptedUserId?<Button size="small" className="login-attempt-unlock" disabled={saving} onClick={()=>unlockAttempt(attempt)}><Unlock size={14}/> Desbloquear</Button>:<span className="login-attempt-no-lock">Sem bloqueio</span>}</td>
      </tr>)}</tbody></table></div>:<Empty title="Nenhuma tentativa recusada registrada" description="As falhas serão agrupadas por usuário e endereço. O nome do dispositivo depende da disponibilidade de DNS reverso."/>}
    </Card>
    {modal==='create'&&<Modal title="Criar usuário" description="As senhas são armazenadas com derivação criptográfica e não podem ser consultadas depois." close={()=>setModal(null)}><form className="form-grid" onSubmit={create}><Field label="Nome de usuário" name="username" minLength={3} maxLength={32} required autoComplete="off"/><label className="field-label">Função<select className="field" name="role" defaultValue="user"><option value="user">Usuário</option><option value="admin">Administrador</option></select></label><Field label="Senha" name="password" type="password" minLength={11} maxLength={256} required autoComplete="new-password" className="full-field"/><small className="full-field">Use pelo menos 11 caracteres. Letras, números, ponto, hífen e sublinhado são aceitos no nome.</small><div className="modal-actions full-field"><Button type="button" onClick={()=>setModal(null)}>Cancelar</Button><Button variant="primary" disabled={saving}>{saving?'Criando…':'Criar conta'}</Button></div></form></Modal>}
  </>;
}
function ProfileAccessPanel({ profile, readOnly = false }) {
  const [state,setState]=useState(null),[open,setOpen]=useState(false),[draft,setDraft]=useState({}),[busy,setBusy]=useState(false);const toast=useToast();
  const load=useCallback(async()=>{try{setState(await api(`/api/instances/${profile.id}/access`));}catch(error){toast(error.message,'error');}},[profile.id,toast]);
  useEffect(()=>{void load();},[load]);
  async function openAccess(){if(readOnly)return;setBusy(true);try{const result=await api(`/api/instances/${profile.id}/access?includeAvailable=true`);setState(result);setDraft(result.userAccess||Object.fromEntries((result.userIds||[]).map(id=>[id,'edit'])));setOpen(true);}catch(error){toast(error.message,'error');}finally{setBusy(false);}}
  async function save(){setBusy(true);try{await api(`/api/instances/${profile.id}/access`,{method:'PUT',body:json({userAccess:draft})});setState(current=>({...current,userAccess:draft,userIds:Object.keys(draft)}));setOpen(false);toast('Acesso ao perfil atualizado.');}catch(error){toast(error.message,'error');}finally{setBusy(false);}}
  function toggle(id){setDraft(current=>{const next={...current};if(next[id])delete next[id];else next[id]='edit';return next;});}
  function setPermission(id,permission){setDraft(current=>({...current,[id]:permission}));}
  function removeFromProfile(id){setDraft(current=>{const next={...current};delete next[id];return next;});}
  const permitted=(state?.users||[]).filter(user=>state.userAccess?.[user.id]||state.userIds?.includes(user.id));
  const permissionLabel=permission=>permission==='operate'?'Operador':'Editor';
  return <section className="profile-access-panel"><div className="profile-access-heading"><span className="profile-access-icon"><Users size={18}/></span><div><h3>Acesso ao perfil</h3><p>{readOnly?'Usuários associados e suas permissões neste perfil.':'Escolha quem pode editar o perfil ou operar os servidores.'}</p></div>{!readOnly&&<Button className="profile-add-users" size="small" onClick={openAccess} disabled={busy}><Plus size={14}/> Adicionar usuários</Button>}</div>{state===null?<Spinner label="Carregando acessos…"/>:permitted.length?<div className="profile-access-users" aria-label="Usuários com acesso ao perfil">{permitted.map(user=><span className="profile-access-chip" key={user.id}><UserRound size={13}/>{user.username}<small>{permissionLabel(state.userAccess?.[user.id]||'edit')}</small></span>)}</div>:<small className="profile-access-empty">Nenhum usuário recebeu acesso a este perfil.</small>}{open&&!readOnly&&<Modal className="profile-access-editor-modal" title="Usuários com acesso" description="Escolha as contas e defina a permissão concedida neste perfil." close={()=>setOpen(false)}><div className="profile-access-modal-list">{(state?.users||[]).length?state.users.map(user=>{const permission=draft[user.id]||'';return <article className={`profile-access-option ${permission?'selected':''}`} key={user.id}><label className="profile-access-choice"><input type="checkbox" checked={!!permission} disabled={busy} onChange={()=>toggle(user.id)}/><span className="profile-access-option-check"><Check size={13}/></span><span className="profile-access-option-copy"><b>{user.username}</b><small>{permission?permissionLabel(permission):'Sem acesso a este perfil'}</small></span></label><select className="field profile-access-permission" value={permission||'edit'} disabled={!permission||busy} onChange={event=>setPermission(user.id,event.target.value)} aria-label={`Permissão de ${user.username}`}><option value="edit">Editor</option><option value="operate">Operador</option></select>{permission&&<Button className="profile-access-remove" size="small" variant="danger" type="button" disabled={busy} title="Remover do perfil" aria-label="Remover do perfil" onClick={()=>removeFromProfile(user.id)}><UserMinus size={14}/><span>Remover</span></Button>}</article>}):<Empty title="Nenhuma conta disponível" description="Crie uma conta de usuário na seção Usuários e acessos para compartilhá-la aqui."/>}<div className="notice profile-access-explanation"><b>Editor:</b> pode configurar todo o perfil, como um administrador. <b>Operador:</b> pode usar os controles e o console do servidor, incluindo parar, reiniciar, forçar parada e atualizar a instalação; as configurações, mods e usuários do perfil continuam somente para consulta.</div><div className="modal-actions"><Button onClick={()=>setOpen(false)}>Cancelar</Button><Button variant="primary" disabled={busy} onClick={save}>{busy?'Salvando…':'Salvar acesso'}</Button></div></div></Modal>}</section>;
}

function MultiServerPanel({ profile, instances, canEditProfile = true }) {
  const [open,setOpen]=useState(false),[saving,setSaving]=useState(false);
  const toast=useToast(),navigate=useNavigate(),confirm=useConfirm(),{refresh}=useDss();
  const servers=profile.servers||[];
  const nextPort=Math.max(2456,...instances.flatMap(item=>{const endpoints=item.servers?.length?item.servers:[item];const span=Math.max(1,Number(item.portSpan)||Number(profile.portSpan)||2);return endpoints.map(server=>Number(server.port)+span);}));
  const nextNumber=servers.length+1;
  async function create(event){event.preventDefault();const values=Object.fromEntries(new FormData(event.currentTarget));setSaving(true);try{const server=await api(`/api/instances/${profile.id}/servers?sourceServerId=${encodeURIComponent(profile.serverId||profile.id)}`,{method:'POST',body:json({...values,name:values.serverName,port:Number(values.port),isPublic:values.isPublic==='on',crossplay:values.crossplay==='on'})});toast(`Servidor ${server.serverName||server.name} adicionado ao perfil.`);setOpen(false);await refresh(false,{suppressFailureToast:true});navigate(`/games/${profile.gameId}/servers/${profile.id}?server=${server.id}`);}catch(error){toast(error.message,'error');}finally{setSaving(false);}}
  async function remove(server,event){event.preventDefault();event.stopPropagation();if(server.runtimeStatus==='running'||servers.length<=1)return;if(!await confirm({title:`Remover ${server.serverName||server.name}?`,description:'O servidor será removido deste perfil. Os arquivos de mods e configuração compartilhados continuarão no perfil.',confirmLabel:'Remover servidor',danger:true}))return;try{await api(`/api/instances/${profile.id}/servers/${server.id}`,{method:'DELETE'});toast(`${server.serverName||server.name} removido.`);await refresh(false,{suppressFailureToast:true});if(server.id===profile.serverId)navigate(`/games/${profile.gameId}/servers/${profile.id}?server=${servers.find(item=>item.id!==server.id)?.id||profile.id}`);}catch(error){toast(error.message,'error');}}
  return <Card className="multi-server-panel"><div className="multi-server-heading"><div><span className="multi-server-icon"><Server size={17}/></span><div><h2>Servidores deste perfil</h2><p>Um perfil e uma instalação compartilhada para todos os mundos, portas e processos.</p></div></div>{canEditProfile&&<Button variant="primary" size="small" onClick={()=>setOpen(true)}><Plus size={15}/> Adicionar servidor</Button>}</div><div className="shared-profile-note"><Package size={14}/> Mods e configurações BepInEx são compartilhados; cada servidor usa nome público, mundo, porta, saves e console próprios.</div><div className="multi-server-list">{servers.map(server=><Link key={server.id} to={`/games/${profile.gameId}/servers/${profile.id}?server=${server.id}`} className={`multi-server-item ${server.id===profile.serverId?'current':''}`}><span className={`multi-server-status ${server.runtimeStatus==='running'?'running':''}`}/><span className="multi-server-copy"><b>{server.serverName||server.name}{server.id===profile.serverId&&<small>Selecionado</small>}</b><span>Mundo {server.worldName} · Porta {server.port}</span></span><span className="multi-server-state">{server.runtimeStatus==='running'?'Em execução':'Parado'}</span>{canEditProfile&&servers.length>1&&<button type="button" className="multi-server-remove" disabled={server.runtimeStatus==='running'} title={server.runtimeStatus==='running'?'Pare o servidor antes de removê-lo':'Remover servidor'} aria-label={`Remover ${server.serverName||server.name}`} onClick={event=>remove(server,event)}><Trash2 size={14}/></button>}<ArrowRight size={15}/></Link>)}</div>{open&&<Modal title="Adicionar servidor ao perfil" description="O nome público identifica o servidor na lista. Defina também um mundo e uma porta; jogo, mods e configurações BepInEx permanecem compartilhados." close={()=>setOpen(false)}><form className="form-grid" onSubmit={create}><Field label="Nome público do servidor" name="serverName" required maxLength={60} defaultValue={`${profile.serverName} ${nextNumber}`}/><Field label="Nome do mundo" name="worldName" required maxLength={40} defaultValue={`${profile.worldName}_${nextNumber}`}/><Field label="Porta base" name="port" type="number" min="1024" max="65532" step="1" required defaultValue={nextPort}/><Field label="Senha do jogo (vazio usa a atual)" name="gamePassword" type="password" autoComplete="new-password" className="full-field"/><div className="multi-server-checks full-field"><label className="check-row"><input type="checkbox" name="isPublic" defaultChecked={profile.isPublic}/> Servidor público</label><label className="check-row"><input type="checkbox" name="crossplay" defaultChecked={profile.crossplay}/> Crossplay</label></div><div className="notice full-field">O DSS escolhe uma porta depois das faixas em uso e valida conflitos e o nome do mundo antes de salvar.</div><div className="modal-actions full-field"><Button type="button" onClick={()=>setOpen(false)}>Cancelar</Button><Button variant="primary" disabled={saving}>{saving?'Criando…':'Adicionar servidor'}</Button></div></form></Modal>}</Card>;
}

function NumericPagination({ page, totalPages, disabled, onPage }) {
  const total=Math.max(1,Number(totalPages)||1),current=Math.min(Math.max(1,Number(page)||1),total);
  const pages=[...new Set([1,total,current-2,current-1,current,current+1,current+2].filter(value=>value>=1&&value<=total))].sort((a,b)=>a-b);
  return <div className="pagination page-number-pagination"><span>página {current} de {total}</span><div><Button disabled={disabled||current<=1} onClick={()=>onPage(current-1)}><ArrowLeft size={14}/> Anterior</Button><nav aria-label="Paginação">{pages.map((value,index)=><React.Fragment key={value}>{index>0&&value-pages[index-1]>1&&<span className="page-ellipsis">…</span>}<button type="button" className={value===current?'active':''} aria-current={value===current?'page':undefined} disabled={disabled} onClick={()=>onPage(value)}>{value}</button></React.Fragment>)}</nav><Button disabled={disabled||current>=total} onClick={()=>onPage(current+1)}>Próxima <ArrowRight size={14}/></Button></div></div>;
}

function HomePage() {
  const { instances, games } = useDss(); const [view, setView] = useState(localStorage.getItem('dss-home-view') || 'cards');
  const game = games.find(g => g.id === 'valheim') || { id: 'valheim', name: 'Valheim' }; const profiles = instances.filter(p => (p.gameId || 'valheim') === game.id); const active = profiles.filter(p => p.runtimeStatus === 'running').length;
  return <><PageHeading eyebrow="DEDICATED SERVER STUDIO" title="Seus jogos" description="Escolha um jogo para ver e administrar seus perfis de servidor." actions={<div className="segmented"><button className={view === 'cards' ? 'selected' : ''} onClick={() => { setView('cards'); localStorage.setItem('dss-home-view','cards'); }} aria-label="Cards"><Gamepad2 size={17}/></button><button className={view === 'list' ? 'selected' : ''} onClick={() => { setView('list'); localStorage.setItem('dss-home-view','list'); }} aria-label="Lista"><List size={17}/></button></div>}/><div className={`game-grid ${view === 'list' ? 'list-view' : ''}`}><article className="game-card"><img src={GAME_IMAGE} alt="Valheim" className="game-cover"/><div className="game-card-content"><span className="eyebrow">JOGO DISPONÍVEL</span><h2>{game.name}</h2><p>Explore um mundo nórdico inspirado na era dos vikings. Configure mundos, portas e parâmetros em perfis independentes.</p><div className="game-stats"><span><b>{profiles.length}</b> {profiles.length === 1 ? 'perfil' : 'perfis'}</span><span><b>{active}</b> ativos</span></div><Link className="button primary" to={`/games/${game.id}/servers`}>Acessar <ArrowRight size={16}/></Link><a className="art-credit" href="https://store.steampowered.com/app/892970/Valheim/" target="_blank" rel="noreferrer">Capa oficial · Steam <ExternalLink size={12}/></a></div></article><article className="game-card game-card-soon"><img src="/games/minecraft-cover.svg" alt="Minecraft" className="game-cover"/><div className="game-card-content"><span className="eyebrow">EM BREVE</span><h2>Minecraft</h2><p>Suporte a servidores de Minecraft em breve.</p><Button className="w-full" disabled aria-label="Minecraft — em breve"><Clock3 size={16}/> Em breve</Button></div></article></div></>;
}

function MetricBar({ title, percent, value, detail, tone = 'green' }) {
  const amount = Math.max(0, Math.min(100, Number(percent) || 0));
  return <article className="metric-card"><div className="metric-card-head"><div><span>{title}</span><strong>{value}</strong></div><b>{amount.toFixed(1)}%</b></div><div className="metric-track"><i className={tone} style={{ width: `${amount}%` }}/></div><p>{detail}</p></article>;
}
function DashboardPage() {
  const { instances } = useDss(); const [host, setHost] = useState(null); const [proc, setProc] = useState([]); const toast = useToast();
  const refresh = useCallback(async () => { try { const [h, p] = await Promise.all([api('/api/host/metrics'), api('/api/processes/metrics')]); setHost(h); setProc(p.processes || []); } catch (e) { toast(e.message, 'error'); } }, [toast]);
  useEffect(() => { refresh(); const t = setInterval(refresh, 3000); return () => clearInterval(t); }, [refresh]);
  return <><PageHeading eyebrow="RECURSOS DO HOST" title="Dashboard" description="Uso atual dos recursos disponíveis no servidor que executa o DSS." actions={<><span className="state-badge running">{host?.machineName || 'Coletando dados'}</span><Button onClick={refresh}><RefreshCw size={15}/> Atualizar</Button></>}/><section className="hardware-summary"><div><span>Processadores lógicos</span><strong>{host?.logicalProcessors ?? '—'}</strong><small>disponíveis para os servidores</small></div><div><span>Memória do DSS</span><strong>{host ? bytes(host.dssWorkingSetBytes) : '—'}</strong><small>processo do painel e serviços</small></div><div><span>Última leitura</span><strong>{host?.sampledAt ? fmtDate(host.sampledAt) : 'Aguardando'}</strong><small>coletada automaticamente</small></div></section><section className="metric-grid"><MetricBar title="Processador" percent={host?.cpuPercent} value={host ? `${host.cpuPercent.toFixed(1)}%` : '—'} detail={`${host?.logicalProcessors || '—'} processadores lógicos`}/><MetricBar title="Memória" percent={host?.usedMemoryPercent} value={host ? `${bytes(host.usedMemoryBytes)} / ${bytes(host.totalMemoryBytes)}` : '—'} detail={host ? `${host.usedMemoryPercent.toFixed(1)}% em uso · ${bytes(host.totalMemoryBytes - host.usedMemoryBytes)} disponível` : 'Coletando uso da memória'} tone="purple"/></section><Card className="section-gap"><div className="panel-head"><div><h2>Armazenamento</h2><p>Volumes fixos disponíveis no host DSS.</p></div></div><div className="disk-grid">{host?.drives?.length ? host.drives.map(d => <MetricBar key={d.name} title={`Disco ${d.name}`} percent={d.usedPercent} value={`${bytes(d.usedBytes)} / ${bytes(d.totalBytes)}`} detail={`${bytes(d.totalBytes - d.usedBytes)} disponível`} tone="blue"/>) : <Empty title="Discos indisponíveis" description="O host ainda não forneceu dados de armazenamento."/>}</div></Card><Card className="section-gap"><div className="panel-head"><div><h2>Perfis ativos</h2><p>Consumo de cada processo de servidor iniciado pelo DSS.</p></div><span className={`state-badge ${proc.length ? 'running' : 'stopped'}`}>{proc.length} {proc.length === 1 ? 'ATIVO' : 'ATIVOS'}</span></div><div className="active-process-list">{proc.length ? proc.map(p => { const profile = instances.find(x => x.id === (p.profileId||p.id)) || p; return <article key={p.id} className="active-process-card"><ProfileImage profile={profile}/><div className="process-main"><div className="process-title"><b>{profile.name}</b><span className="state-badge running">Ativo</span></div><span>{profile.gameId} · Mundo {profile.worldName}</span><div className="process-stats"><span><b>CPU</b>{Number(p.metrics.cpuPercent || 0).toFixed(1)}%</span><span><b>Memória</b>{bytes(p.metrics.workingSetBytes)}</span><span><b>PID</b>{p.metrics.processId}</span><span><b>Ativo há</b>{humanDuration(p.metrics.uptimeSeconds)}</span></div></div><Link className="button small" to={`/games/${profile.gameId}/servers/${p.profileId||profile.id}?server=${p.id}`}>Abrir perfil →</Link></article>; }) : <Empty title="Nenhum servidor ativo" description="O consumo do processo aparecerá aqui quando iniciar um servidor pelo DSS."/>}</div></Card></>;
}

function ProfileImage({ profile, className = 'profile-image' }) {
  const { profileImageUrls = {} } = useDss() || {};
  if (profile?.hasImage) return <img className={className} src={profileImageUrls[profile.id] || `/api/instances/${profile.id}/image?v=${encodeURIComponent(profile.updatedAt || '')}`} alt={`Imagem de ${profile.name}`} loading="lazy" draggable={false} onDragStart={event => event.preventDefault()}/>;
  return <span className={`${className} profile-fallback`}>DSS</span>;
}

function ProfileHostIndicators({ profile }) {
  const [metrics, setMetrics] = useState(null);
  const [players, setPlayers] = useState(null);
  const previous = useRef(null);
  useEffect(() => {
    let live = true;
    previous.current = null;
    const sample = async () => {
      try {
        const value = await api(`/api/instances/${profile.id}/performance?serverId=${encodeURIComponent(profile.serverId||profile.id)}`);
        if (!live) return;
        const now = performance.now();
        const process = value.process;
        let readBytesPerSecond = 0, writeBytesPerSecond = 0;
        if (process && previous.current?.processId === process.processId) {
          const elapsed = Math.max(.1, (now - previous.current.sampledAt) / 1000);
          readBytesPerSecond = Math.max(0, process.ioReadBytes - previous.current.readBytes) / elapsed;
          writeBytesPerSecond = Math.max(0, process.ioWriteBytes - previous.current.writeBytes) / elapsed;
        }
        previous.current = process ? { processId: process.processId, readBytes: process.ioReadBytes, writeBytes: process.ioWriteBytes, sampledAt: now } : null;
        setMetrics({ ...value, readBytesPerSecond, writeBytesPerSecond });
      } catch {}
    };
    sample();
    const timer = setInterval(sample, 3000);
    return () => { live = false; clearInterval(timer); };
  }, [profile.id, profile.serverId]);

  useEffect(() => {
    let live = true;
    const samplePlayers = async () => {
      try { const value = await api(`/api/instances/${profile.id}/players?serverId=${encodeURIComponent(profile.serverId||profile.id)}`); if (live) setPlayers(value); }
      catch { if (live) setPlayers(null); }
    };
    samplePlayers();
    const timer = setInterval(samplePlayers, 10000);
    return () => { live = false; clearInterval(timer); };
  }, [profile.id, profile.serverId]);

  const process = metrics?.process;
  const speed = value => `${bytes(value || 0)}/s`;
  const portRange = `${metrics?.networkProtocol || 'UDP'} ${metrics?.networkPortStart ?? profile.port}${(metrics?.networkPortEnd ?? profile.port) !== (metrics?.networkPortStart ?? profile.port) ? `–${metrics?.networkPortEnd}` : ''}`;
  return <div className="profile-host-metrics" aria-label="Desempenho do servidor">
    <HostMetric icon={Cpu} label="CPU do servidor" value={process ? `${process.cpuPercent.toFixed(0)}%` : '—'} detail={process ? `Processo ${process.processId}` : 'Servidor parado'} percent={process?.cpuPercent}/>
    <HostMetric icon={Server} label="RAM do servidor" value={process ? bytes(process.workingSetBytes) : '—'} detail={process ? 'Memória do processo' : 'Servidor parado'}/>
    <HostMetric icon={HardDrive} label="Pasta Saves" value={metrics ? bytes(metrics.savesBytes) : '—'} detail="Espaço ocupado pelos arquivos de saves" title="Tamanho total dos arquivos dentro da pasta Saves deste servidor."/>
    <HostMetric icon={Activity} label="I/O do servidor" value={process ? `↓ ${speed(metrics.readBytesPerSecond)}` : '—'} detail={process ? `↑ ${speed(metrics.writeBytesPerSecond)} · ${portRange}` : 'Servidor parado'} title={`Taxa de I/O do processo do servidor; pode incluir arquivos além do tráfego de rede. Portas configuradas: ${portRange}.`}/>
    <HostMetric icon={Users} label="Jogadores ativos" value={players?.activeCount == null ? '—' : String(players.activeCount)} detail={players?.countSource || 'Consultando sessão'} title={`Jogadores conectados ao servidor. ${players?.countSource || ''}`}/>
  </div>;
}

function HostMetric({ icon: Icon, label, value, detail, percent, title }) {
  const boundedPercent = Number.isFinite(percent) ? Math.max(0, Math.min(100, percent)) : null;
  return <div className="profile-host-metric" title={title || `${label}: ${value} · ${detail}`}>
    <div className="profile-host-metric-head"><Icon size={14}/><span>{label}</span></div>
    <strong>{value}</strong>
    <small>{detail}</small>
    {boundedPercent !== null && <span className="profile-host-metric-track"><i style={{ width: `${boundedPercent}%` }}/></span>}
  </div>;
}

function ServerAddressChooser({ profileId, serverId, port, toast }) {
  const [open, setOpen] = useState(false);
  const [refreshKey, setRefreshKey] = useState(0);
  const [local, setLocal] = useState({ loading: false, addresses: [], error: '' });
  const [publicAddress, setPublicAddress] = useState({ loading: false, address: '', error: '' });
  const [connectivity, setConnectivity] = useState({ loading: false, local: {}, public: null });
  const [copied, setCopied] = useState('');
  const containerRef = useRef(null);
  const copiedTimer = useRef(0);
  const popoverId = useId();

  useEffect(() => {
    if (!open) return;
    let active = true;
    setLocal({ loading: true, addresses: [], error: '' });
    setPublicAddress({ loading: true, address: '', error: '' });
    setConnectivity({ loading: true, local: {}, public: null });
    api(`/api/instances/${encodeURIComponent(profileId)}/network/connectivity?serverId=${encodeURIComponent(serverId || profileId)}`)
      .then(result => {
        if (!active) return;
        const localResults = result.localAddresses || [];
        setLocal({ loading: false, addresses: localResults.map(item => item.address), error: '' });
        setPublicAddress({ loading: false, address: result.publicAddress?.address || '', error: '' });
        setConnectivity({
          loading: false,
          local: Object.fromEntries(localResults.map(item => [item.address, item])),
          public: result.publicAddress || null
        });
      })
      .catch(error => {
        if (!active) return;
        const message = error.message || 'Não foi possível verificar os endereços.';
        setLocal({ loading: false, addresses: [], error: message });
        setPublicAddress({ loading: false, address: '', error: message });
        setConnectivity({ loading: false, local: {}, public: null });
      });
    return () => { active = false; };
  }, [open, refreshKey, profileId, serverId]);

  useEffect(() => {
    if (!open) return;
    const onPointerDown = event => { if (!containerRef.current?.contains(event.target)) setOpen(false); };
    const onKeyDown = event => { if (event.key === 'Escape') setOpen(false); };
    document.addEventListener('pointerdown', onPointerDown);
    window.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  useEffect(() => () => window.clearTimeout(copiedTimer.current), []);

  async function copyAddress(address) {
    const value = address.includes(':') ? `[${address}]:${port}` : `${address}:${port}`;
    try {
      await copyTextToClipboard(value);
      setCopied(value);
      toast(`${value} copiado.`);
      window.clearTimeout(copiedTimer.current);
      copiedTimer.current = window.setTimeout(() => setCopied(''), 1600);
    } catch (error) {
      toast(error.message || 'Não foi possível copiar o endereço.', 'error');
    }
  }

  function AddressOption({ address, type, result }) {
    const value = address.includes(':') ? `[${address}]:${port}` : `${address}:${port}`;
    const isCopied = copied === value;
    const status = connectivity.loading ? 'checking' : result?.status || 'unknown';
    const statusLabel = status === 'online' ? 'Consulta UDP respondida' : status === 'observed' ? 'Conexões recentes detectadas' : status === 'offline' ? 'Servidor offline' : status === 'restricted' || status === 'unverified' ? 'Teste inconclusivo' : status === 'relay' ? 'Conexão por relay' : status === 'unsupported' ? 'Teste indisponível' : status === 'checking' ? 'Verificando conexão' : 'Não verificado';
    return <button type="button" className="address-choice" onClick={() => copyAddress(address)}>
      <span><code>{value}</code><span className="address-choice-meta"><small>{type}</small><small className={`address-health ${status}`} title={result?.message || statusLabel} role="status">{status === 'online' || status === 'observed' ? <Check size={12}/> : status === 'offline' ? <X size={12}/> : status === 'relay' ? <Globe2 size={12}/> : status === 'checking' ? <LoaderCircle size={12} className="spin"/> : <CircleAlert size={12}/>} {statusLabel}</small></span></span>
      {isCopied ? <Check size={16}/> : <Copy size={15}/>}
    </button>;
  }

  return <div className="address-picker profile-view-controls" ref={containerRef}>
    <Button type="button" className="w-full address-copy address-picker-trigger" aria-expanded={open} aria-controls={popoverId} onClick={() => setOpen(value => !value)}>
      <span className="address-picker-trigger-copy"><b>Endereço do servidor</b><small>Copiar IP local ou público</small></span>
      <Copy size={15}/><ChevronDown size={14} className={open ? 'address-picker-chevron open' : 'address-picker-chevron'}/>
    </Button>
    {open && <section className="address-popover" id={popoverId} aria-label="Escolher endereço para copiar">
      <header><div><b>Copiar endereço</b><small>Porta do jogo · UDP {port} · consulta nas portas {port} e {port + 1}</small></div><button type="button" className="icon-btn" aria-label="Atualizar endereços e status" title="Atualizar endereços e status" onClick={() => setRefreshKey(value => value + 1)}><RefreshCw size={14}/></button></header>
      <div className="address-choice-group">
        <div className="address-choice-heading"><Network size={15}/><span><b>Rede local</b><small>Para quem está na mesma rede</small></span></div>
      {local.loading ? <p className="address-state"><LoaderCircle size={14} className="spin"/> Consultando endereços…</p>
          : local.addresses.length ? local.addresses.map(address => <AddressOption key={address} address={address} type="IP desta máquina" result={connectivity.local[address]}/>)
          : <p className="address-state">{local.error || 'Nenhum endereço local disponível.'}</p>}
      </div>
      <div className="address-choice-group public">
        <div className="address-choice-heading"><Globe2 size={15}/><span><b>Internet</b><small>Para quem está fora da rede</small></span></div>
        {publicAddress.loading ? <p className="address-state"><LoaderCircle size={14} className="spin"/> Consultando IP público…</p>
          : publicAddress.address ? <AddressOption address={publicAddress.address} type="IP público" result={connectivity.public}/>
          : <p className="address-state">{publicAddress.error || 'IP público indisponível no momento.'}</p>}
      </div>
      <p className="address-popover-hint">A consulta Steam verifica UDP nas portas do jogo e de consulta. Se ela não responder, o resultado é inconclusivo e não significa que a porta de jogo esteja bloqueada. Conexões recentes registradas pelo servidor também aparecem como confirmação.</p>
    </section>}
  </div>;
}

function ProfilesPage() {
  const { gameId = 'valheim' } = useParams(); const navigate = useNavigate(); const { games, instances, refresh } = useDss(); const toast = useToast(); const [modal, setModal] = useState(''); const [busy, setBusy] = useState(false); const [importText, setImportText] = useState('');
  const game = games.find(g => g.id === gameId) || { id: gameId, name: gameId }; const profiles = instances.filter(p => p.gameId === gameId);
  async function create(event) { event.preventDefault(); const f = new FormData(event.currentTarget); setBusy(true); try { const created = await api('/api/instances', { method: 'POST', body: json(profilePayload(Object.fromEntries(f), gameId)) }); await refresh(); setModal(''); location.assign(`/games/${gameId}/servers/${created.id}`); } catch(e) { toast(e.message, 'error'); } finally { setBusy(false); } }
  async function importProfile() {
    let created = null;
    setBusy(true);
    try {
      const doc = JSON.parse(importText);
      if (!doc || typeof doc !== 'object' || Array.isArray(doc)) throw new Error('O JSON precisa conter um objeto de perfil exportado pelo DSS.');
      if ((Object.hasOwn(doc, 'mods') && !Array.isArray(doc.mods)) || (Object.hasOwn(doc, 'Mods') && !Array.isArray(doc.Mods)))
        throw new Error('A lista de mods do JSON está em um formato inválido. Copie o JSON novamente no perfil de origem.');
      const rawMods = Array.isArray(doc.mods) ? doc.mods : Array.isArray(doc.Mods) ? doc.Mods : [];
      const mods = rawMods.map(mod => {
        let provider = String(mod.provider ?? mod.Provider ?? '').trim().toLowerCase();
        if (provider === 'hex') provider = 'hexium';
        return {
          provider,
          packageId: String(mod.packageId ?? mod.PackageId ?? '').trim(),
          version: String(mod.version ?? mod.Version ?? '').trim(),
          enabled: (mod.enabled ?? mod.Enabled) !== false
        };
      });
      const supportedProviders = new Set(['thunderstore', 'hexium', 'bepinex']);
      const invalid = mods.find(mod => !supportedProviders.has(mod.provider) || !mod.packageId || mod.packageId.length > 160 || !mod.version);
      if (invalid) throw new Error(`O JSON contém um mod sem catálogo compatível ou com dados incompletos: ${invalid.packageId || invalid.provider || 'mod desconhecido'}.`);
      const uniqueMods = [...new Map(mods.map(mod => [`${mod.provider}:${mod.packageId}`.toLowerCase(), mod])).values()];
      const modsByProvider = uniqueMods.reduce((groups, mod) => {
        (groups[mod.provider] ||= []).push(mod);
        return groups;
      }, {});
      created = await api('/api/instances/import', { method: 'POST', body: json(doc) });
      for (const [provider, providerMods] of Object.entries(modsByProvider)) {
        for (let offset = 0; offset < providerMods.length; offset += 200) {
          const batch = providerMods.slice(offset, offset + 200);
          await installPackagesWithProgress({
            profileId: created.id,
            provider,
            packageIds: batch.map(mod => mod.packageId),
            dependencyVersions: Object.fromEntries(batch.map(mod => [mod.packageId, mod.version])),
            toast,
            key: `profile-json-import:${created.id}`
          });
        }
      }
      if (uniqueMods.some(mod => !mod.enabled)) {
        const inventory = await api(`/api/instances/${created.id}/mods`);
        const installedByKey = new Map((inventory.packages || []).map(mod => [`${String(mod.provider || '').toLowerCase()}:${String(mod.packageId || '').toLowerCase()}`, mod]));
        for (const desired of uniqueMods.filter(mod => !mod.enabled)) {
          const installed = installedByKey.get(`${desired.provider}:${desired.packageId.toLowerCase()}`);
          if (installed?.enabled) await api(`/api/instances/${created.id}/mods/${installed.id}/enabled`, { method: 'POST', body: json({ enabled: false, cascadeDependents: true }) });
        }
      }
      await refresh();
      setModal('');
      const omitted = Number(doc.omittedModCount ?? doc.OmittedModCount) || 0;
      toast(`Perfil importado com ${uniqueMods.length} mod(s) e suas versões.${omitted ? ` ${omitted} mod(s) local(is) ou sem catálogo compatível não foram incluídos no JSON.` : ''}`, 'success', { durationMs: 10000 });
      navigate(`/games/${created.gameId || gameId}/servers/${created.id}`);
    } catch(e) {
      if (created) {
        await refresh();
        setModal('');
        toast(`O perfil foi criado, mas a importação dos mods foi interrompida: ${e.message || 'falha desconhecida'}`, 'error', { durationMs: 12000 });
        navigate(`/games/${created.gameId || gameId}/servers/${created.id}`);
      } else toast(e.message || 'JSON inválido.', 'error');
    } finally { setBusy(false); }
  }
  return <><PageHeading eyebrow={`JOGO · ${game.name.toUpperCase()}`} title={`Perfis de ${game.name}`} description="Um perfil reúne os parâmetros, saves e portas de um servidor. Perfis podem compartilhar os mesmos arquivos do jogo." back={{ href:'/', label:'← Todos os jogos' }} actions={<><Button onClick={() => setModal('import')}><Upload size={15}/> Importar JSON</Button><Button variant="primary" onClick={() => setModal('create')}><Plus size={16}/> Novo perfil</Button></>}/><Card><div className="panel-head"><div><h2>{profiles.length} {profiles.length === 1 ? 'perfil' : 'perfis'}</h2><p>Abra um perfil para iniciar, atualizar e gerenciar o servidor.</p></div><button className="text-button" onClick={() => refresh()}>⟳ Atualizar</button></div>{profiles.length ? <div className="profile-list">{profiles.map(profile => <Link key={profile.id} to={`/games/${gameId}/servers/${profile.id}`} className="profile-list-card"><ProfileImage profile={profile} className="profile-emblem"/><div className="profile-list-main"><strong>{profile.name}<span className={`state-badge ${profile.runtimeStatus === 'running' ? 'running' : 'stopped'}`}>{profile.runtimeStatus === 'running' ? 'Em execução' : 'Parado'}</span></strong><span>Mundo {profile.worldName} · {profile.serverName} · Porta {profile.port}</span><small>{profile.installPath || 'Pasta do servidor não definida'} · {profile.serverInstalled ? 'Arquivos instalados' : 'Aguardando instalação'}</small></div><span className="profile-open-label">Abrir perfil <ArrowRight size={16}/></span></Link>)}</div> : <Empty title={`Nenhum perfil de ${game.name} ainda`} description="Crie seu primeiro perfil. Perfis podem compartilhar a instalação do jogo e manter mundos, portas e parâmetros próprios." action={<Button variant="primary" onClick={() => setModal('create')}><Plus size={16}/> Criar perfil</Button>}/>}</Card>
    {modal === 'create' && <Modal title="Criar perfil" description="Defina as opções iniciais do servidor. Você poderá alterar tudo no perfil." close={() => setModal('')}><form className="form-grid" autoComplete="off" onSubmit={create}><PasswordAutofillDecoys/><Field label="Nome do perfil" name="name" required placeholder="Meu servidor Valheim"/><Field label="Nome público do servidor" name="serverName" required defaultValue="Valheim Server"/><Field label="Mundo" name="worldName" required defaultValue="Dedicated"/><Field label="Porta base" name="port" type="number" min="1024" max="65532" defaultValue="2456"/><Field label="Pasta do servidor dedicado" name="installPath" required placeholder="E:\\Servers\\Valheim" className="full-field"/><input type="hidden" name="useInstallPathSaves" value="on"/><Field label="Senha do servidor (opcional)" name="serverPasswordDraft" type="password" autoComplete="new-password" placeholder="Opcional; defina antes de iniciar" data-lpignore="true" data-1p-ignore="true" data-bwignore="true" data-form-type="other"/><div className="modal-actions full-field"><Button type="button" onClick={() => setModal('')}>Cancelar</Button><Button type="submit" variant="primary" disabled={busy}>{busy ? 'Criando…' : 'Criar perfil'}</Button></div></form></Modal>}
    {modal === 'import' && <Modal title="Importar perfil JSON" description="Cole o JSON exportado por um perfil DSS. Se ele incluir mods, o DSS instalará as mesmas versões e estados." close={() => !busy && setModal('')}><textarea className="field code-area import-json" value={importText} onChange={e => setImportText(e.target.value)} placeholder="Cole o JSON do perfil…" disabled={busy}/><div className="modal-actions"><Button onClick={() => setModal('')} disabled={busy}>Cancelar</Button><Button variant="primary" onClick={importProfile} disabled={busy||!importText.trim()}>{busy ? <><LoaderCircle size={15} className="spin"/> Importando…</> : 'Importar perfil'}</Button></div></Modal>}
  </>;
}
function profilePayload(v, gameId, formData) { const args = Array.isArray(v.extraArguments) ? v.extraArguments : String(v.extraArguments || '').match(/"[^\"]*"|\S+/g) || []; let settings = v.gameSettings || {}; if (typeof settings === 'string') { try { settings = JSON.parse(settings || '{}'); } catch { settings = {}; } } if (gameId === 'valheim' && formData) { const numberOr=(key,fallback)=>{const value=Number(formData.get(key));return Number.isFinite(value)?value:fallback;};const modifiers=Object.fromEntries(['combat','deathPenalty','resources','raids','portals'].map(key=>[key,String(formData.get(`modifier_${key}`)||'')]).filter(([,value])=>value));settings={...settings,saveInterval:numberOr('saveInterval',Number(settings.saveInterval??1800)),backups:numberOr('backups',Number(settings.backups??4)),backupShort:numberOr('backupShort',Number(settings.backupShort??7200)),backupLong:numberOr('backupLong',Number(settings.backupLong??43200)),logFile:String(formData.get('logFile')||'').trim(),instanceId:String(formData.get('instanceId')||'').trim(),preset:String(formData.get('preset')||''),modifiers,setKeys:formData.getAll('setKeys').map(String),serverBranch:String(formData.get('serverBranch')||settings.serverBranch||'public').trim()};} return { gameId, name: v.name, installPath: v.installPath, serverName: v.serverName, worldName: v.worldName, gamePassword: v.serverPasswordDraft ?? v.gamePassword ?? '', port: Number(v.port), isPublic: v.isPublic === 'on' || v.isPublic === true, crossplay: v.crossplay === 'on' || v.crossplay === true, useInstallPathSaves: v.useInstallPathSaves === 'on' || v.useInstallPathSaves === true, saveDirectory: v.saveDirectory || '', extraArguments: args.map(arg => arg.replace(/^"|"$/g,'')), gameSettings: settings }; }
function Field({ label, className = '', ...props }) { return <label className={`field-label ${className}`}>{label}<input className="field" {...props}/></label>; }
function ValheimLaunchOptions({ settings = {}, disabled }) {
  const modifiers=settings.modifiers||{};
  const choices=[['combat','Combate',['veryeasy','easy','hard','veryhard']],['deathPenalty','Penalidade de morte',['casual','veryeasy','easy','hard','hardcore']],['resources','Recursos',['muchless','less','more','muchmore','most']],['raids','Invasões',['none','muchless','less','more','muchmore']],['portals','Portais',['casual','hard','veryhard']]];
  const worldKeys=[['nobuildcost','Sem custo de construção'],['playerevents','Eventos por jogador'],['passivemobs','Criaturas passivas'],['nomap','Sem mapa']];
  const number=(key,label,fallback,min,max,help)=><label className="field-label">{label}<input className="field" name={key} type="number" min={min} max={max} disabled={disabled} defaultValue={settings[key]??fallback}/><small>{help}</small></label>;
  return <section className="launch-options-section full-field"><div className="panel-head"><div><h3>Opções de inicialização do Valheim</h3><p>Controles oficiais do servidor; as opções geram os argumentos ao iniciar.</p></div></div><div className="launch-option-grid">{number('saveInterval','Intervalo entre saves (segundos)',1800,60,86400,'-saveinterval · padrão: 1800 s.')}{number('backups','Quantidade de backups',4,0,100,'-backups · 0 desativa os backups automáticos.')}{number('backupShort','Intervalo do primeiro backup (segundos)',7200,60,1209600,'-backupshort · padrão: 7200 s.')}{number('backupLong','Intervalo dos próximos backups (segundos)',43200,60,1209600,'-backuplong · padrão: 43200 s.')}<Field label="Arquivo de log (opcional)" name="logFile" disabled={disabled} defaultValue={settings.logFile||''} placeholder="Caminho completo do arquivo"/><Field label="ID do Crossplay (opcional)" name="instanceId" disabled={disabled} defaultValue={settings.instanceId||''} placeholder="Único quando compartilhar IP e porta"/></div><div className="launch-group"><h4>Modificadores do mundo</h4><div className="launch-modifier-grid"><label className="field-label"><span>Predefinição</span><select className="field" name="preset" disabled={disabled} defaultValue={settings.preset||''}><option value="">Sem predefinição</option>{['normal','casual','easy','hard','hardcore','immersive','hammer'].map(value=><option key={value} value={value}>{value}</option>)}</select><small>-preset · define um conjunto inicial de modificadores.</small></label>{choices.map(([key,label,options])=><label className="field-label" key={key}><span>{label}</span><select className="field" name={`modifier_${key}`} disabled={disabled} defaultValue={modifiers[key]||''}><option value="">Padrão do mundo</option>{options.map(value=><option key={value} value={value}>{value}</option>)}</select></label>)}</div><fieldset className="launch-key-fieldset"><legend>Opções de mundo</legend>{worldKeys.map(([value,label])=><label className="profile-check" key={value}><input type="checkbox" name="setKeys" value={value} disabled={disabled} defaultChecked={(settings.setKeys||[]).includes(value)}/><span><b>{label}</b><small>-setkey {value}</small></span></label>)}</fieldset></div><p className="launch-arguments-note">O DSS gera os argumentos automaticamente a partir dos campos do perfil e desta seção.</p></section>;
}
function PasswordAutofillDecoys() { return <div className="autofill-decoys" aria-hidden="true"><input type="text" name="username" autoComplete="username" tabIndex={-1}/><input type="password" name="password" autoComplete="current-password" tabIndex={-1}/></div>; }
function Modal({ title, description, close, children, className = '', backdropClassName = '', style }) { return <div className={`modal-backdrop ${backdropClassName}`}><section className={`modal-card ${className}`} style={style} role="dialog" aria-modal="true"><header className="modal-head"><div><h2>{title}</h2>{description && <p>{description}</p>}</div><button type="button" className="icon-btn" onClick={event=>{event.preventDefault();event.stopPropagation();close?.();}} aria-label="Fechar"><X size={18}/></button></header><div className="modal-content">{children}</div></section></div>; }

class InstalledModsErrorBoundary extends React.Component {
  state = { error: null, retry: 0 };
  static getDerivedStateFromError(error) { return { error }; }
  componentDidCatch(error, info) { console.error('Erro ao renderizar os mods do perfil:', error, info); }
  componentDidUpdate(previous) {
    if (previous.profileId !== this.props.profileId && this.state.error) this.setState({ error: null, retry: this.state.retry + 1 });
  }
  render() {
    if (this.state.error) return <Card><div className="inline-error"><CircleAlert size={17}/><span>Não foi possível atualizar a lista de mods: {String(this.state.error.message || this.state.error)}</span></div><div className="modal-actions"><Button variant="primary" onClick={() => this.setState(state => ({ error: null, retry: state.retry + 1 }))}>Recarregar lista</Button></div></Card>;
    return React.cloneElement(this.props.children, { key: this.state.retry });
  }
}

function ProfileRoute() {
  const { gameId, profileId } = useParams();
  const { instances } = useDss();
  if (!instances.some(profile => profile.id === profileId))
    return <Card><Empty title="Perfil indisponível" description="O DSS não encontrou este perfil. Atualize a lista ou volte aos perfis do jogo." action={<Link className="button secondary" to={`/games/${encodeURIComponent(gameId || 'valheim')}/servers`}>Voltar aos perfis</Link>}/></Card>;
  return <ProfilePage/>;
}

function ProfileSettingsReadOnly({ profile }) {
  const values = [
    ['Nome do perfil', profile.name],
    ['Nome público', profile.serverName],
    ['Mundo', profile.worldName],
    ['Porta base', profile.port],
    ['Pasta do servidor dedicado', profile.installPath],
    ['Pasta de saves', profile.useInstallPathSaves ? 'Salvar junto aos arquivos do jogo' : profile.saveDirectory || 'Pasta padrão do servidor'],
    ['Senha do servidor', profile.hasPassword ? 'Configurada' : 'Não configurada'],
    ['Servidor público', profile.isPublic ? 'Sim' : 'Não'],
    ['Crossplay', profile.crossplay ? 'Ativado' : 'Desativado'],
    ['Argumentos adicionais', (profile.extraArguments || []).join(' ') || 'Nenhum'],
    ['Configurações do jogo', Object.keys(profile.gameSettings || {}).length ? JSON.stringify(profile.gameSettings, null, 2) : 'Nenhuma']
  ];
  return <Card className="profile-read-only-settings"><div className="panel-head"><div><h2>Configurações do perfil</h2><p>Acesso de Operador: configurações, mods e usuários são somente para consulta; os controles, o console e a atualização do servidor continuam disponíveis.</p></div></div><dl className="profile-read-only-grid">{values.map(([label,value])=><div key={label}><dt>{label}</dt><dd>{label === 'Configurações do jogo' && value !== 'Nenhuma' ? <pre>{value}</pre> : String(value ?? '—')}</dd></div>)}</dl></Card>;
}

function ProfilePage() {
  const { gameId, profileId } = useParams(); const location = useLocation(); const { instances, refresh, auth } = useDss(); const toast = useToast(); const navigate = useNavigate();
  const rootProfile = instances.find(p => p.id === profileId); const requestedServerId = new URLSearchParams(location.search).get('server'); const activeServer = rootProfile?.servers?.find(server => server.id === requestedServerId) || rootProfile?.servers?.find(server => server.id === rootProfile.id) || rootProfile?.servers?.[0]; const profile = rootProfile && activeServer ? { ...rootProfile, ...activeServer, id:rootProfile.id, profileId:rootProfile.id, serverId:activeServer.id, name:rootProfile.name, gameSettings:{...rootProfile.gameSettings,...activeServer.gameSettings,...(rootProfile.gameSettings?.serverBranch ? {serverBranch:rootProfile.gameSettings.serverBranch} : {})}, servers:rootProfile.servers } : rootProfile; const groupServers = profile?.servers || []; const sharedProfileCount = groupServers.length; const groupRunning = groupServers.some(item => item.runtimeStatus === 'running'); const savedServerBranch = typeof profile?.gameSettings?.serverBranch === 'string' ? profile.gameSettings.serverBranch : 'public'; const serverQuery = profile?.serverId ? `?server=${encodeURIComponent(profile.serverId)}` : ''; const sub = location.pathname.split('/').filter(Boolean).at(-1); const tab = sub === 'mods' ? 'mods' : sub === 'buscar' ? 'catalog' : sub === 'bepinex' ? 'bepinex' : sub === 'historico' ? 'history' : 'settings';
  const profilePath = `/games/${gameId}/servers/${profileId}`; const profileConfigDirty = useProfileConfigDirty(profileId); const historyIndex = useRef(window.history.state?.idx ?? 0);
  const [profileAccess,setProfileAccess]=useState(null);
  useEffect(()=>{let active=true;setProfileAccess(null);if(!profileId)return()=>{active=false;};api(`/api/instances/${profileId}/my-access`,{suppressFailureToast:true}).then(result=>{if(active)setProfileAccess({profileId,permission:result.permission});}).catch(()=>{if(active)setProfileAccess({profileId,permission:null});});return()=>{active=false;};},[profileId]);
  const canEditProfile=auth.user?.role==='admin'||(profileAccess?.profileId===profileId&&profileAccess.permission==='edit');
  const canOperateProfile=auth.user?.role==='admin'||(profileAccess?.profileId===profileId&&profileAccess.permission==='operate');
  const canManageServer=canEditProfile||canOperateProfile;
  useEffect(() => {
    if (rootProfile && activeServer && requestedServerId && requestedServerId !== activeServer.id)
      navigate(`${location.pathname}?server=${encodeURIComponent(activeServer.id)}`, { replace: true });
  }, [rootProfile?.id, requestedServerId, activeServer?.id, location.pathname, navigate]);
  const normalizedInstallPath = profile?.installPath?.replace(/[\\/]+$/, '').toLocaleLowerCase(); const serverFilesShared = Boolean(normalizedInstallPath && instances.some(item => item.id !== profileId && item.installPath?.replace(/[\\/]+$/, '').toLocaleLowerCase() === normalizedInstallPath));
  const [startingServer, setStartingServer] = useState(false); const [removingProfile, setRemovingProfile] = useState(false); const [profileRemovalOpen,setProfileRemovalOpen]=useState(false); const [deleteServerFiles,setDeleteServerFiles]=useState(false); const [deleteProfileSaves,setDeleteProfileSaves]=useState(false); const [steamModalOpen,setSteamModalOpen]=useState(false);
  const [jsonCopyState,setJsonCopyState]=useState('idle'); const [profileExportData,setProfileExportData]=useState(null); const jsonCopyTimer=useRef(0);
  useEffect(()=>()=>window.clearTimeout(jsonCopyTimer.current),[]);
  const [firewall, setFirewall] = useState({ authorized:false, verified:false, checking:true }); const [operation, setOperation] = useState(profile?.gameOperation || null); const [installedModUpdates,setInstalledModUpdates]=useState({}); const [settings, setSettings] = useState(null); const [consoleOpen, setConsoleOpen] = useState(false); const [playerManagementOpen, setPlayerManagementOpen] = useState(false); const [profileDetailsOpen, setProfileDetailsOpen] = useState(false); const [checkingServerVersion, setCheckingServerVersion] = useState(false); const [serverBranches, setServerBranches] = useState([]); const [branchStatus, setBranchStatus] = useState('idle'); const [selectedServerBranch,setSelectedServerBranch]=useState(savedServerBranch);
  useEffect(()=>{
    if(tab!=='settings'||!profileId){setProfileExportData(null);setJsonCopyState('idle');return;}
    let active=true;
    setProfileExportData(null);
    setJsonCopyState('loading');
    api('/api/instances/'+encodeURIComponent(profileId)+'/export',{suppressFailureToast:true})
      .then(data=>{if(active){setProfileExportData(data);setJsonCopyState('idle');}})
      .catch(()=>{if(active)setJsonCopyState('idle');});
    return()=>{active=false;};
  },[tab,profileId,settings]);
  useEffect(() => { setSelectedServerBranch(savedServerBranch); }, [profileId, savedServerBranch]);
  useEffect(() => { const index = window.history.state?.idx; if (typeof index === 'number') historyIndex.current = index; }, [location.pathname]);
  useEffect(() => {
    const isProfilePath = path => path === profilePath || path.startsWith(`${profilePath}/`);
    const message = 'Há alterações não salvas nos arquivos do BepInEx. Sair do perfil descartará essas alterações.';
    function onClick(event) {
      if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
      const anchor = event.target instanceof Element ? event.target.closest('a[href]') : null;
      if (!anchor || anchor.target === '_blank' || anchor.hasAttribute('download')) return;
      let target;
      try { target = new URL(anchor.href, window.location.href); } catch { return; }
      if (target.origin === window.location.origin && isProfilePath(target.pathname)) return;
      if (!hasUnsavedProfileConfig(profileId)) return;
      if (!window.confirm(message)) { event.preventDefault(); event.stopPropagation(); event.stopImmediatePropagation?.(); return; }
      clearProfileConfigDrafts(profileId);
    }
    function onPopState(event) {
      const targetPath = window.location.pathname;
      const targetIndex = window.history.state?.idx;
      if (isProfilePath(targetPath) || !hasUnsavedProfileConfig(profileId)) { if (typeof targetIndex === 'number') historyIndex.current = targetIndex; return; }
      if (!window.confirm(message)) {
        event.stopImmediatePropagation?.();
        if (typeof targetIndex === 'number' && typeof historyIndex.current === 'number') window.history.go(historyIndex.current - targetIndex);
        return;
      }
      clearProfileConfigDrafts(profileId);
      if (typeof targetIndex === 'number') historyIndex.current = targetIndex;
    }
    document.addEventListener('click', onClick, true);
    window.addEventListener('popstate', onPopState, true);
    return () => { document.removeEventListener('click', onClick, true); window.removeEventListener('popstate', onPopState, true); };
  }, [gameId, profileId, profilePath]);
  async function checkServerVersion(notify = false) {
    setCheckingServerVersion(true);
    const modCheck = notify
      ? api(`/api/instances/${profileId}/mods/check-updates`, { method:'POST', body:'{}', suppressFailureToast:true }).then(result => ({ result })).catch(error => ({ error }))
      : Promise.resolve(null);
    try {
      const latest = await api(`/api/instances/${profileId}/game/check-updates`, { method:'POST', body:'{}', suppressFailureToast:!notify });
      const checkedMods = await modCheck;
      let modSummary = '';
      if (checkedMods?.result) {
        const updates = checkedMods.result.updates || [];
        setInstalledModUpdates(Object.fromEntries(updates.filter(item => item.modId).map(item => [item.modId, item])));
        const available = updates.filter(item => item.hasUpdate).length;
        const identified = Number(checkedMods.result.checkedCount) || 0;
        const unknown = Number(checkedMods.result.unidentifiedCount) || 0;
        modSummary = available
          ? `${available} mod(s) têm atualização disponível entre ${identified} identificado(s).`
          : `${identified} mod(s) identificado(s) conferido(s)${unknown ? `; ${unknown} sem identificador foram ignorados` : ''}.`;
      } else if (checkedMods?.error) {
        modSummary = `Mods não verificados: ${checkedMods.error.message}`;
      }
      await refresh(false, { suppressFailureToast:true });
      if (notify) {
        const serverSummary = !latest.serverInstalled ? 'O servidor ainda não está instalado neste perfil.'
          : latest.updateCheckError ? `Não foi possível consultar a versão pública: ${latest.updateCheckError}`
          : latest.updateAvailable ? `A branch selecionada usa o build ${latest.latestBuildId}; instalado: ${latest.installedBuildId}.`
          : latest.installedBuildId ? `Build ${latest.installedBuildId} conferido; a instalação corresponde à versão consultada.`
          : 'Consulta concluída, mas o build instalado não foi identificado.';
        toast([serverSummary, modSummary].filter(Boolean).join(' · '), latest.updateCheckError || checkedMods?.error ? 'error' : 'success', { durationMs: 10000 });
      }
    } catch (error) {
      if (notify) toast(error.message, 'error');
    } finally { setCheckingServerVersion(false); }
  }
  useEffect(() => { if (profileId && canManageServer) checkServerVersion(); }, [profileId, canManageServer]);
  useEffect(() => {
    if (tab !== 'settings' || gameId !== 'valheim') return;
    let live = true;
    setBranchStatus('loading');
    api(`/api/games/${encodeURIComponent(gameId)}/branches`).then(result => {
      if (!live) return;
      setServerBranches(result.branches || []);
      setBranchStatus('ready');
    }).catch(error => {
      if (!live) return;
      setBranchStatus('error');
      toast(`Não foi possível carregar as versões do servidor: ${error.message}`, 'error');
    });
    return () => { live = false; };
  }, [gameId, tab]);
  useEffect(() => {
    if (!profile) return;
    let live=true, pollTimer=0, stopTimer=0;
    const checkStatus=()=>api(`/api/instances/${profileId}/firewall/status?serverId=${encodeURIComponent(profile.serverId||profile.id)}`)
      .then(status=>{if(live){setFirewall({...status,checking:false});if(status.authorized&&pollTimer)window.clearInterval(pollTimer);}})
      .catch(()=>{if(live)setFirewall({authorized:false,verified:false,checking:false});});
    const pollAfterPreparation=()=>{
      if(pollTimer)window.clearInterval(pollTimer);
      void checkStatus();
      pollTimer=window.setInterval(checkStatus,2000);
      stopTimer=window.setTimeout(()=>{if(pollTimer)window.clearInterval(pollTimer);},90000);
    };
    setFirewall({ authorized:false, verified:false, checking:true });
    void checkStatus();
    window.addEventListener('dss-firewall-status-refresh',pollAfterPreparation);
    return ()=>{live=false;window.removeEventListener('dss-firewall-status-refresh',pollAfterPreparation);if(pollTimer)window.clearInterval(pollTimer);if(stopTimer)window.clearTimeout(stopTimer);};
  }, [profileId,profile?.id,profile?.serverId]);
  useEffect(() => { if (!profile) return; setOperation(profile.gameOperation || null); setInstalledModUpdates({}); api(`/api/instances/${profileId}/mods/check-updates`, { method: 'POST', body: '{}' }).then(result=>setInstalledModUpdates(Object.fromEntries((result.updates||[]).map(item=>[item.modId,item])))).catch(() => {}); }, [profileId]);
  useEffect(() => {
    if (!operation?.running) return;
    let live = true;
    const cursor = { operationId: operation.operationId || '', ignoreOperationId: operation.ignoreOperationId || '', sequence: Number(operation.outputSequence || 0), base: Number(operation.outputBaseSequence || 0), output: operation.output || [] };
    const wait = milliseconds => new Promise(resolve => window.setTimeout(resolve, milliseconds));
    async function poll() {
      while (live) {
        const identity = cursor.operationId ? `operationId=${encodeURIComponent(cursor.operationId)}&` : '';
        try {
          const response = await api(`/api/instances/${profileId}/game/update-status?${identity}after=${cursor.sequence}`, { method:'GET', cache:'no-store', suppressFailureToast:true });
          const next = response?.operation;
          if (!live) break;
          // Before the new request reaches the host, the endpoint may still return
          // the previous saved log. Keep waiting instead of ending this poll early.
          if (!next || next.state === 'history' || next.operationId === cursor.ignoreOperationId) { await wait(750); continue; }
          const replaced = cursor.operationId !== (next.operationId || '');
          const resetWindow = replaced || Number(next.outputBaseSequence || 0) > cursor.base;
          const output = (resetWindow ? [...(next.output || [])] : [...cursor.output, ...(next.output || [])]).slice(-1000);
          cursor.operationId = next.operationId || '';
          cursor.sequence = Number(next.outputSequence || cursor.sequence);
          cursor.base = Number(next.outputBaseSequence || 0);
          cursor.output = output;
          setOperation({ ...next, output });
          if (!next.running) {
            refresh(false, { suppressFailureToast:true }).catch(() => {});
            break;
          }
        } catch (error) {
          if (!live) return;
        }
        await wait(750);
      }
    }
    void poll();
    return () => { live = false; };
  }, [operation?.running, operation?.operationId, profileId]);
  async function action(path, label) { try { const result = await api(`/api/instances/${profileId}/${path}?serverId=${encodeURIComponent(profile.serverId)}`, { method:'POST', body:'{}', suppressFailureToast:true });
    // Read SteamCMD state before refreshing the whole profile: remote refreshes can
    // take several seconds, leaving the open modal without a phase or live output.
    const operationStatus = result?.operation ? result : await api(`/api/instances/${profileId}/game/update-status?after=0`, { method:'GET', cache:'no-store', suppressFailureToast:true }).catch(()=>null);
    if (operationStatus?.operation) { setOperation(operationStatus.operation); if(operationStatus.operation.running) setSteamModalOpen(true); }
    toast(result.message || label);
    void refresh(false, { suppressFailureToast:true }).catch(() => {});
    return true;
  } catch(e) { toast(e.message, 'error'); return false; } }
  async function start() {
    if (startingServer) return;
    setStartingServer(true);
    setOperation(null);
    try {
      const result = await api(`/api/instances/${profileId}/start?serverId=${encodeURIComponent(profile.serverId)}`, { method:'POST', body:'{}', suppressFailureToast:true });
      toast(result.message || 'Inicialização solicitada.');
      await refresh(false, { suppressFailureToast:true }).catch(() => {});
      const status = await api(`/api/instances/${profileId}/game/update-status?after=0`, { cache:'no-store', suppressFailureToast:true }).catch(() => null);
      if (status?.operation) {
        setOperation(status.operation);
        if (status.operation.running) setSteamModalOpen(true);
      }
      if (result.status === 'running') {
        const servers = await api(`/api/instances/${profileId}/servers`, { cache:'no-store', suppressFailureToast:true }).catch(() => null);
        if (servers?.servers?.some(server => server.id === profile.serverId && server.runtimeStatus === 'running'))
          await refresh(false, { suppressFailureToast:true }).catch(() => {});
        else if (!status?.operation?.running) setStartingServer(false);
      } else if (!result.installing && !status?.operation?.running) setStartingServer(false);
    } catch (error) {
      toast(error.message, 'error');
      setStartingServer(false);
    }
  }
  useEffect(() => {
    if (!startingServer) return;
    if (profile?.runtimeStatus === 'running') { setStartingServer(false); return; }
    if (operation && !operation.running && operation.startsServerAfterInstall && operation.state === 'failed') setStartingServer(false);
  }, [startingServer, profile?.runtimeStatus, operation?.running, operation?.startsServerAfterInstall, operation?.state]);
  useEffect(() => {
    if (!startingServer) return;
    let live = true, timer = 0;
    const deadline = Date.now() + 15 * 60 * 1000;
    const check = async () => {
      try {
        const result = await api(`/api/instances/${profileId}/servers`, { cache:'no-store', suppressFailureToast:true });
        if (!live) return;
        if (result.servers?.some(server => server.id === profile.serverId && server.runtimeStatus === 'running')) {
          await refresh(false, { suppressFailureToast:true }).catch(() => {});
          if (live) timer = window.setTimeout(check, 750);
          return;
        }
      } catch {}
      if (!live) return;
      if (Date.now() >= deadline) {
        setStartingServer(false);
        toast('Não foi possível confirmar que o servidor ficou ativo. Confira o console do servidor e tente novamente.', 'error');
        return;
      }
      timer = window.setTimeout(check, 1000);
    };
    void check();
    return () => { live = false; window.clearTimeout(timer); };
  }, [startingServer, profileId, profile.serverId]);
  async function updateGame() {
    setOperation({ state:'queued', phase:'Preparando SteamCMD', message:'Solicitação enviada. Aguardando o início da operação…', output:[], running:true, startsServerAfterInstall:false, outputBaseSequence:0, outputSequence:0, currentOutputLine:null, operationId:'', ignoreOperationId:operation?.operationId||'' });
    setSteamModalOpen(true);
    if(!await action('game/update', 'Atualização solicitada.')) { setOperation(null); setSteamModalOpen(false); }
  }
  async function abortUpdate() { await action('game/abort', 'Cancelamento solicitado.'); }
  async function saveSettings(event) { event.preventDefault(); const formData = new FormData(event.currentTarget); const values = Object.fromEntries(formData); setSettings('saving'); try { await api(`/api/instances/${profileId}?serverId=${encodeURIComponent(profile.serverId)}`, { method:'PUT', body: json(profilePayload(values, gameId, formData)) }); setSettings(null); toast('Configurações do servidor salvas.'); void refresh(false, { suppressFailureToast:true }).catch(() => {}); } catch(e) { toast(e.message, 'error'); } finally { setSettings(null); } }
  async function authorizeNetwork() {
    if (firewall?.checking) return;
    setFirewall(current => ({...current, checking:true}));
    try {
      const status = await api(`/api/instances/${profileId}/firewall/status?serverId=${encodeURIComponent(profile.serverId)}`);
      setFirewall({...status, checking:false});
      if (status.authorized) return toast('A rede já está autorizada.');
      setFirewall(current => ({...current, checking:true}));
      const result = await api(`/api/instances/${profileId}/firewall/authorize?serverId=${encodeURIComponent(profile.serverId)}`, {method:'POST', body:'{}'});
      setFirewall({authorized:true, verified:true, checking:false});
      toast(result.message);
    } catch (error) {
      setFirewall(current => ({...current, checking:false}));
      toast(error.message, 'error');
    }
  }

  async function copyProfileJson() {
    if(jsonCopyState==='copying'||jsonCopyState==='loading')return;
    if(!profileExportData){toast('Não foi possível preparar o JSON. Reabra Configurações e tente novamente.','error');return;}
    setJsonCopyState('copying');
    const visibleFeedback = new Promise(resolve => window.setTimeout(resolve, 360));
    try {
      await copyTextToClipboard(JSON.stringify(profileExportData, null, 2));
      await visibleFeedback;
      setJsonCopyState('copied');
      toast(`JSON copiado com ${profileExportData.mods?.length || 0} mod(s), versão e estado.`);
      window.clearTimeout(jsonCopyTimer.current);
      jsonCopyTimer.current=window.setTimeout(()=>setJsonCopyState('idle'),1800);
    } catch(e) { await visibleFeedback; setJsonCopyState('idle'); toast(e.message, 'error'); }
  }
  async function removeProfile() { setRemovingProfile(true); try { await new Promise(resolve=>setTimeout(resolve,window.matchMedia('(prefers-reduced-motion: reduce)').matches?0:1050)); await api(`/api/instances/${profileId}?deleteServerFiles=${deleteServerFiles}&deleteSaves=${deleteProfileSaves}`, { method:'DELETE' }); clearProfileConfigDrafts(profileId); await refresh(); navigate(`/games/${gameId}/servers`); } catch(e) { setRemovingProfile(false); toast(e.message, 'error'); } }
  if (!profile) return <Empty title="Carregando perfil…" description="O DSS está atualizando a lista de servidores."/>;
  const passwordMissing = gameId === 'valheim' && !profile.hasPassword;
  const selectedBranchForCard = serverBranches.find(branch => branch.id === selectedServerBranch);
  const selectedBuildForCard = String(selectedBranchForCard?.buildId || '').trim();
  const installedBuildForCard = String(profile.installedBuildId || '').trim();
  const buildProfile = selectedBuildForCard && installedBuildForCard
    ? { ...profile, latestBuildId: selectedBuildForCard, updateAvailable: selectedBuildForCard !== installedBuildForCard }
    : profile;
    return <><PageHeading eyebrow="GERENCIAMENTO DO PERFIL" title={<span className="profile-heading-title">{profile.name}{canEditProfile && <button type="button" className="profile-heading-settings" onClick={() => setProfileDetailsOpen(true)} title="Editar informações do perfil" aria-label="Editar informações do perfil"><Settings size={20}/></button>}</span>} description={`Valheim · ${profile.serverName} · Mundo ${profile.worldName}`} back={{ href:`/games/${gameId}/servers`, label:'← Perfis de Valheim' }} actions={<ProfileHostIndicators profile={profile}/>}/><div className="profile-layout"><aside className={`profile-sidebar ${removingProfile ? 'dss-thanos-card' : ''} ${canEditProfile ? 'profile-access-editable' : ''}`}><ProfileImage profile={profile} className="profile-cover"/><h2>{profile.serverName||profile.name}<span className={`state-badge ${profile.runtimeStatus === 'running' ? 'running' : 'stopped'}`}>{profile.runtimeStatus === 'running' ? 'Em execução' : 'Parado'}</span></h2><small>{profile.serverName} · {profile.port}</small><ServerBuildCard profile={buildProfile} canEdit={canEditProfile} canOperate={canOperateProfile} checking={checkingServerVersion} onCheck={() => checkServerVersion(true)} onUpdate={updateGame} updating={operation?.running} operationRunning={operation?.running} groupRunning={groupRunning} onOpenSteam={() => setSteamModalOpen(true)} branchChangePending={canEditProfile && selectedServerBranch !== savedServerBranch}/><div className={"profile-controls " + (canOperateProfile && !canEditProfile ? "profile-operate-controls" : "")}>{startingServer ? <Button variant="primary" className="w-full start-server-button starting" disabled aria-live="polite" role="status"><span className="start-button-content"><LoaderCircle size={16} className="spin"/> Iniciando servidor…</span><span className="start-button-sparks" aria-hidden="true"><i/><i/><i/><i/></span></Button> : profile.runtimeStatus === 'running' ? <><LongPressButton variant="danger" className="w-full user-server-action" onLongPress={() => action('stop','Servidor parado.')}>■ Parar servidor</LongPressButton>{canManageServer && <LongPressButton variant="danger" className="w-full force-stop-button" durationMs={5000} title="Segure por 5 segundos para encerrar sem salvamento seguro" onLongPress={() => action('force-stop','Servidor encerrado à força.')}><Power size={15}/> Forçar encerramento</LongPressButton>}</> : <Button variant="primary" className={`w-full start-server-button user-server-action ${startingServer ? 'starting' : ''}`} disabled={operation?.running || startingServer || passwordMissing} title={passwordMissing ? "Defina uma senha em Configurações antes de iniciar" : "Iniciar servidor"} onClick={start}><span className="start-button-content"><Play size={16} fill="currentColor" className={startingServer ? 'power-up-icon' : ''}/>{startingServer ? 'Iniciando servidor…' : passwordMissing ? 'Defina a senha para iniciar' : 'Iniciar servidor'}</span><span className="start-button-sparks" aria-hidden="true"><i/><i/><i/><i/></span></Button>}{!firewall?.checking && !firewall?.authorized && <div className="network-start-warning" role="status"><CircleAlert size={14}/><span>Falta liberar a rede no Firewall do Windows. O servidor pode iniciar, mas conexões remotas podem ser bloqueadas.</span><button type="button" onClick={() => navigate(profilePath + serverQuery)}>Configurar</button></div>}{profile.runtimeStatus === 'running' && <LongPressButton className="w-full user-server-action" onLongPress={() => action('restart','Servidor reiniciado.')}>↻ Reiniciar</LongPressButton>}{canManageServer && <><Button className="w-full" onClick={() => setConsoleOpen(true)}><Terminal size={16}/> Console</Button>{gameId === 'valheim' && <Button className="w-full" onClick={() => setPlayerManagementOpen(true)}><Users size={16}/> Gestão</Button>}</>}<ServerAddressChooser profileId={profileId} serverId={profile.serverId} port={profile.port} toast={toast}/></div></aside><main className={`profile-main ${canEditProfile ? 'profile-access-editable' : ''}`}><MultiServerPanel profile={profile} instances={instances} canEditProfile={canEditProfile}/><nav className="profile-tabs"><Link className={tab==='mods'?'active':''} to={`/games/${gameId}/servers/${profileId}/mods${serverQuery}`}>Mods do perfil</Link><Link className={tab==='catalog'?'active':''} to={`/games/${gameId}/servers/${profileId}/mods/buscar${serverQuery}`}>Buscar mods</Link><Link className={tab==='settings'?'active':''} to={`/games/${gameId}/servers/${profileId}${serverQuery}`}>Configurações</Link><Link className={tab==='bepinex'?'active':''} to={`/games/${gameId}/servers/${profileId}/bepinex${serverQuery}`}>BepInEx{profileConfigDirty && <span className="dirty-dot profile-tab-dirty" title="Há alterações não salvas" aria-label="Há arquivos BepInEx não salvos"/>}</Link><Link className={tab==='history'?'active':''} to={`/games/${gameId}/servers/${profileId}/historico${serverQuery}`}>Histórico</Link></nav>{tab === 'settings' && (canEditProfile ? <section className="panel-card"><div className="panel-head"><div><h2>Configuração deste servidor</h2><p>Porta, mundo e opções próprias do servidor selecionado; mods e arquivos BepInEx ficam no perfil.</p></div><span className={`state-badge ${profile.runtimeStatus === 'running' ? 'running' : 'stopped'}`}>{profile.runtimeStatus === 'running' ? 'PARE PARA EDITAR' : 'EDITÁVEL'}</span></div>{profile.runtimeStatus === 'running' && <div className="notice">Pare o servidor para alterar os parâmetros de inicialização.</div>}{!canEditProfile&&<div className="notice profile-read-only-note">Configurações do perfil, mods e arquivos BepInEx ficam somente para leitura. O console e a Gestão de jogadores permitem as ações operacionais.</div>}<div className="profile-config-gated" inert={!canEditProfile}><section className="network-access-panel"><span className="network-access-icon"><ShieldCheck size={20}/></span><div className="network-access-copy"><div className="network-access-title"><h3>Acesso de rede</h3><span className={`state-badge ${firewall?.authorized ? 'running' : 'stopped'}`}>{firewall?.checking ? 'VERIFICANDO' : firewall?.authorized ? 'AUTORIZADA' : 'FALTA LIBERAR'}</span></div><p>O servidor pode iniciar sem esta regra, mas o Firewall pode bloquear conexões pela rede. A regra padrão libera as portas apenas em redes privadas.</p></div><Button className="network-access-action" disabled={!canEditProfile || firewall?.checking || firewall?.authorized} onClick={authorizeNetwork}>{firewall?.checking ? <><RefreshCw size={15} className="spin"/> Verificando…</> : firewall?.authorized ? <><ShieldCheck size={15}/> Rede autorizada</> : <><ShieldCheck size={15}/> Autorizar rede</>}</Button></section><form className="form-grid" autoComplete="off" onSubmit={saveSettings}><PasswordAutofillDecoys/><input type="hidden" name="gameSettings" value={JSON.stringify(profile.gameSettings || {})}/><input type="hidden" name="name" value={profile.name}/><input type="hidden" name="serverName" value={profile.serverName}/><input type="hidden" name="worldName" value={profile.worldName}/><Field label="Porta base" name="port" type="number" min="1024" max="65532" step="1" defaultValue={profile.port}/>{gameId === 'valheim' && <ServerBranchField profile={profile} branches={serverBranches} branchStatus={branchStatus} selected={selectedServerBranch} onChange={setSelectedServerBranch} disabled={groupRunning}/>}<Field label="Pasta do servidor dedicado" name="installPath" required readOnly={sharedProfileCount > 1} defaultValue={profile.installPath} className="full-field"/>{sharedProfileCount > 1 && <small className="full-field shared-install-note">Instalação, mods e BepInEx são compartilhados pelos {sharedProfileCount} servidores deste perfil.</small>}<Field label="Pasta personalizada de saves" name="saveDirectory" defaultValue={profile.useInstallPathSaves ? '' : profile.saveDirectory} placeholder="Vazio usa a pasta Saves do servidor" className="full-field"/><fieldset className="server-option-group full-field"><legend>Dados e acesso do servidor</legend><div className="server-option-grid"><label className="profile-check"><input type="checkbox" name="useInstallPathSaves" defaultChecked={profile.useInstallPathSaves}/><span><b>Salvar junto aos arquivos do jogo</b><small>Usa a pasta Saves da instalação compartilhada.</small></span></label><label className="profile-check"><input type="checkbox" name="isPublic" defaultChecked={profile.isPublic}/><span><b>Servidor público</b><small>Exibe o servidor na lista pública do Valheim.</small></span></label><label className="profile-check"><input type="checkbox" name="crossplay" defaultChecked={profile.crossplay}/><span><b>Crossplay</b><small>Permite conexões entre plataformas suportadas.</small></span></label></div></fieldset><Field label={profile.hasPassword ? 'Nova senha (vazio mantém)' : 'Senha do servidor'} name="serverPasswordDraft" type="password" autoComplete="new-password" data-lpignore="true" data-1p-ignore="true" data-bwignore="true" data-form-type="other"/>{gameId === 'valheim' && <ValheimLaunchOptions settings={profile.gameSettings} disabled={profile.runtimeStatus === 'running'}/>}<div className="full-field launch-args"><h3>Parâmetros incluídos automaticamente</h3><p>Os dados de identificação do perfil ficam no botão de edição ao lado do título. Porta, senha, visibilidade, Crossplay, saves e modificadores têm controles próprios.</p><div className="launch-flag-list"><span>-nographics</span><span>-batchmode</span></div><label className="field-label">Argumentos complementares<textarea className="field code-area" name="extraArguments" defaultValue={(profile.extraArguments || []).join(' ')} placeholder="Argumentos de ferramentas externas, se necessário"/><small>Use somente argumentos extras que não tenham um controle próprio nesta tela.</small></label></div><div className="form-actions full-field settings-actions"><Button type="button" className={'copy-profile-json ' + jsonCopyState} disabled={jsonCopyState==='copying'||jsonCopyState==='loading'} onClick={copyProfileJson}>{jsonCopyState==='copied'?<Check size={15}/>:jsonCopyState==='copying'||jsonCopyState==='loading'?<LoaderCircle size={15} className="spin"/>:<Copy size={15}/>} {jsonCopyState==='copied'?'JSON copiado':jsonCopyState==='copying'||jsonCopyState==='loading'?'Preparando JSON…':'Copiar JSON'}</Button><Button variant="primary" type="submit" disabled={profile.runtimeStatus === 'running' || settings === 'saving'}>{settings === 'saving' ? 'Salvando…' : 'Salvar alterações'}</Button></div></form></div></section> : <ProfileSettingsReadOnly profile={profile}/>)}{tab==='mods' && <InstalledMods profile={profile} canEditProfile={canEditProfile} groupRunning={groupRunning} initialUpdates={installedModUpdates} onUpdates={setInstalledModUpdates}/>}{tab==='catalog' && <Catalog profile={profile} canEditProfile={canEditProfile} groupRunning={groupRunning}/>}{tab==='bepinex' && <BepInExEditor key={profileId + (canEditProfile ? 'edit' : 'view')} profile={profile} groupRunning={groupRunning} accessReadOnly={!canEditProfile}/>}{tab==='history' && <ProfileHistory profile={profile} canEditProfile={canEditProfile}/>}</main></div>{profileDetailsOpen && <ProfileDetailsModal profile={profile} canEditProfile={canEditProfile} close={() => setProfileDetailsOpen(false)} refresh={refresh} onRemoveProfile={() => setProfileRemovalOpen(true)}/>}{profileRemovalOpen && <ProfileRemovalModal profile={profile} close={() => setProfileRemovalOpen(false)} deleteServerFiles={deleteServerFiles} setDeleteServerFiles={setDeleteServerFiles} deleteProfileSaves={deleteProfileSaves} setDeleteProfileSaves={setDeleteProfileSaves} serverFilesShared={serverFilesShared} removingProfile={removingProfile} groupRunning={groupRunning} operationRunning={operation?.running} onConfirm={removeProfile}/>}{consoleOpen && <ServerConsole profile={profile} close={() => setConsoleOpen(false)} onAction={action} readOnly={!canManageServer} canInstallConsoleMod={canEditProfile}/>}{playerManagementOpen && <PlayerManagementModal profile={profile} close={() => setPlayerManagementOpen(false)}/>}{steamModalOpen && <Modal className={canManageServer ? "steamcmd-modal profile-operate-controls" : "steamcmd-modal"} title="SteamCMD · arquivos do jogo" description={operation?.message || "A saída da instalação e da atualização aparece aqui em tempo real."} close={() => setSteamModalOpen(false)}><section className={`operation-card ${operation?.running ? "steamcmd-running" : "steamcmd-finished"}`}><div className="steamcmd-operation-heading"><div><b>{operation?.phase || "Nenhuma operação recente"}</b><p>{operation?.message || "As operações SteamCMD ficam associadas ao perfil compartilhado."}</p></div>{operation?.running && canManageServer && <LongPressButton variant="danger" onLongPress={abortUpdate}>Segure para abortar</LongPressButton>}</div><SteamCommandOutput profileId={profile.id} operation={operation || {output:[]}} toast={toast}/></section></Modal>}</>;
}

function ServerBranchField({ profile, branches, branchStatus, selected, onChange, disabled }) {
  const savedBranch = typeof profile.gameSettings?.serverBranch === 'string' ? profile.gameSettings.serverBranch : 'public';
  const selectedBranch = branches.find(branch => branch.id === selected);
  const installedBuild = String(profile.installedBuildId || '').trim();
  const selectedBuild = String(selectedBranch?.buildId || '').trim();
  const installedNumber = Number(installedBuild);
  const selectedNumber = Number(selectedBuild);
  const comparable = Number.isFinite(installedNumber) && Number.isFinite(selectedNumber) && installedNumber > 0 && selectedNumber > 0;
  const hasDifferentBuild = comparable && selectedNumber !== installedNumber;
  const status = !profile.serverInstalled
    ? { tone:'neutral', text:'O servidor ainda não está instalado neste perfil.' }
    : !installedBuild
      ? { tone:'neutral', text:'A versão instalada ainda não foi identificada.' }
      : comparable && hasDifferentBuild
        ? { tone:'available', text:`Build selecionada: ${selectedBuild} · instalada: ${installedBuild}. Atualizar aplicará esta versão.` }
        : comparable
            ? { tone:'current', text:`Build ${installedBuild} já corresponde a esta branch.` }
            : selected !== savedBranch
              ? { tone:'neutral', text:`Build instalada: ${installedBuild}. A branch selecionada não informa um build para comparação.` }
              : { tone:'current', text:`Build instalada: ${installedBuild}.` };
  const listed = branches.some(branch => branch.id === selected);
  return <><label className="field-label server-branch-field">Versão do servidor dedicado<select className="field" name="serverBranch" value={selected} onChange={event => onChange(event.target.value)} disabled={disabled}>{!listed && <option value={selected}>{branchStatus === 'loading' ? 'Carregando versões…' : selected === 'public' ? 'Pública atual' : `Branch salva: ${selected}`}</option>}{branches.map(branch => <option key={branch.id} value={branch.id} title={branch.description}>{branch.name}{branch.buildId ? ` · build ${branch.buildId}` : ''}</option>)}</select><small>Escolha a branch que o SteamCMD usará ao instalar ou atualizar este servidor. A opção pública é a padrão.</small></label><div className={`server-branch-version-status full-field ${status.tone}`} role="status">{status.tone === 'available' ? <Download size={14}/> : status.tone === 'current' ? <Check size={14}/> : <CircleAlert size={14}/>}<span>{status.text}</span></div></>;
}

function ServerBuildCard({ profile, canEdit = true, canOperate = false, checking, onCheck, onUpdate, updating, operationRunning, groupRunning, onOpenSteam, branchChangePending }) {
  const canCheck = canEdit || canOperate;
  const canUpdate = canEdit || (canOperate && profile.serverInstalled);
  const canViewOperation = canEdit || canOperate;
  const installedBuild = profile.installedBuildId?.trim();
  const latestBuild = profile.latestBuildId?.trim();
  let status = 'Aguardando verificação';
  let statusClass = '';
  if (!profile.serverInstalled) status = 'Servidor não instalado';
  else if (branchChangePending) { status = 'Salve a branch selecionada para aplicar esta versão.'; statusClass = 'available'; }
  else if (checking) status = 'Consultando a branch selecionada…';
  else if (profile.updateCheckError) { status = `Falha na consulta: ${profile.updateCheckError}`; statusClass = 'error'; }
  else if (profile.updateAvailable) { status = `Versão da branch selecionada · build ${latestBuild || 'desconhecido'}`; statusClass = 'available'; }
  else if (profile.updateAvailable === false) { status = 'Instalação corresponde à branch selecionada'; statusClass = 'current'; }

  return <section className={"server-build-card " + (profile.updateAvailable ? "available " : "") + (canOperate && !canEdit ? "profile-operate-controls" : "")} aria-live="polite">
    <div className="server-build-head"><div><span className="eyebrow">VERSÃO DO SERVIDOR</span><strong>{profile.serverInstalled ? installedBuild ? `Build ${installedBuild}` : 'Build não identificado' : 'Não instalado'}</strong></div>{canCheck&&<Button className="small" disabled={!profile.serverInstalled || checking || branchChangePending} onClick={onCheck} title={branchChangePending ? 'Salve a branch antes de consultar' : 'Consultar o build da branch selecionada'}><RefreshCw size={14} className={checking ? 'spin' : ''}/>{checking ? 'Verificando' : 'Verificar'}</Button>}</div>
    <div className="server-build-summary"><small className={`server-build-status ${statusClass}`}>{profile.updateAvailable&&<Download size={13}/>} {status}</small>{profile.lastUpdateCheck && <small className="server-build-checked">Última consulta: {fmtDate(profile.lastUpdateCheck)}</small>}</div>
    {groupRunning && <small className="server-build-stop-hint">Pare todos os servidores deste perfil para instalar ou atualizar os arquivos compartilhados.</small>}
    <div className="server-build-actions">{canUpdate&&<Button variant={!groupRunning && (!profile.serverInstalled || profile.updateAvailable)?'primary':undefined} className={`server-build-update ${!groupRunning && (!profile.serverInstalled || profile.updateAvailable)?'server-update-button':''}`} disabled={!profile.installPath || updating || operationRunning || groupRunning || branchChangePending} title={groupRunning ? 'Pare todos os servidores deste perfil antes de instalar ou atualizar.' : branchChangePending ? 'Salve a branch selecionada antes de atualizar.' : undefined} onClick={onUpdate}><Download size={14}/>{updating ? 'Atualizando…' : groupRunning ? 'Pare para atualizar' : branchChangePending ? 'Salve a versão selecionada' : !profile.serverInstalled ? 'Instalar servidor' : profile.updateAvailable ? 'Aplicar versão selecionada' : 'Atualizar jogo'}{!groupRunning && profile.updateAvailable && <i className="update-dot"/>}</Button>}{canViewOperation&&<Button className="server-build-steamcmd" onClick={onOpenSteam}><Terminal size={14}/>{operationRunning ? 'SteamCMD em execução' : 'Abrir SteamCMD'}{operationRunning && <span className="steamcmd-live-dot" title="SteamCMD em execução"/>}</Button>}</div>
  </section>;
}

function ProfileRemovalModal({ profile, close, deleteServerFiles, setDeleteServerFiles, deleteProfileSaves,
  setDeleteProfileSaves, serverFilesShared, removingProfile, groupRunning, operationRunning, onConfirm }) {
  return <Modal title="Remover perfil" description="O perfil será removido do DSS. Selecione os arquivos que deseja excluir; as opções vêm desmarcadas." close={close} className="profile-removal-modal">
    <div className="profile-removal-warning"><ShieldCheck size={18}/><span>Sem seleção, os arquivos do jogo e todos os saves ficam no computador e podem ser usados depois.</span></div>
    <label className="profile-removal-option"><input type="checkbox" checked={deleteServerFiles} disabled={!profile.installPath||serverFilesShared||removingProfile} onChange={event=>setDeleteServerFiles(event.target.checked)}/><span><b>Excluir arquivos do servidor</b><small>{profile.installPath||'Pasta do servidor não definida'}{serverFilesShared?' · Esta pasta também é usada por outro perfil.':''}</small></span></label>
    {serverFilesShared&&<small className="profile-removal-note">A pasta está compartilhada e ficará protegida; remova este perfil mantendo os arquivos.</small>}
    <label className="profile-removal-option"><input type="checkbox" checked={deleteProfileSaves} disabled={removingProfile} onChange={event=>setDeleteProfileSaves(event.target.checked)}/><span><b>Excluir saves de todos os servidores deste perfil</b><small>{profile.saveDirectory||(profile.useInstallPathSaves?profile.installPath+'\\Saves':'Pasta de saves gerenciada pelo DSS')}</small></span></label>
    <p className="profile-removal-note">A exclusão de arquivos e saves é permanente. Segure o botão por 15 segundos para confirmar esta remoção.</p>
    <div className="modal-actions"><Button disabled={removingProfile} onClick={close}>Cancelar</Button><LongPressButton variant="danger" className="profile-removal-submit" durationMs={15000} disabled={removingProfile||groupRunning||operationRunning} title="Segure por 15 segundos para remover o perfil e excluir somente os itens marcados" onLongPress={onConfirm}><Trash2 size={15}/>{removingProfile?'Removendo…':'Segure 15 s para remover'}</LongPressButton></div>
  </Modal>;
}

function ProfileDetailsModal({ profile, canEditProfile, close, refresh, onRemoveProfile }) {
  const { setProfileImageUrl } = useDss();
  const [name, setName] = useState(profile.name);
  const [serverName, setServerName] = useState(profile.serverName);
  const [worldName, setWorldName] = useState(profile.worldName);
  const [imageData, setImageData] = useState('');
  const [preview, setPreview] = useState('');
  const [removeImage, setRemoveImage] = useState(false);
  const [saving, setSaving] = useState(false);
  const input = useRef();
  const toast = useToast();
  const canEditInfo = canEditProfile && profile.runtimeStatus !== 'running';
  const infoChanged = name.trim() !== profile.name || serverName.trim() !== profile.serverName || worldName.trim() !== profile.worldName;
  const photoChanged = canEditProfile && (Boolean(imageData) || (removeImage && profile.hasImage));
  const canSave = photoChanged || (canEditInfo && infoChanged);

  function selectImage(file) {
    if (!file) return;
    const reader = new FileReader();
    reader.onerror = () => toast('Não foi possível ler a imagem selecionada.', 'error');
    reader.onload = () => {
      const image = new Image();
      image.onerror = () => toast('O arquivo selecionado não é uma imagem válida.', 'error');
      image.onload = () => {
        try {
          const side = Math.min(image.naturalWidth, image.naturalHeight);
          const canvas = document.createElement('canvas');
          canvas.width = canvas.height = 512;
          const context = canvas.getContext('2d');
          context.drawImage(image, (image.naturalWidth - side) / 2, (image.naturalHeight - side) / 2, side, side, 0, 0, 512, 512);
          const cropped = canvas.toDataURL('image/jpeg', .88);
          setImageData(cropped);
          setPreview(cropped);
          setRemoveImage(false);
        } catch (error) { toast(error.message || 'Não foi possível preparar a imagem.', 'error'); }
      };
      image.src = String(reader.result || '');
    };
    reader.readAsDataURL(file);
  }

  async function save(event) {
    event.preventDefault();
    if (!canSave) return;
    setSaving(true);
    try {
      if (canEditInfo && infoChanged) {
        await api(`/api/instances/${profile.id}?serverId=${encodeURIComponent(profile.serverId||profile.id)}`, { method: 'PUT', body: json({
          gameId: profile.gameId,
          name: name.trim(),
          installPath: profile.installPath,
          serverName: serverName.trim(),
          worldName: worldName.trim(),
          gamePassword: '',
          port: profile.port,
          isPublic: profile.isPublic,
          crossplay: profile.crossplay,
          extraArguments: profile.extraArguments || [],
          saveDirectory: profile.saveDirectory || '',
          gameSettings: profile.gameSettings || {},
          useInstallPathSaves: Boolean(profile.useInstallPathSaves)
        }) });
      }
      if (imageData) {
        const savedImage = await api(`/api/instances/${profile.id}/image`, { method: 'PUT', body: json({ imageData }) });
        setProfileImageUrl(profile.id, savedImage.imageUrl || `/api/instances/${profile.id}/image?v=${Date.now()}`);
      }
      else if (removeImage && profile.hasImage) await api(`/api/instances/${profile.id}/image`, { method: 'DELETE' });
      await refresh();
      toast('Informações do perfil atualizadas.');
      close();
    } catch (error) {
      await refresh();
      toast(error.message, 'error');
    } finally { setSaving(false); }
  }

  function clearImage() {
    setImageData('');
    setPreview('');
    setRemoveImage(Boolean(profile.hasImage));
    if (input.current) input.current.value = '';
  }

  const currentImage = profile.hasImage && !removeImage ? `/api/instances/${profile.id}/image?v=${encodeURIComponent(profile.updatedAt || '')}` : '';
  return <Modal title="Configurações do perfil" description="Identidade, imagem, acesso e ações de administração deste perfil." close={close} className={`profile-details-modal ${canEditProfile ? 'profile-access-editable' : ''}`}>
    <form className="profile-details-form" onSubmit={save}>
      <div className="profile-edit-photo">
        <div className="profile-edit-photo-preview">{preview || currentImage ? <img src={preview || currentImage} alt="Prévia da imagem do perfil"/> : <ImagePlus size={30}/>}</div>
        <div className="profile-edit-photo-controls">
          <b>Imagem do perfil</b>
          <small>Use uma imagem quadrada ou escolha a área central; o DSS salva um recorte quadrado.</small>
          <input ref={input} type="file" accept="image/*" hidden onChange={event => selectImage(event.target.files?.[0])}/>
          <div><Button type="button" onClick={() => input.current?.click()}><ImagePlus size={15}/>{profile.hasImage && !removeImage || preview ? 'Alterar imagem' : 'Adicionar imagem'}</Button>{(profile.hasImage || preview) && <Button type="button" variant="danger" onClick={clearImage}>Remover imagem</Button>}</div>
        </div>
      </div>
      {profile.runtimeStatus === 'running' && <div className="notice">Pare o servidor para editar o nome, o nome público ou o mundo. A imagem ainda pode ser alterada agora.</div>}
      <div className="form-grid profile-details-fields">
        <Field label="Nome do perfil" name="profileName" required maxLength="60" value={name} disabled={!canEditInfo} onChange={event => setName(event.target.value)}/>
        <Field label="Nome público" name="serverName" required maxLength="60" value={serverName} disabled={!canEditInfo} onChange={event => setServerName(event.target.value)}/>
        <Field label="Mundo" name="worldName" required maxLength="40" value={worldName} disabled={!canEditInfo} onChange={event => setWorldName(event.target.value)}/>
      </div>
      <div className="modal-actions"><Button type="button" onClick={close}>Cancelar</Button><Button type="submit" variant="primary" disabled={saving || !canSave}>{saving ? 'Salvando…' : 'Salvar alterações'}</Button></div>
    </form>
    {canEditProfile && <ProfileAccessPanel profile={profile}/>}
    {canEditProfile&&<section className="profile-danger-zone profile-details-danger-zone"><div><h3>Remover perfil</h3><p>Os arquivos do servidor e os saves são mantidos por padrão. Você poderá escolher o que excluir antes de confirmar.</p></div><Button variant="danger" className="profile-danger-confirm" onClick={() => { close(); onRemoveProfile(); }}><Trash2 size={15}/> Remover perfil</Button></section>}
  </Modal>;
}

function ServerConsole({ profile, close, readOnly = false, canInstallConsoleMod = true }) {
  const [entries,setEntries]=useState([]); const sequence=useRef(0); const [running,setRunning]=useState(profile.runtimeStatus==='running'); const [consoleConnected,setConsoleConnected]=useState(false); const [command,setCommand]=useState(''); const [auto,setAuto]=useState(true); const [stdinModReady,setStdinModReady]=useState(false); const [installingStdinMod,setInstallingStdinMod]=useState(false); const output=useRef(null); const toast=useToast();
  const loadModState=useCallback(async()=>{try{const result=await api(`/api/instances/${profile.id}/mods`,{suppressFailureToast:true});setStdinModReady((result.packages||[]).some(mod=>mod.packageId?.toLowerCase()==='dreamwraith-consolestdinheeler'&&mod.enabled));}catch{}},[profile.id]);
  useEffect(() => { let live=true;sequence.current=0;setEntries([]);setRunning(profile.runtimeStatus==='running');const poll=async()=>{ try{const r=await api(`/api/instances/${profile.id}/console?serverId=${encodeURIComponent(profile.serverId||profile.id)}&after=${sequence.current}`);if(!live)return;setEntries(current=>[...current,...(r.entries||[])].slice(-1000));sequence.current=r.sequence??sequence.current;setRunning(r.running);setConsoleConnected(r.consoleConnected===true);}catch{}};poll();const t=setInterval(poll,900);return()=>{live=false;clearInterval(t);}; },[profile.id,profile.serverId]);
  useEffect(()=>{void loadModState();},[loadModState]);
  useEffect(()=>{if(auto&&output.current)output.current.scrollTop=output.current.scrollHeight;},[entries,auto]);
  async function send(e){e.preventDefault();if(readOnly||!command.trim()||!stdinModReady)return;try{const r=await api(`/api/instances/${profile.id}/commands?serverId=${encodeURIComponent(profile.serverId||profile.id)}`,{method:'POST',body:json({command:command.trim()})});toast(r.message);setCommand('');}catch(e){toast(e.message,'error');}}
  async function installConsoleMod(){if(running||installingStdinMod)return;setInstallingStdinMod(true);try{const result=await api(`/api/instances/${profile.id}/mods/install`,{method:'POST',body:json({provider:'thunderstore',packageId:'DreamWraith-ConsoleStdinHeeler'})});toast(result.message,'success',{title:'Requisito do console instalado',detail:'Reinicie o servidor para ativar a leitura de comandos.',durationMs:10000});await loadModState();}catch(error){toast(error.message,'error',{title:'Não foi possível instalar o requisito'});}finally{setInstallingStdinMod(false);}}
  async function doAction(name){try{const r=await api(`/api/instances/${profile.id}/${name}?serverId=${encodeURIComponent(profile.serverId||profile.id)}`,{method:'POST',body:'{}'});toast(r.message);setRunning(name==='restart');}catch(e){toast(e.message,'error');}}
  const consoleDescription = readOnly ? !running ? 'Servidor parado · exibindo a saída recente' : consoleConnected ? 'Conectado · console em modo somente leitura' : 'Servidor ativo · console da sessão anterior indisponível' : !running ? 'Servidor parado · exibindo a saída recente' : consoleConnected ? stdinModReady ? 'Conectado · saída atualizada em tempo real' : 'Saída conectada · instale o requisito para enviar comandos' : 'Servidor ativo · console da sessão anterior indisponível; comandos não podem ser enviados';
  const emptyConsoleMessage = running && !consoleConnected ? 'A sessão anterior não permite recuperar a saída. Próximos inícios terão console persistente.' : 'Aguardando saída do servidor…';
  const visibleLog = entries.length ? entries.map(e=>`[${new Date(e.timestamp).toLocaleTimeString()}] [${e.stream}] ${e.text}`).join('\n') : '';
  async function copyConsoleLog(){try{const count=await copyFullLog('',visibleLog);toast(`Saída visível do console copiada (${count.toLocaleString()} caracteres).`,'success',{title:'Console copiado'});}catch(error){toast(error.message,'error');}}
  async function downloadConsoleLog(){try{const count=await downloadFullLog(`/api/instances/${profile.id}/console/log-file?serverId=${encodeURIComponent(profile.serverId||profile.id)}`,visibleLog,`console-${profile.serverName||profile.name}.log`);toast(`Log completo do console baixado (${count.toLocaleString()} caracteres).`,'success',{title:'Download do console'});}catch(error){toast(error.message,'error');}}
  return <Modal className={readOnly ? "console-modal" : "console-modal profile-operate-controls"} title={`Console · ${profile.name}`} description={consoleDescription} close={close}><label className="check-row"><input type="checkbox" checked={auto} onChange={e=>setAuto(e.target.checked)}/> Rolar automaticamente para o último registro</label><small className="console-history-limit">Exibindo as últimas 1.000 linhas. Baixe o log para acessar o histórico completo.</small><div className="console-log-frame"><pre ref={output} className="console-output" aria-live="polite">{visibleLog||emptyConsoleMessage}</pre><div className="console-log-actions"><button type="button" title="Copiar console exibido" aria-label="Copiar console exibido" onClick={copyConsoleLog}><Copy size={15}/></button><button type="button" title="Baixar log completo" aria-label="Baixar log completo do console" onClick={downloadConsoleLog}><Download size={15}/></button></div></div>{!stdinModReady&&canInstallConsoleMod&&<section className="console-mod-requirement"><span><ShieldCheck size={18}/></span><div><b>Comandos exigem ConsoleStdinHeeler</b><p>O servidor dedicado vanilla não lê comandos do terminal. Instale o mod DreamWraith e reinicie o servidor para habilitar este campo.</p></div><Button size="small" variant="primary" disabled={running||installingStdinMod} onClick={installConsoleMod}>{installingStdinMod?<><LoaderCircle size={14} className="spin"/> Instalando…</>:running?'Pare para instalar':'Instalar requisito'}</Button></section>}{!stdinModReady&&!canInstallConsoleMod&&!readOnly&&<div className="notice">Um Editor precisa instalar o requisito do console antes de enviar comandos.</div>}{!readOnly&&<form onSubmit={send} className="command-form"><label>Comando do servidor</label><div><input className="field" value={command} onChange={e=>setCommand(e.target.value)} disabled={!running||!consoleConnected||!stdinModReady} placeholder={!running?'Inicie o servidor para enviar comandos':!stdinModReady?'Instale ConsoleStdinHeeler para habilitar os comandos':consoleConnected?'Digite um comando e pressione Enter':'Disponível quando o console persistente estiver conectado'}/><Button variant="primary" disabled={!running||!consoleConnected||!stdinModReady}>Enviar</Button></div></form>}<div className="modal-actions"><LongPressButton disabled={!running} onLongPress={()=>doAction('restart')}>↻ Reiniciar</LongPressButton><LongPressButton variant="danger" disabled={!running} onLongPress={()=>doAction('stop')}>■ Parar</LongPressButton><LongPressButton variant="danger" className="force-stop-button" disabled={!running} durationMs={5000} title="Segure por 5 segundos para encerrar sem salvamento seguro" onLongPress={()=>doAction('force-stop')}><Power size={15}/> Forçar encerramento</LongPressButton></div></Modal>;
}

function PlayerManagementModal({ profile, close }) {
  const [snapshot,setSnapshot]=useState(null),[loading,setLoading]=useState(true),[refreshing,setRefreshing]=useState(false);
  const [actionKey,setActionKey]=useState(''),[userId,setUserId]=useState(''),[manualAction,setManualAction]=useState('admin'),[error,setError]=useState(''),[rowAnimation,setRowAnimation]=useState(null);
  const toast=useToast(),confirm=useConfirm();
  const refresh=useCallback(async(silent=false)=>{
    if(!silent)setRefreshing(true);
    try{const value=await api(`/api/instances/${profile.id}/players?serverId=${encodeURIComponent(profile.serverId||profile.id)}`,{suppressFailureToast:true});setSnapshot(value);setError('');}
    catch(e){setError(e.message);}
    finally{setLoading(false);if(!silent)setRefreshing(false);}
  },[profile.id]);
  useEffect(()=>{void refresh();const timer=setInterval(()=>void refresh(true),8000);return()=>clearInterval(timer);},[refresh]);
  async function changeAccess(action,id,playerName=''){
    if(!id||actionKey)return false;
    if((action==='ban'||action==='kick')&&!await confirm({title:action==='kick'?'Kickar personagem':'Banir ID de plataforma',description:action==='kick'?`Remover a personagem ${playerName||id} do servidor agora? Isso não adiciona o ID à lista de banimento.`:`Banir o ID de plataforma ${id} e bloquear todas as personagens associadas nos próximos acessos? O Valheim não mantém banimentos persistentes por personagem.`,confirmLabel:action==='kick'?'Kickar personagem':'Banir ID',danger:true}))return false;
    const permittedIds=snapshot?.permitted||[];
    if(action==='allow'&&!permittedIds.length&&!await confirm({title:'Ativar a lista de permitidos?',description:`Adicionar ${playerName||id} ativa a lista de permitidos. A partir daí, somente os IDs incluídos poderão entrar; os demais jogadores serão impedidos de conectar.`,confirmLabel:'Ativar e permitir jogador',cancelLabel:'Cancelar'}))return false;
    if(action==='unallow'&&permittedIds.length===1&&permittedIds[0]===id&&!await confirm({title:'Desativar a lista de permitidos?',description:'Este é o último ID da lista. Ao removê-lo, a lista ficará vazia e deixará de restringir quem pode entrar no servidor.',confirmLabel:'Remover último ID',cancelLabel:'Cancelar'}))return false;
    const key=`${action}:${id}`;setActionKey(key);
    try{const result=await api(`/api/instances/${profile.id}/players/${action}?serverId=${encodeURIComponent(profile.serverId||profile.id)}`,{method:'POST',body:json({userId:id,playerName})});const nonce=Date.now();setRowAnimation({key:id,action,nonce});window.setTimeout(()=>setRowAnimation(current=>current?.nonce===nonce?null:current),760);toast(result.message,'success',{title:action==='kick'?'Kick confirmado':'Acesso atualizado',durationMs:5000});if(action==='kick')await new Promise(resolve=>window.setTimeout(resolve,460));await refresh(true);return true;}
    catch(e){toast(e.message,'error');return false;}
    finally{setActionKey('');}
  }
  async function submitId(event){event.preventDefault();if(await changeAccess(manualAction,userId.trim()))setUserId('');}
  const players=snapshot?.players||[];
  const activeCount=snapshot?.activeCount;
  return <Modal className="player-management-modal profile-operate-controls" title={`Gestão de jogadores · ${profile.name}`} description="Acompanhe a sessão, consulte o tempo registrado e edite os acessos do servidor." close={close}>
    <div className="player-management-summary">
      <div className="player-count-card"><span className="player-count-icon"><Users size={19}/></span><div><small>JOGADORES ATIVOS</small><strong>{activeCount==null?'—':activeCount}</strong><span>{snapshot?.countSource||'Consultando servidor'}</span></div><Button onClick={()=>refresh()} disabled={refreshing} title="Atualizar jogadores"><RefreshCw size={15} className={refreshing?'spin':''}/></Button></div>
      <div className="player-access-card"><ShieldCheck size={19}/><div><b>Controle de acesso</b><span>Use o ID da plataforma para gerenciar admins, banimentos e permissões de entrada.</span></div></div>
    </div>
    <div className="notice player-restart-notice">O histórico de tempo é salvo nos dados persistentes deste perfil e separado por personagem. O Valheim grava banimentos pelo ID da plataforma; banir uma personagem também bloqueia as outras que usam o mesmo ID.{snapshot?.serverRunning&&' Kicks da personagem conectada são enviados ao servidor. Alterações em admins e na lista de permitidos são gravadas nos arquivos oficiais; o Valheim pode exigir que o jogador reconecte ou que o servidor reinicie para aplicá-las.'}</div>
    {snapshot?.permitted?.length>0&&<div className="notice player-whitelist-notice" role="status"><UserCheck size={15}/><span>Lista de permitidos ativa: somente os IDs cadastrados aqui poderão entrar no servidor.</span></div>}
    <form className="player-access-form" onSubmit={submitId}><label className="field-label">ID de plataforma<input className="field" value={userId} onChange={event=>setUserId(event.target.value)} placeholder="Ex.: steam_7656119…" autoComplete="off"/></label><label className="field-label">Ação<select className="field" value={manualAction} onChange={event=>setManualAction(event.target.value)}><option value="admin">Promover a admin</option><option value="allow">Adicionar à lista de permitidos</option><option value="ban">Adicionar à lista de banimento</option></select></label><Button variant="primary" disabled={!userId.trim()||!!actionKey}>{actionKey?'Salvando…':manualAction==='admin'?<><UserCheck size={15}/> Promover</>:manualAction==='allow'?<><UserCheck size={15}/> Permitir</>:<><Ban size={15}/> Banir</>}</Button></form>
    <section className="player-roster"><div className="player-roster-head"><div><h3>Jogadores conhecidos</h3><p>Jogadores reconhecidos pelos logs de todas as sessões, ativos primeiro.</p></div><span className={`state-badge ${activeCount>0?'running':'stopped'}`}>{activeCount==null?'SEM DADOS':`${activeCount} ATIVO${activeCount===1?'':'S'}`}</span></div>
      {error&&!players.length?<div className="inline-error"><CircleAlert size={16}/>{error}</div>:loading?<Spinner label="Lendo sessões e permissões…"/>:players.length?<div className="player-roster-list">{players.map((player,index)=>{
        const id=player.userId||'';const lastSeen=player.lastSeenAt?new Date(player.lastSeenAt):null;const lastSeenLabel=lastSeen&&!Number.isNaN(lastSeen.getTime())?new Intl.DateTimeFormat(undefined,{dateStyle:'short',timeStyle:'short'}).format(lastSeen):'';const busy=actionKey.endsWith(`:${id}`);const busyAction=busy?actionKey.slice(0,actionKey.indexOf(':')):'';const session=Number(player.sessionSeconds)||0,total=Number(player.totalSeconds)||0;
        return <article className={`player-roster-row ${player.isActive?'active':''} ${rowAnimation?.key===id?`player-action-${rowAnimation.action}`:''}`} key={`${id||'unknown'}:${player.name.toLowerCase()}:${index}`}>
          <span className="player-roster-avatar"><UserRound size={18}/></span><div className="player-roster-identity"><div><b>{player.name}</b><span className={player.isActive?'player-online-badge':'player-offline-badge'}><i/>{player.isActive?'Online':'Offline'}</span></div><code>{id||'ID não identificado nos registros'}</code>{lastSeenLabel&&<small className="player-last-seen">Último acesso · {lastSeenLabel}</small>}</div>
          <div className="player-roster-time"><span><Clock3 size={13}/><small>Sessão</small><b>{player.isActive?humanDuration(session):'—'}</b></span><span><Clock3 size={13}/><small>Total DSS</small><b>{humanDuration(total)}</b></span></div>
          <div className="player-roster-roles">{player.isAdmin&&<span className="player-role admin"><ShieldCheck size={12}/> Admin</span>}{player.isBanned&&<span className="player-role banned"><Ban size={12}/> Banido</span>}{player.isPermitted&&<span className="player-role permitted">Permitido</span>}</div>
          <div className="player-roster-actions"><Button size="small" disabled={!id||busy} onClick={()=>changeAccess(player.isAdmin?'unadmin':'admin',id,player.name)} title={!id?'Adicione o ID manualmente para gerenciar':player.isAdmin?'Remover admin':'Promover a admin'}>{busyAction==='admin'||busyAction==='unadmin'?<LoaderCircle size={13} className="spin"/>:player.isAdmin?'Remover admin':<><UserCheck size={13}/> Admin</>}</Button><Button size="small" variant={player.isPermitted?'':'primary'} className="player-permit-button" disabled={!id||busy} onClick={()=>changeAccess(player.isPermitted?'unallow':'allow',id,player.name)} title={!id?'Adicione o ID manualmente para gerenciar':player.isPermitted?'Remover da lista de permitidos':'Permitir entrada no servidor'}>{busyAction==='allow'||busyAction==='unallow'?<LoaderCircle size={13} className="spin"/>:player.isPermitted?'Retirar':<><UserCheck size={13}/> Permitir</>}</Button><Button size="small" variant={player.isBanned?'':'danger'} disabled={!id||busy} onClick={()=>changeAccess(player.isBanned?'unban':'ban',id,player.name)} title={!id?'Adicione o ID manualmente para gerenciar':player.isBanned?'Remover banimento':'Banir todas as personagens deste ID de plataforma'}>{busyAction==='ban'||busyAction==='unban'?<LoaderCircle size={13} className="spin"/>:player.isBanned?'Desbanir':<><Ban size={13}/> Banir</>}</Button>{player.isActive&&<Button size="small" variant="danger" className="player-kick-button" disabled={!id||busy} onClick={()=>changeAccess('kick',id,player.name)} title={!id?'ID não identificado para kick':`Kickar a personagem ${player.name} agora`}>{busyAction==='kick'?<LoaderCircle size={13} className="spin"/>:<><UserMinus size={13}/> Kick</>}</Button>}</div>
        </article>;
      })}</div>:<>{activeCount==null&&snapshot?.serverRunning&&<div className="player-roster-empty-hint"><CircleAlert size={16}/><span>A contagem ao vivo não respondeu e ainda não há conexões registradas nos logs recentes. Você ainda pode administrar uma conta pelo ID de plataforma acima.</span></div>}<Empty title="Nenhum jogador registrado" description="Os nomes e IDs aparecem aqui depois que o servidor registrar conexões. Você também pode gerenciar um ID manualmente acima."/></>}
    </section>
    <section className="player-access-lists"><AccessList title="Administradores" icon={<ShieldCheck size={15}/>} values={snapshot?.admins||[]} empty="Nenhum admin configurado"/><AccessList title="Banidos" icon={<Ban size={15}/>} values={snapshot?.banned||[]} empty="Lista de banimento vazia"/><AccessList title="Permitidos" icon={<UserCheck size={15}/>} values={snapshot?.permitted||[]} empty="Lista permitida vazia"/></section>
    <div className="player-management-footnote">Os jogadores e o tempo por personagem ficam salvos em disco nos dados do perfil e permanecem após reinícios e atualizações. O total inclui os registros disponíveis nos logs do servidor.</div>
  </Modal>;
}

function AccessList({title,icon,values,empty}) { return <div className="player-access-list"><h4>{icon}{title}<span>{values.length}</span></h4>{values.length?<div>{values.map(id=><code key={id}>{id}</code>)}</div>:<small>{empty}</small>}</div>; }

function InstalledMods(props) {
  const [updateAccess,setUpdateAccess]=useState({profileId:null,allowed:false});
  useEffect(()=>{
    let active=true;
    setUpdateAccess({profileId:props.profile.id,allowed:Boolean(props.canEditProfile)});
    if(props.canEditProfile)return()=>{active=false;};
    api(`/api/instances/${props.profile.id}/my-access`,{suppressFailureToast:true})
      .then(result=>{if(active)setUpdateAccess({profileId:props.profile.id,allowed:result.permission==='operate'});})
      .catch(()=>{if(active)setUpdateAccess({profileId:props.profile.id,allowed:false});});
    return()=>{active=false;};
  },[props.profile.id,props.canEditProfile]);
  const canCheckUpdates=props.canEditProfile||updateAccess.profileId===props.profile.id&&updateAccess.allowed;
  return <InstalledModsErrorBoundary profileId={props.profile.id}><InstalledModsContent {...props} canCheckUpdates={canCheckUpdates}/></InstalledModsErrorBoundary>;
}

function InstalledModsContent({ profile, canEditProfile = true, canCheckUpdates = canEditProfile, groupRunning = false, initialUpdates = {}, onUpdates }) {
  const [inventory,setInventory]=useState(null); const [loading,setLoading]=useState(true); const [checkingUpdates,setCheckingUpdates]=useState(false); const [refreshingInventory,setRefreshingInventory]=useState(false); const [filter,setFilter]=useState(''); const [searchFields,setSearchFields]=useState(['title','creator','packageId','version']); const [statusFilter,setStatusFilter]=useState('all'); const [providerFilter,setProviderFilter]=useState('all'); const [sortBy,setSortBy]=useState('installedAt'); const [sortDirection,setSortDirection]=useState('desc'); const [inactiveOrder,setInactiveOrder]=useState('normal'); const [modsPage,setModsPage]=useState(1); const [modsPageSize,setModsPageSize]=useState(24); const [selected,setSelected]=useState([]); const [updates,setUpdates]=useState(initialUpdates); const [layout,setLayout]=useState('list'); const [managedMod,setManagedMod]=useState(null); const [r2modmanImportOpen,setR2modmanImportOpen]=useState(false); const [disintegratingMods,setDisintegratingMods]=useState([]); const [updatingMods,setUpdatingMods]=useState([]); const [meteorEffects,setMeteorEffects]=useState([]); const [meteorTargets,setMeteorTargets]=useState({}); const meteorTimers=useRef(new Map()); const inventoryRef=useRef(null),selectAllRef=useRef(null),checkingUpdatesRef=useRef(false); const toast=useToast(); const confirm=useConfirm(); const navigate=useNavigate();
  const [batchingToggle,setBatchingToggle]=useState(false);
  useEffect(()=>{setUpdates(initialUpdates||{});},[initialUpdates]);
  useEffect(()=>()=>{meteorTimers.current.forEach(timer=>window.clearTimeout(timer));meteorTimers.current.clear();},[]);
  function launchModMeteor(modId, version) {
    const card=[...document.querySelectorAll('[data-installed-mod]')].find(element=>element.dataset.installedMod===modId);
    const versionLine=card?.querySelector('.installed-mod-version-value');
    const cardRect=card?.getBoundingClientRect(),versionRect=versionLine?.getBoundingClientRect();
    const target=cardRect&&versionRect?{
      x:Math.max(8,Math.min(92,((versionRect.left+versionRect.width/2-cardRect.left)/cardRect.width)*100)),
      y:Math.max(8,Math.min(92,((versionRect.top+versionRect.height/2-cardRect.top)/cardRect.height)*100)),
      version
    }:{x:50,y:38,version};
    setMeteorTargets(current=>({...current,[modId]:target}));
    setMeteorEffects(current=>current.includes(modId)?current:[...current,modId]);
    const previous=meteorTimers.current.get(modId);
    if(previous)window.clearTimeout(previous);
    const timer=window.setTimeout(()=>{setMeteorEffects(current=>current.filter(id=>id!==modId));meteorTimers.current.delete(modId);},1900);
    meteorTimers.current.set(modId,timer);
  }
  const checkUpdates=useCallback(async(notify=false)=>{if(checkingUpdatesRef.current)return;checkingUpdatesRef.current=true;setCheckingUpdates(true);try{const r=await api(`/api/instances/${profile.id}/mods/check-updates`,{method:'POST',body:'{}'});const result=Object.fromEntries((r.updates||[]).map(x=>[x.modId,x]));setUpdates(result);onUpdates?.(result);if(notify)toast(r.message);}catch(e){if(notify)toast(e.message,'error');}finally{checkingUpdatesRef.current=false;setCheckingUpdates(false);}},[profile.id,toast,onUpdates]);
  const refresh=useCallback(async(checkVersions=true)=>{if(!inventoryRef.current)setLoading(true);try{const result=await api(`/api/instances/${profile.id}/mods`);inventoryRef.current=result;setInventory(result);if(checkVersions)void checkUpdates(false);}catch(e){toast(e.message,'error');}finally{setLoading(false);}},[profile.id,toast,checkUpdates]);
  useEffect(()=>{void refresh(false);},[refresh]);
  useEffect(()=>{if(canCheckUpdates&&inventory?.packages?.length)void checkUpdates(false);},[canCheckUpdates,inventory,checkUpdates]);
  useEffect(()=>{if(selectAllRef.current){const visibleIds=new Set(mods.map(mod=>mod.id));const visibleSelected=selected.filter(id=>visibleIds.has(id)).length;selectAllRef.current.indeterminate=visibleSelected>0&&visibleSelected<visibleIds.size;}},[selected,inventory,filter,searchFields,statusFilter,providerFilter]);
  async function enabled(mod,on){if(mod.locked)return;try{let deps=[];if(!on){const r=await api(`/api/instances/${profile.id}/mods/${mod.id}/dependents`);const activeIds=new Set((inventory?.packages||[]).filter(item=>item.enabled).map(item=>item.id));deps=(r.dependents||[]).filter(dependent=>activeIds.has(dependent.id));if(deps.length&&!await confirm({title:'Desativar dependentes também?',description:`${mod.name} é necessário para os pacotes ativos listados.`,content:<div className="dependency-list">{deps.map(d=><div key={d.id} style={{paddingLeft:`${Math.min(d.depth,5)*14}px`}}>↳ {d.name} <small>{d.version}</small></div>)}</div>,confirmLabel:'Desativar em cascata',danger:true}))return;}await api(`/api/instances/${profile.id}/mods/${mod.id}/enabled`,{method:'POST',body:json({enabled:on,cascadeDependents:!on&&deps.length>0})});refresh();}catch(e){toast(e.message,'error');}}
  async function lockMod(mod,locked){try{await api(`/api/instances/${profile.id}/mods/${mod.id}/locked`,{method:'POST',body:json({locked})});if(locked)setSelected(current=>current.filter(id=>id!==mod.id));await refresh(false);}catch(e){toast(e.message,'error');}}
  async function updateMod(mod){if(mod.locked)return toast(`${mod.name} está bloqueado. Desbloqueie-o antes de atualizar a versão.`,'error');launchModMeteor(mod.id,updates[mod.id]?.latestVersion);const selector=`[data-installed-mod="${mod.id}"]`,before=document.querySelector(selector)?.getBoundingClientRect().top,key=`update-mod:${profile.id}:${mod.id}`;setUpdatingMods(current=>current.includes(mod.id)?current:[...current,mod.id]);toast(`Atualizando ${mod.name}…`,'progress',{key,title:'Atualização de mod',detail:`Versão instalada: ${mod.version}. O DSS manterá a lista e sua posição na página.`});try{const result=await api(`/api/instances/${profile.id}/mods/${mod.id}/update`,{method:'POST',body:'{}',suppressFailureToast:true});await refresh(false);await checkUpdates(false);await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));const after=document.querySelector(selector)?.getBoundingClientRect().top;if(before!==undefined&&after!==undefined&&Math.abs(after-before)>1)window.scrollBy(0,after-before);toast(result.message,'success',{key,title:'Mod atualizado',detail:`A lista do perfil permaneceu aberta.`,durationMs:10000});}catch(e){toast(e.message,'error',{key,title:'Falha ao atualizar mod',durationMs:12000});}finally{setUpdatingMods(current=>current.filter(id=>id!==mod.id));}}
  async function uninstall(mod){let markedForRemoval=false;try{const r=await api(`/api/instances/${profile.id}/mods/${mod.id}/dependents`);if(r.dependents?.length){await confirm({title:'Remoção bloqueada',description:`${mod.name} possui mods dependentes. Selecione todos eles para removê-los em lote.`,content:<div className="dependency-list">{r.dependents.map(d=><div key={d.id}>{d.name} · v{d.version}{d.locked?' · bloqueado':''}</div>)}</div>,confirmLabel:'Entendi',confirmDisabled:true});return;}if(mod.locked)return toast(`${mod.name} está bloqueado. Desbloqueie-o antes de desinstalar.`,'error');if(!await confirm({title:'Desinstalar mod',description:`Remover ${mod.name} e os arquivos gerenciados por ele deste perfil?`,confirmLabel:'Desinstalar',danger:true}))return;setDisintegratingMods(current=>current.includes(mod.id)?current:[...current,mod.id]);markedForRemoval=true;await new Promise(resolve=>setTimeout(resolve,window.matchMedia('(prefers-reduced-motion: reduce)').matches?0:900));const result=await api(`/api/instances/${profile.id}/mods/${mod.id}`,{method:'DELETE'});const visibleInventory=inventoryRef.current;if(visibleInventory)inventoryRef.current={...visibleInventory,packages:(visibleInventory.packages||[]).filter(item=>item.id!==mod.id)};setInventory(current=>current?{...current,packages:(current.packages||[]).filter(item=>item.id!==mod.id)}:current);setSelected(current=>current.filter(id=>id!==mod.id));const source=installedProviderInfo(mod).provider;const fileSummary=`${(result.removedFiles||[]).length} arquivo(s) removido(s) · ${(result.preservedFiles||[]).length} arquivo(s) alterado(s) preservado(s).`;toast(`1 mod removido: ${mod.name}.`,'success',{title:'Desinstalação concluída',detail:fileSummary,modDetails:[{provider:source,packageId:mod.packageId,name:mod.name}],durationMs:10000});try{const latest=await api(`/api/instances/${profile.id}/mods`,{cache:'no-store',suppressFailureToast:true});const nextInventory={...latest,packages:(latest.packages||[]).filter(item=>item.id!==mod.id)};inventoryRef.current=nextInventory;setInventory(nextInventory);}catch{}}catch(e){toast(e.message,'error');}finally{if(markedForRemoval)setDisintegratingMods(current=>current.filter(id=>id!==mod.id));}}
  const fieldsMeta=[['title','Nome'],['creator','Autor'],['description','Descrição'],['packageId','ID do pacote'],['version','Versão'],['tags','Tags'],['provider','Provedor']];
  const providers=[...new Set((inventory?.packages||[]).map(mod=>installedProviderInfo(mod).provider).filter(Boolean))].sort();
  const mods=(inventory?.packages||[]).filter(mod=>{
    if(statusFilter==='active'&&!mod.enabled||statusFilter==='inactive'&&mod.enabled)return false;
    const source=installedProviderInfo(mod).provider;
    if(providerFilter!=='all'&&source!==providerFilter)return false;
    const needle=filter.trim().toLocaleLowerCase();if(!needle)return true;
    const searchable={title:mod.name,creator:mod.owner,description:mod.description,packageId:mod.packageId,version:mod.version,tags:(mod.categories||[]).join(' '),provider:providerNames[source]||source};
    const activeFields=searchFields.length?searchFields:['title'];
    return activeFields.some(field=>String(searchable[field]||'').toLocaleLowerCase().includes(needle));
  }).sort((left,right)=>{
    const leftHasUpdate=Boolean(updates[left.id]?.hasUpdate),rightHasUpdate=Boolean(updates[right.id]?.hasUpdate);
    if(leftHasUpdate!==rightHasUpdate)return leftHasUpdate?-1:1;
    if(inactiveOrder!=='normal'&&left.enabled!==right.enabled)return inactiveOrder==='first'?(left.enabled?1:-1):(left.enabled?-1:1);
    const access=mod=>({name:mod.name,owner:mod.owner,version:mod.version,packageId:mod.packageId,provider:installedProviderInfo(mod).provider,downloads:Number(mod.totalDownloads)||0,rating:Number(mod.ratingScore)||0,size:Number(mod.fileSize)||0,created:Date.parse(mod.createdAt)||0,updated:Date.parse(mod.updatedAt)||0,installedAt:Date.parse(mod.installedAt)||0,files:mod.managedFiles?.length||0,tags:(mod.categories||[]).join(' ')}[sortBy]);
    const a=access(left),b=access(right),comparison=typeof a==='string'?a.localeCompare(String(b||''),undefined,{numeric:true,sensitivity:'base'}):a-b;
    return sortDirection==='desc'?-comparison:comparison;
  });
  const modsPageCount=Math.max(1,Math.ceil(mods.length/modsPageSize));
  const currentModsPage=Math.min(modsPage,modsPageCount);
  const pageMods=mods.slice((currentModsPage-1)*modsPageSize,currentModsPage*modsPageSize);
  useEffect(()=>{setModsPage(1);},[filter,searchFields,statusFilter,providerFilter,sortBy,sortDirection,inactiveOrder,modsPageSize,updates]);
  useEffect(()=>{if(modsPage!==currentModsPage)setModsPage(currentModsPage);},[modsPage,currentModsPage]);
  const selectableMods=mods;
  const allVisibleSelected=selectableMods.length>0&&selectableMods.every(mod=>selected.includes(mod.id));
  const toggleAllVisible=()=>{const visibleIds=selectableMods.map(mod=>mod.id);setSelected(current=>allVisibleSelected?current.filter(id=>!visibleIds.includes(id)):[...new Set([...current,...visibleIds])]);};
  const selectedMods=(inventory?.packages||[]).filter(mod=>selected.includes(mod.id));
  const selectedUnlocked=selectedMods.filter(mod=>!mod.locked);
  const selectedLocked=selectedMods.filter(mod=>mod.locked);
  const pendingUpdateMods=(inventory?.packages||[]).filter(mod=>updates[mod.id]?.hasUpdate);
  const pendingUnlockedUpdates=pendingUpdateMods.filter(mod=>!mod.locked);
  async function refreshAndCheck(){setRefreshingInventory(true);try{await refresh(false);await checkUpdates(true);}finally{setRefreshingInventory(false);}}
  async function lockBatch(locked){const chosen=selectedMods.filter(mod=>mod.locked!==locked);if(!chosen.length)return;try{await api(`/api/instances/${profile.id}/mods/locked-batch`,{method:'POST',body:json({modIds:chosen.map(mod=>mod.id),locked})});setSelected(current=>current.filter(id=>!chosen.some(mod=>mod.id===id)));await refresh(false);}catch(error){toast(error.message,'error');}}
  async function updateAllMods(){const chosen=pendingUnlockedUpdates;if(!chosen.length){if(pendingUpdateMods.length)toast('Todos os mods com atualização disponível estão bloqueados. Desbloqueie-os para continuar.','error');return;}if((groupRunning || profile.runtimeStatus === 'running'))return toast('Pare o servidor antes de atualizar mods.','error');const accepted=await confirm({title:`Atualizar ${chosen.length} mods`,description:'As atualizações serão aplicadas em sequência para manter o inventário visível e preservar o posicionamento da página.',content:<div className="dependency-list">{chosen.slice(0,10).map(mod=><div key={mod.id}>{mod.name} · {mod.version} → {updates[mod.id]?.latestVersion}</div>)}{chosen.length>10&&<small>e mais {chosen.length-10} mod(s)</small>}{pendingUpdateMods.length>chosen.length&&<small>{pendingUpdateMods.length-chosen.length} mod(s) bloqueados foram ignorados.</small>}</div>,confirmLabel:`Atualizar ${chosen.length} mods`});if(!accepted)return;const key=`update-all:${profile.id}`,ids=chosen.map(mod=>mod.id),anchor=document.querySelector('.installed-mod-card')?.getBoundingClientRect().top;setUpdatingMods(ids);let succeeded=0;const failed=[];toast(`Atualizando 0 de ${chosen.length} mods.`,'progress',{key,title:'Atualização em lote',detail:'O layout e a posição da lista serão mantidos.'});try{for(let index=0;index<chosen.length;index++){const mod=chosen[index];launchModMeteor(mod.id,updates[mod.id]?.latestVersion);toast(`Atualizando ${mod.name} · ${index+1} de ${chosen.length}.`,'progress',{key,title:'Atualização em lote',detail:`Versão nova: ${updates[mod.id]?.latestVersion||'disponível'}`,progress:Math.round(index/chosen.length*100),progressText:`${index} de ${chosen.length}`});try{await api(`/api/instances/${profile.id}/mods/${mod.id}/update`,{method:'POST',body:'{}',suppressFailureToast:true});succeeded++;}catch(error){failed.push(`${mod.name}: ${error.message}`);}}await refresh(false);await checkUpdates(false);await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));const currentTop=document.querySelector('.installed-mod-card')?.getBoundingClientRect().top;if(anchor!==undefined&&currentTop!==undefined&&Math.abs(currentTop-anchor)>1)window.scrollBy(0,currentTop-anchor);toast(`${succeeded} de ${chosen.length} mods atualizados${failed.length?`; ${failed.length} falharam`:'.'}`,failed.length?'error':'success',{key,title:failed.length?'Atualizações concluídas parcialmente':'Atualizações concluídas',detail:failed.slice(0,3).join(' · '),durationMs:10000,progress:100,progressText:`${succeeded} de ${chosen.length}`});}catch(error){toast(error.message,'error',{key,title:'Falha ao atualizar mods',durationMs:12000});}finally{setUpdatingMods([]);}}
  async function batch(on){
    if(batchingToggle)return;
    const chosen=(inventory?.packages||[]).filter(mod=>selected.includes(mod.id)&&!mod.locked&&Boolean(mod.enabled)!==on);
    if(!chosen.length){setSelected([]);return;}
    const selectedIds=new Set(chosen.map(mod=>mod.id));
    if(!on){
      try{
        const activeIds=new Set((inventory?.packages||[]).filter(item=>item.enabled).map(item=>item.id));
        const dependents=await Promise.all(chosen.map(async mod=>{
          const result=await api(`/api/instances/${profile.id}/mods/${mod.id}/dependents`,{suppressFailureToast:true});
          return (result.dependents||[]).filter(item=>activeIds.has(item.id)&&!selectedIds.has(item.id));
        }));
        const cascadeMods=[...new Map(dependents.flat().map(item=>[item.id,item])).values()];
        if(cascadeMods.length&&!await confirm({title:'Desativar dependentes também?',description:`Os ${chosen.length} mods selecionados serão desativados junto com os dependentes ativos abaixo.`,content:<div className="dependency-list batch-toggle-dependents"><b>{cascadeMods.length} mod(s) ativo(s) serão desativados em cascata.</b>{cascadeMods.sort((a,b)=>a.name.localeCompare(b.name)).map(item=><div key={item.id}>{item.name}</div>)}</div>,confirmLabel:'Desativar em cascata',danger:true}))return;
      }catch(error){toast(error.message,'error');return;}
    }
    setBatchingToggle(true);
    try{
      const result=await api(`/api/instances/${profile.id}/mods/enabled-batch`,{method:'POST',body:json({modIds:chosen.map(mod=>mod.id),enabled:on}),suppressFailureToast:true});
      setSelected([]);
      await refresh(false);
      if(result.failed?.length)toast(result.message,'error',{title:'Alteração em lote parcial',detail:result.failed.slice(0,3).map(item=>`${item.name}: ${item.error}`).join(' · '),durationMs:12000});
      else toast(result.message,'success');
    }catch(error){toast(error.message,'error');}
    finally{setBatchingToggle(false);}
  }
  async function installFramework(){const key=`install-framework:${profile.id}`;toast('Preparando o BepInEx e verificando o pacote.','progress',{key,title:'Instalação do BepInEx'});try{const result=await installPackagesWithProgress({profileId:profile.id,provider:'bepinex',packageIds:['denikson-BepInExPack_Valheim'],dependencyVersions:{},toast,key});toast('1 mod instalado: BepInExPack Valheim.', 'success',{key,title:'Instalação concluída',detail:`${Math.max(0,(result.installed?.length||1)-1)} dependência(s) incluída(s).`,modDetails:[{provider:'bepinex',packageId:'denikson-BepInExPack_Valheim',name:'BepInExPack Valheim'}],durationMs:10000});await refresh();}catch(error){toast(error.message,'error',{key,title:'Falha ao instalar BepInEx',durationMs:12000});}}
  async function uninstallBatch(){
    const chosen=(inventory?.packages||[]).filter(mod=>selected.includes(mod.id));
    if(!chosen.length)return;
    if((groupRunning || profile.runtimeStatus === 'running'))return toast('Pare o servidor antes de remover mods.','error');
    const ids=chosen.map(mod=>mod.id),selectedIds=new Set(ids),installedById=new Map((inventory?.packages||[]).map(mod=>[mod.id,mod]));
    let dependencyChecks;
    try{
      dependencyChecks=await Promise.all(chosen.map(async mod=>{
        const result=await api(`/api/instances/${profile.id}/mods/${mod.id}/dependents`,{suppressFailureToast:true});
        return [mod.id,result.dependents||[]];
      }));
    }catch(error){
      await confirm({title:'Não foi possível verificar os dependentes',description:'O DSS não conseguiu confirmar a relação entre os mods. A remoção foi bloqueada para proteger o perfil.',content:<div className="dependency-list"><b>{error.message}</b><small>Tente novamente quando o catálogo e o perfil estiverem disponíveis.</small></div>,confirmLabel:'Remoção bloqueada',cancelLabel:'Fechar',confirmDisabled:true,danger:true});
      return;
    }
    const dependents=[...new Map(dependencyChecks.flatMap(([,items])=>items).map(item=>[item.id,item])).values()];
    const externalDependents=dependents.filter(item=>!selectedIds.has(item.id));
    const lockedDependents=dependents.filter(item=>selectedIds.has(item.id)&&installedById.get(item.id)?.locked);
    const lockedSelected=chosen.filter(mod=>mod.locked);
    if(externalDependents.length||lockedDependents.length||lockedSelected.length){
      await confirm({title:'Remoção em lote bloqueada',description:'Resolva os itens abaixo antes de remover qualquer mod.',content:<div className="dependency-list batch-uninstall-blockers">{lockedSelected.length>0&&<section><b>Mods selecionados bloqueados</b>{lockedSelected.map(mod=><div key={mod.id}>{mod.name} · desbloqueie antes de remover</div>)}</section>}{externalDependents.length>0&&<section><b>Dependentes fora da seleção</b>{externalDependents.map(mod=><div key={mod.id}>{mod.name} · v{mod.version}</div>)}</section>}{lockedDependents.length>0&&<section><b>Dependentes selecionados bloqueados</b>{lockedDependents.map(mod=><div key={mod.id}>{mod.name} · desbloqueie antes de remover</div>)}</section>}</div>,confirmLabel:'Remoção bloqueada',cancelLabel:'Fechar',confirmDisabled:true,danger:true});
      return;
    }
    const accepted=await confirm({title:`Desinstalar ${chosen.length} mods em lote`,description:'Dependências verificadas: todos os dependentes estão incluídos na seleção e desbloqueados.',content:<div className="dependency-list batch-uninstall-review"><b>{dependents.length?`${dependents.length} dependente(s) também serão removidos em lote.`:'Nenhum dependente foi encontrado.'}</b>{chosen.slice(0,8).map(mod=><div key={mod.id}>{mod.name} · v{mod.version}</div>)}{chosen.length>8&&<small>e mais {chosen.length-8} mod(s)</small>}<small>Os arquivos serão verificados antes da remoção em paralelo.</small></div>,confirmLabel:`Desinstalar ${chosen.length} mods`,danger:true});
    if(!accepted)return;
    const key=`uninstall-batch:${profile.id}`;
    setDisintegratingMods(current=>[...new Set([...current,...ids])]);
    toast(`Removendo ${chosen.length} mods selecionados em paralelo.`,'progress',{key,title:'Desinstalação em lote',detail:'O DSS está verificando dependências e arquivos gerenciados.'});
    try{
      const animation=new Promise(resolve=>setTimeout(resolve,window.matchMedia('(prefers-reduced-motion: reduce)').matches?0:900));
      const request=(async()=>{
        try{
          return await api(`/api/instances/${profile.id}/mods/uninstall-batch`,{method:'POST',body:json({modIds:ids}),suppressFailureToast:true});
        }catch(error){
          if(error.status!==405)throw error;
          const selectedIds=new Set(ids), byId=new Map(chosen.map(mod=>[mod.id,mod]));
          const dependencyChecks=await Promise.all(chosen.map(async mod=>{
            const result=await api(`/api/instances/${profile.id}/mods/${mod.id}/dependents`,{suppressFailureToast:true});
            return [mod.id,result.dependents||[]];
          }));
          const dependentsById=new Map(dependencyChecks);
          const outside=[...new Map(dependencyChecks.flatMap(([,dependents])=>dependents).filter(dependent=>!selectedIds.has(dependent.id)).map(dependent=>[dependent.id,dependent])).values()];
          if(outside.length){
            const names=outside.slice(0,5).map(dependent=>dependent.name).join(', ');
            throw new Error(`A remoção foi bloqueada: mods não selecionados dependem dos pacotes escolhidos (${names}${outside.length>5?'…':''}).`);
          }
          if(chosen.some(mod=>mod.locked))throw new Error(`A remoção em lote foi bloqueada: desbloqueie ${chosen.filter(mod=>mod.locked).map(mod=>mod.name).join(', ')} antes de remover qualquer pacote.`);
          const pending=new Set(ids), removedModIds=[], failed=[], failedIds=new Set();
          while(pending.size){
            const ready=[...pending].filter(modId=>!failedIds.has(modId)&&!(dependentsById.get(modId)||[]).some(dependent=>pending.has(dependent.id)));
            if(!ready.length){
              for(const modId of pending){if(failedIds.has(modId))continue;const mod=byId.get(modId);failed.push({name:mod?.name||'Mod',error:'Dependências pendentes impedem a remoção.'});}
              break;
            }
            const outcomes=await Promise.allSettled(ready.map(modId=>api(`/api/instances/${profile.id}/mods/${modId}`,{method:'DELETE',suppressFailureToast:true})));
            outcomes.forEach((outcome,index)=>{
              const modId=ready[index],mod=byId.get(modId);
              if(outcome.status==='fulfilled'){removedModIds.push(modId);pending.delete(modId);}
              else {failedIds.add(modId);failed.push({name:mod?.name||'Mod',error:outcome.reason?.message||'Falha ao remover o mod.'});}
            });
          }
          const message=failed.length
            ? `${removedModIds.length} de ${chosen.length} mods removidos. ${failed.length} mod(s) não puderam ser removidos.`
            : `${removedModIds.length} mods removidos em lote.`;
          return {removedModIds,failed,message};
        }
      })();
      const [,result]=await Promise.all([animation,request]);
      const removedIds=result.removedModIds||[];
      setSelected(current=>current.filter(id=>!removedIds.includes(id)));
      const removedMods=chosen.filter(mod=>removedIds.includes(mod.id)),removedCount=removedMods.length;
      const removedRefs=removedMods.map(mod=>({provider:installedProviderInfo(mod).provider,packageId:mod.packageId,name:mod.name}));
      const summary=result.failed?.length?`${removedCount} de ${chosen.length} mods removidos em lote; ${result.failed.length} ${result.failed.length===1?'falhou':'falharam'}.`:`${removedCount} mod${removedCount===1?'':'s'} removido${removedCount===1?'':'s'}${removedCount>1?' em lote':''}.`;
       toast(summary,result.failed?.length?'error':'success',{key,title:result.failed?.length?'Lote parcialmente concluído':removedCount>1?'Desinstalação em lote concluída':'Desinstalação concluída',detail:result.failed?.length?`${result.failed.length} ${result.failed.length===1?'mod precisa':'mods precisam'} de atenção.`:result.message||`${removedCount} ${removedCount===1?'mod removido':'mods removidos'} da seleção.`,modDetails:removedRefs,durationMs:result.failed?.length?12000:10000});
      await refresh();
    }catch(error){if(error.status===409||/dependentes|dependem|bloquead[ao]/i.test(error.message)){await confirm({title:'Remoção em lote bloqueada',description:'A verificação final encontrou uma dependência ou bloqueio que impede a remoção.',content:<div className="dependency-list"><b>{error.message}</b></div>,confirmLabel:'Remoção bloqueada',cancelLabel:'Fechar',confirmDisabled:true,danger:true});}else toast(error.message,'error',{key,title:'Desinstalação em lote interrompida',durationMs:12000});}
    finally{setDisintegratingMods(current=>current.filter(id=>!ids.includes(id)));}
  }
  if(loading)return <Card><Spinner label="Lendo mods e arquivos gerenciados…"/></Card>;
  return <Card className={`installed-page ${canEditProfile?'':'profile-view-controls'}`}><div className="panel-head"><div><h2>Mods e componentes instalados</h2><p>Pacotes gerenciados pelo DSS neste perfil.</p></div><div className="installed-header-tools">{canCheckUpdates&&!canEditProfile&&<Button onClick={()=>checkUpdates(true)} disabled={checkingUpdates}><RefreshCw size={15} className={checkingUpdates?'spin':''}/>{checkingUpdates?'Verificando versões…':'Verificar versões'}</Button>}{canEditProfile&&<><div className="installed-inventory-actions"><Button onClick={()=>setR2modmanImportOpen(true)} disabled={groupRunning||profile.runtimeStatus==='running'} title={groupRunning||profile.runtimeStatus==='running'?'Pare todos os servidores para importar':'Importar mods e configurações de um perfil r2modman'}><Upload size={15}/> Importar r2modman</Button><Button onClick={refreshAndCheck} disabled={checkingUpdates||refreshingInventory}><RefreshCw size={15} className={checkingUpdates||refreshingInventory?'spin':''}/>{checkingUpdates?'Verificando versões…':refreshingInventory?'Verificando inventário…':'Verificar inventário'}</Button>{pendingUpdateMods.length>0&&<Button variant="primary" className="update-all-button" onClick={updateAllMods} disabled={(groupRunning || profile.runtimeStatus === 'running')||!pendingUnlockedUpdates.length||updatingMods.length>0}><Download size={15}/>{updatingMods.length?'Atualizando mods…':`Atualizar todos (${pendingUpdateMods.length})`}</Button>}</div>{selected.length>0&&<div className="installed-batch-actions"><span className="selection-count">{selected.length} selecionado(s)</span><Button onClick={()=>batch(true)} disabled={batchingToggle||!selectedUnlocked.some(mod=>!mod.enabled)||(groupRunning || profile.runtimeStatus === 'running')}>Ativar</Button><Button onClick={()=>batch(false)} disabled={batchingToggle||!selectedUnlocked.some(mod=>mod.enabled)||(groupRunning || profile.runtimeStatus === 'running')}>Desativar</Button><Button variant="danger" className="batch-uninstall-button" onClick={uninstallBatch} disabled={!selected.length||(groupRunning || profile.runtimeStatus === 'running')}><Trash2 size={14}/> Desinstalar</Button>{selectedUnlocked.length>0&&<Button onClick={()=>lockBatch(true)} title="Bloquear ativação, desativação, remoção e mudança de versão"><Lock size={14}/> Bloquear</Button>}{selectedLocked.length>0&&<Button onClick={()=>lockBatch(false)} title="Remover o bloqueio dos mods selecionados"><Unlock size={14}/> Desbloquear</Button>}</div>}</>}</div></div>{!inventory?.loaderInstalled&&<div className="loader-missing"><span className="provider-mark">B</span><div><b>BepInEx não detectado</b><p>Instale o carregador antes de usar mods.</p></div>{canEditProfile&&<Button variant="primary" disabled={(groupRunning || profile.runtimeStatus === 'running')} onClick={installFramework}>Instalar BepInEx</Button>}</div>}<div className="installed-filter-panel"><div className="installed-filter-top"><label className="search-input"><Search size={17}/><input className="field" value={filter} onChange={e=>setFilter(e.target.value)} placeholder="Filtrar mods instalados"/><button type="button" aria-label="Limpar busca" onClick={()=>setFilter('')}><X size={15}/></button></label>{canEditProfile&&<label className="select-all-mods"><input ref={selectAllRef} type="checkbox" checked={allVisibleSelected} onChange={toggleAllVisible} disabled={!selectableMods.length}/><span/>{allVisibleSelected?'Desmarcar todos':`Selecionar todos (${selectableMods.length})`}</label>}<div className="segmented"><button className={layout==='cards'?'selected':''} onClick={()=>setLayout('cards')} type="button" aria-label="Exibição em cartões"><Package size={15}/></button><button className={layout==='list'?'selected':''} onClick={()=>setLayout('list')} type="button" aria-label="Exibição em lista"><List size={15}/></button></div></div><div className="installed-search-fields"><span className="eyebrow">BUSCAR EM</span><div className="field-checks">{canEditProfile?fieldsMeta.map(([id,label])=><label key={id} className={`field-check ${searchFields.includes(id)?'checked':''}`}><input type="checkbox" checked={searchFields.includes(id)} onChange={event=>setSearchFields(current=>event.target.checked?[...current,id]:current.filter(value=>value!==id))}/><span>{searchFields.includes(id)?'✓':''}</span>{label}</label>):<span className="read-only-search-fields">Nome · Autor · ID do pacote · Versão</span>}</div></div><div className="installed-filter-controls"><label>STATUS<select className="field" value={statusFilter} onChange={event=>setStatusFilter(event.target.value)}><option value="all">Todos os mods</option><option value="active">Ativos</option><option value="inactive">Desativados</option></select></label><label>PROVEDOR<select className="field" value={providerFilter} onChange={event=>setProviderFilter(event.target.value)}><option value="all">Todos os provedores</option>{providers.map(value=><option key={value} value={value}>{providerNames[value]||value}</option>)}</select></label><label>ORDENAR POR<select className="field" value={sortBy} onChange={event=>setSortBy(event.target.value)}><option value="installedAt">Data de instalação</option><option value="name">Nome</option><option value="owner">Autor</option><option value="provider">Provedor</option><option value="version">Versão</option><option value="downloads">Downloads</option><option value="rating">Likes</option><option value="size">Tamanho</option><option value="created">Primeiro envio</option><option value="updated">Última atualização</option><option value="files">Arquivos gerenciados</option><option value="tags">Tags</option></select></label><label>ORDEM<select className="field" value={sortDirection} onChange={event=>setSortDirection(event.target.value)}><option value="asc">Crescente</option><option value="desc">Decrescente</option></select></label><label>MODS INATIVOS<select className="field" value={inactiveOrder} onChange={event=>setInactiveOrder(event.target.value)}><option value="normal">Ordem normal</option><option value="first">Inativos primeiro</option><option value="last">Inativos por último</option></select></label></div></div>{mods.length?<><div className="installed-page-toolbar"><span>{(currentModsPage-1)*modsPageSize+1}–{Math.min(currentModsPage*modsPageSize,mods.length)} de {mods.length} mods</span><label>Mods por página<select className="field" value={modsPageSize} onChange={event=>setModsPageSize(Number(event.target.value))}><option value={12}>12</option><option value={24}>24</option><option value={48}>48</option><option value={96}>96</option></select></label></div><div className={`installed-grid ${layout}`}>
    {pageMods.map(mod => {
      const { provider: providerSource, projectUrl } = installedProviderInfo(mod); const providerLabel = providerNames[providerSource] || providerSource; const dependencies = mod.dependencies || []; const managedFileCount = mod.managedFiles?.length || 0;
      const updateInfo=updates[mod.id];
      const statusTags = <>{mod.enabled && <b className="tag tag-active">ATIVO</b>}{mod.locked&&<b className="tag tag-locked"><Lock size={10}/> BLOQUEADO</b>}{updateInfo?.hasUpdate&&<b className="tag tag-update"><Download size={11}/> NOVA VERSÃO</b>}</>;
      return <article data-installed-mod={mod.id} className={`installed-mod-card ${mod.enabled ? '' : 'mod-inactive'} ${updateInfo?.hasUpdate?'has-update':''} ${mod.locked?'mod-locked':''} ${updatingMods.includes(mod.id)?'is-updating':''} ${meteorEffects.includes(mod.id)?'meteor-arrival':''} ${selected.includes(mod.id) ? 'selected' : ''} ${disintegratingMods.includes(mod.id) ? 'dss-thanos-card' : ''}`} key={mod.id}>
        {meteorEffects.includes(mod.id)&&<span className="mod-meteor-scene" style={{'--meteor-x':`${meteorTargets[mod.id]?.x??50}%`,'--meteor-y':`${meteorTargets[mod.id]?.y??38}%`}} aria-hidden="true"><i className="mod-meteor-trail"/><i className="mod-meteor-impact"/><i className="mod-meteor-shockwave"/><i className="mod-meteor-spark spark-one"/><i className="mod-meteor-spark spark-two"/><i className="mod-meteor-spark spark-three"/><b className="mod-meteor-label">{meteorTargets[mod.id]?.version?`v${meteorTargets[mod.id].version}`:'NOVA VERSÃO'}</b></span>}
        {canEditProfile&&<label className="select-mod" title="Selecionar mod para ações em lote"><input type="checkbox" checked={selected.includes(mod.id)} onChange={e => setSelected(current => e.target.checked ? [...new Set([...current,mod.id])] : current.filter(id => id !== mod.id))}/><span/></label>}
        {canEditProfile&&<button type="button" className={`mod-lock-toggle mod-lock-control ${mod.locked?'locked':''}`} aria-pressed={Boolean(mod.locked)} aria-label={`${mod.locked?'Desbloquear':'Bloquear'} ${mod.name}`} title={mod.locked?'Desbloquear ativação, remoção e alteração de versão':'Bloquear ativação, remoção e alteração de versão'} onClick={()=>lockMod(mod,!mod.locked)}>{mod.locked?<Lock size={14}/>:<Unlock size={14}/>}</button>}
        {layout === 'cards' && <div className="mod-tags">{statusTags}</div>}
        <div className="mod-image-wrap">{mod.iconUrl ? <img src={mod.iconUrl} alt="" loading="lazy"/> : <span>{mod.name.slice(0, 2).toUpperCase()}</span>}{projectUrl&&<a className="mod-project-hitbox" href={projectUrl} target="_blank" rel="noreferrer" aria-label={`Abrir página de ${mod.name}`}/>}</div>
        <div className="installed-mod-main">
          <div className="installed-mod-title">
              <div className={`installed-mod-name ${mod.isFramework ? 'framework-name' : ''}`}><div className="installed-mod-heading-row"><h3>{projectUrl?<a href={projectUrl} target="_blank" rel="noreferrer">{mod.name}</a>:mod.name}</h3>{canEditProfile&&<button type="button" className="mod-settings-button" onClick={()=>setManagedMod(mod)} title={`Gerenciar versão e configs de ${mod.name}`} aria-label={`Gerenciar versão e arquivos de configuração de ${mod.name}`}><Settings size={15}/></button>}</div><p>por {mod.owner || providerLabel}</p><code className="installed-mod-version-target"><span className="installed-mod-version-value">v{mod.version}</span> · {mod.packageId}</code><div className="installed-rating-row"><small className="installed-upload-dates">Primeiro envio {fmtDate(mod.createdAt)} · Último envio {fmtDate(mod.updatedAt)}</small><small className="installed-rating">👍 {mod.ratingScore?.toLocaleString() ?? '—'}</small></div><div className="installed-date-badge"><span className="installed-date-icon"><Clock3 size={14}/></span><span className="installed-date-copy"><span>Instalado em</span><time dateTime={mod.installedAt||undefined}>{mod.installedAt?fmtDate(mod.installedAt):'data não registrada'}</time></span></div><small className="installed-file-size">Peso do pacote {mod.fileSize != null ? bytes(mod.fileSize) : 'indisponível'}</small></div>
             <div className="installed-mod-meta-side">{projectUrl?<a className={'provider-source ' + providerSource} href={projectUrl} target="_blank" rel="noopener noreferrer" title={`Abrir ${mod.name} no ${providerLabel}`} aria-label={`Abrir página de ${mod.name} no ${providerLabel}`} onClick={event=>event.stopPropagation()}><ProviderIcon provider={providerSource}/></a>:<span className={'provider-source ' + providerSource} title={`Página do mod indisponível · instalado de ${providerLabel}`}><ProviderIcon provider={providerSource}/></span>}{layout === 'list' && <div className="installed-dss-tags">{statusTags}</div>}</div>
          </div>
          <div className="installed-category-tags" title={(mod.categories || []).join(' · ')}>{(mod.categories || []).slice(0, 5).map(category => <span key={category}>{category}</span>)}{(mod.categories?.length || 0) > 5 && <span className="more-tags">+{mod.categories.length - 5}</span>}</div>
          <div className="installed-description"><DependencyPopover dependencies={dependencies} emptyLabel="Sem dependências declaradas"/><span className="description-separator">·</span><span>{managedFileCount} {managedFileCount === 1 ? 'arquivo gerenciado' : 'arquivos gerenciados'}</span></div>
            {canEditProfile&&<div className="mod-actions"><button type="button" role="switch" aria-checked={Boolean(mod.enabled)} aria-label={`${mod.enabled ? 'Desativar' : 'Ativar'} ${mod.name}`} title={mod.locked?'Desbloqueie este mod para alterar seu estado':`${mod.enabled ? 'Desativar' : 'Ativar'} mod`} className={`mod-enabled-switch ${mod.enabled ? 'on' : ''}`} disabled={(groupRunning || profile.runtimeStatus === 'running') || mod.locked} onClick={() => enabled(mod, !mod.enabled)}><span className="mod-switch-track"><span/></span><span>{mod.enabled ? 'Ativo' : 'Inativo'}</span></button><Button variant={updateInfo?.hasUpdate?'primary':undefined} className={updateInfo?.hasUpdate?'mod-update-action':''} disabled={(groupRunning || profile.runtimeStatus === 'running') || !updateInfo?.hasUpdate || updatingMods.includes(mod.id)||mod.locked} title={mod.locked?'Desbloqueie este mod para alterar sua versão':updateInfo?.hasUpdate?`Nova versão disponível: ${updateInfo.latestVersion}`:'Nenhuma atualização disponível'} onClick={()=>updateMod(mod)}>{updatingMods.includes(mod.id)?<><LoaderCircle size={14} className="spin"/> Atualizando…</>:updateInfo?.hasUpdate?<><Download size={14}/> Atualizar · v{updateInfo.latestVersion}</>:'Sem atualização'}</Button><Button variant="danger" className={`mod-uninstall-button ${mod.locked?'locked-uninstall-button':''}`} disabled={(groupRunning || profile.runtimeStatus === 'running') || mod.locked} title={mod.locked?'Desbloqueie este mod para desinstalá-lo':'Desinstalar mod'} onClick={() => uninstall(mod)}>Desinstalar</Button></div>}
        </div>
      </article>;
    })}
  </div><NumericPagination page={currentModsPage} totalPages={modsPageCount} onPage={nextPage=>{setModsPage(nextPage);document.querySelector('.installed-page')?.scrollIntoView({behavior:'smooth',block:'start'});}}/></>:<Empty title={(inventory?.packages||[]).length?'Nenhum mod corresponde aos filtros':'Nenhum mod instalado'} description={(inventory?.packages||[]).length?'Ajuste a busca ou os filtros para ver os mods do perfil.':'Use Buscar mods para consultar os catálogos e instalar pacotes neste perfil.'}/>}{r2modmanImportOpen&&<R2modmanImportModal profile={profile} groupRunning={groupRunning||profile.runtimeStatus==='running'} close={()=>setR2modmanImportOpen(false)} onImported={async()=>{setR2modmanImportOpen(false);setSelected([]);await refresh(false);await checkUpdates(false);}}/>}{managedMod&&<InstalledModSettingsModal profile={profile} mod={managedMod} packages={inventory?.packages||[]} close={()=>setManagedMod(null)} onApplied={async message=>{setManagedMod(null);toast(message);await refresh();}} navigate={navigate}/>}</Card>;
}

function R2modmanImportModal({ profile, groupRunning, close, onImported }) {
  const { language } = useDss();
  const [source,setSource]=useState('file');
  const [file,setFile]=useState(null);
  const [code,setCode]=useState('');
  const [preview,setPreview]=useState(null);
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState('');
  const [replaceConfirmed,setReplaceConfirmed]=useState(false);
  const [progress,setProgress]=useState(null);
  const [selectedMods,setSelectedMods]=useState([]);
  const [selectedConfigs,setSelectedConfigs]=useState([]);
  const fileInput=useRef(null);
  const toast=useToast();
  const localizedError = error ? translateText(error, language) : '';
  const canSubmit=source==='file'?Boolean(file):Boolean(code.trim());
  const closeIfIdle=()=>{if(!busy)close();};

  const selectedOverwriteCount=preview?.overwrites?.filter(path=>selectedConfigs.includes(path)).length||0;
  const selectedItemCount=selectedMods.length+selectedConfigs.length;
  function buildFormData(options={}){
    const form=new FormData();
    if(source==='file'&&file)form.append('file',file);
    if(source==='code')form.append('code',code.trim());
    if(options.confirmOverwrite!==undefined)form.append('confirmOverwrite',String(options.confirmOverwrite));
    if(options.selectionProvided)form.append('selectionProvided','true');
    options.selectedMods?.forEach(id=>form.append('selectedMods',id));
    options.selectedConfigs?.forEach(path=>form.append('selectedConfigs',path));
    return form;
  }
  function changeSource(next){setSource(next);setPreview(null);setError('');setReplaceConfirmed(false);setSelectedMods([]);setSelectedConfigs([]);}
  async function readPreview(){
    if(!canSubmit)return;
    setBusy(true);setError('');setPreview(null);
    try{const result=await api(`/api/instances/${profile.id}/mods/r2modman/preview`,{method:'POST',body:buildFormData()});setPreview(result);setSelectedMods((result.mods||[]).map(mod=>mod.packageId));setSelectedConfigs((result.configs||[]).map(config=>config.relativePath));setReplaceConfirmed(false);}
    catch(reason){setError(reason.message||'Não foi possível ler este perfil.');}
    finally{setBusy(false);}
  }
  async function importProfile(){
    if(!preview||groupRunning||selectedItemCount===0||(selectedOverwriteCount>0&&!replaceConfirmed))return;
    setBusy(true);setError('');setProgress({message:'Preparando a importação…',percent:0});
    const key=`r2modman-import:${profile.id}`;
    try{
      const result=await streamApi(`/api/instances/${profile.id}/mods/r2modman/import`,{method:'POST',body:buildFormData({confirmOverwrite:replaceConfirmed,selectedMods,selectedConfigs,selectionProvided:true}),suppressFailureToast:true},event=>{
        if(event.type!=='progress')return;
        const totalBytes=Number(event.totalBytes)||0,downloaded=Number(event.downloadedBytes)||0;
        const percent=totalBytes>0?Math.min(100,Math.floor(downloaded*100/totalBytes)):null;
        setProgress({message:event.packageName?`Instalando ${event.packageName} · ${event.index} de ${event.total}`:'Instalando pacotes e dependências…',percent});
        toast(event.packageName?`Importando ${event.packageName} · ${event.index} de ${event.total}`:'Importando perfil r2modman…','progress',{key,title:'Importação de perfil',detail:totalBytes>0?`${bytes(downloaded)} de ${bytes(totalBytes)} baixados`:'Preparando os pacotes',progress:percent??undefined,progressText:percent===null?'':`${percent}%`});
      });
      setProgress({message:result.message,percent:100});
      toast(result.message,'success',{key,title:'Perfil r2modman importado',detail:`${result.installed?.length||0} pacote(s) instalados ou atualizados · ${result.configCount||0} configuração(ões).`,durationMs:10000});
      await onImported?.();
    }catch(reason){const message=reason.message||'A importação foi interrompida.';setError(message);toast(translateText(message,language),'error',{key,title:'Falha na importação',durationMs:12000});}
    finally{setBusy(false);}
  }

  return <Modal className="r2modman-import-modal" title="Importar perfil r2modman" description="Traga os mods nas versões exportadas e as configurações do perfil." close={closeIfIdle}>
    <div className="r2modman-import-content">
      {groupRunning&&<div className="notice r2modman-import-error"><CircleAlert size={16}/>Pare todos os servidores deste perfil para importar mods e configurações.</div>}
      {!preview&&<>
        <div className="r2modman-source-tabs" role="tablist" aria-label="Origem do perfil">
          <button type="button" role="tab" aria-selected={source==='file'} className={source==='file'?'active':''} onClick={()=>changeSource('file')}><Upload size={15}/> Arquivo .r2z</button>
          <button type="button" role="tab" aria-selected={source==='code'} className={source==='code'?'active':''} onClick={()=>changeSource('code')}><Package size={15}/> Código do perfil</button>
        </div>
        {source==='file'?<div className="r2modman-source-field"><input ref={fileInput} type="file" accept=".r2z,application/zip" onChange={event=>{setFile(event.target.files?.[0]||null);setError('');}}/><span className="r2modman-source-copy"><b>{file?.name||'Selecione um arquivo .r2z'}</b><small>{file?bytes(file.size):'Até 25 MB · mods, versões, estados e configurações'}</small></span><Button type="button" onClick={()=>fileInput.current?.click()}><Upload size={15}/>{file?'Trocar arquivo':'Selecionar arquivo'}</Button></div>:<label className="field-label">Código UUID do perfil<input className="field" value={code} onChange={event=>{setCode(event.target.value);setError('');}} placeholder="01a132a2-1328-63b7-0cc2-3a87e928fc70" autoComplete="off" spellCheck="false"/><small>O código é consultado no serviço do Thunderstore; alguns códigos podem expirar.</small></label>}
        <div className="notice r2modman-import-note"><ShieldCheck size={16}/><span>A importação adiciona ou atualiza os mods listados, aplica os estados ativo/inativo e grava as configurações em <code>BepInEx/config</code>. Mods que não estão na lista permanecem no perfil.</span></div>
      </>}
      {preview&&<>
        <div className="r2modman-import-preview-head"><span className="r2modman-preview-icon"><Package size={20}/></span><div><small>PERFIL ENCONTRADO</small><b>{preview.profileName}</b><span>{preview.packageCount} mods · {preview.enabledCount} ativos · {preview.disabledCount} inativos · {preview.configCount} configurações</span></div><Button type="button" onClick={()=>{setPreview(null);setReplaceConfirmed(false);}} disabled={busy}>Trocar origem</Button></div>
        {preview.frameworkAlreadyPresent&&<div className="notice r2modman-import-note"><ShieldCheck size={16}/><span>O BepInEx já está presente. Se o pacote estiver selecionado, o DSS atualizará ou adotará os arquivos e registrará a versão do perfil.</span></div>}
        <div className="r2modman-import-lists">
          <section><header><b>Mods e versões</b><span>{selectedMods.length} de {preview.packageCount} selecionados</span><div className="r2modman-list-actions"><button type="button" onClick={()=>setSelectedMods(preview.mods.map(mod=>mod.packageId))}>Todos</button><button type="button" onClick={()=>setSelectedMods([])}>Limpar</button></div></header><div className="r2modman-import-list">{preview.mods.map(mod=><label className={`r2modman-select-row ${selectedMods.includes(mod.packageId)?'selected':''}`} key={mod.packageId}><input className="dss-checkbox" type="checkbox" checked={selectedMods.includes(mod.packageId)} disabled={busy} onChange={event=>{setSelectedMods(current=>event.target.checked?[...current,mod.packageId]:current.filter(id=>id!==mod.packageId));setReplaceConfirmed(false);}}/><span className="r2modman-select-copy"><code>{mod.packageId}</code><span><small>v{mod.version}</small><small className={mod.enabled?'enabled':'disabled'}>{mod.enabled?'Ativo':'Inativo'}</small></span></span></label>)}</div></section>
          <section><header><b>Configurações</b><span>{selectedConfigs.length} de {preview.configCount} · {bytes(preview.configBytes)}</span><div className="r2modman-list-actions"><button type="button" onClick={()=>{setSelectedConfigs(preview.configs.map(config=>config.relativePath));setReplaceConfirmed(false);}}>Todas</button><button type="button" onClick={()=>{setSelectedConfigs([]);setReplaceConfirmed(false);}}>Limpar</button></div></header><div className="r2modman-import-list">{preview.configs.map(config=><label className={`r2modman-select-row ${selectedConfigs.includes(config.relativePath)?'selected':''}`} key={config.relativePath}><input className="dss-checkbox" type="checkbox" checked={selectedConfigs.includes(config.relativePath)} disabled={busy} onChange={event=>{setSelectedConfigs(current=>event.target.checked?[...current,config.relativePath]:current.filter(path=>path!==config.relativePath));setReplaceConfirmed(false);}}/><span className="r2modman-select-copy"><code>{config.relativePath}</code><span><small>{bytes(config.size)}</small>{preview.overwrites.includes(config.relativePath)&&<small className="overwrite">Substitui arquivo existente</small>}</span></span></label>)}</div></section>
        </div>
        <div className="notice r2modman-import-note"><ShieldCheck size={16}/><span>Somente os itens selecionados serão importados. As dependências necessárias dos mods selecionados também serão instaladas; outros mods e arquivos permanecem intactos.</span></div>
        {selectedOverwriteCount>0&&<label className="profile-check r2modman-overwrite-check"><input type="checkbox" checked={replaceConfirmed} onChange={event=>setReplaceConfirmed(event.target.checked)}/><span><b>Substituir {selectedOverwriteCount} configuração(ões) existente(s)</b><small>Os demais arquivos em BepInEx/config serão preservados.</small></span></label>}
      </>}
      {progress&&<div className="r2modman-import-progress" role="status"><div><LoaderCircle size={15} className={busy?'spin':''}/><span>{progress.message}</span>{progress.percent!==null&&<b>{progress.percent}%</b>}</div><i><span style={{width:progress.percent===null?'35%':`${progress.percent}%`}}/></i></div>}
      {localizedError&&<div className="notice r2modman-import-error" role="alert"><CircleAlert size={16}/>{localizedError}</div>}
      <footer className="modal-actions"><Button type="button" onClick={closeIfIdle} disabled={busy}>Cancelar</Button>{preview?<Button type="button" variant="primary" onClick={importProfile} disabled={busy||groupRunning||selectedItemCount===0||(selectedOverwriteCount>0&&!replaceConfirmed)}>{busy?<><LoaderCircle size={15} className="spin"/>Importando…</>:<><Package size={15}/>Importar {selectedMods.length} mod(s) e {selectedConfigs.length} config(s)</>}</Button>:<Button type="button" variant="primary" onClick={readPreview} disabled={!canSubmit||busy||groupRunning}>{busy?<><LoaderCircle size={15} className="spin"/>Lendo perfil…</>:<><Search size={15}/>Ver conteúdo do perfil</>}</Button>}</footer>
    </div>
  </Modal>;
}

function getPackageDependencyMinimum(dependency, packageId) {
  const prefix = `${packageId}-`;
  if (!String(dependency || '').toLocaleLowerCase().startsWith(prefix.toLocaleLowerCase())) return null;
  const version = String(dependency).slice(prefix.length).trim();
  return /^\d+(?:\.\d+){1,3}(?:[-+][\w.-]+)?$/.test(version) ? version : null;
}

function configFilesForMod(mod, allFiles) {
  const configPrefix = 'bepinex/config/';
  const owned = new Set((mod.managedFiles || []).map(path => {
    const normalized = String(path).replaceAll('\\', '/');
    const index = normalized.toLocaleLowerCase().indexOf(configPrefix);
    return index >= 0 ? normalized.slice(index + configPrefix.length).toLocaleLowerCase() : null;
  }).filter(Boolean));
  const normalize = value => String(value || '').toLocaleLowerCase().replace(/[^a-z0-9]/g, '');
  const packageName = String(mod.packageId || '').split('-').slice(1).join('-');
  const tokens = [...new Set([normalize(mod.name), normalize(packageName)].filter(token => token.length >= 5))];
  const related = allFiles.filter(path => {
    const normalizedPath = String(path).replaceAll('\\', '/').toLocaleLowerCase();
    if (owned.has(normalizedPath)) return true;
    const normalizedName = normalize(normalizedPath.split('/').at(-1)?.replace(/\.[^.]+$/, ''));
    return tokens.some(token => normalizedName.includes(token) || token.includes(normalizedName));
  });
  return related.length ? { files: related, matched: true } : { files: allFiles, matched: false };
}

function InstalledModSettingsModal({ profile, mod, packages, close, onApplied, navigate }) {
  const currentProvider=installedProviderInfo(mod).provider;
  const [provider,setProvider]=useState(currentProvider),[providerOptions,setProviderOptions]=useState([]),[versions,setVersions]=useState([]),[configFiles,setConfigFiles]=useState([]),[version,setVersion]=useState(mod.version),[loading,setLoading]=useState(true),[busy,setBusy]=useState(false),[error,setError]=useState('');
  const toast=useToast(),confirm=useConfirm();
  useEffect(()=>{
    let live=true;
    setProvider(currentProvider);setProviderOptions([]);setVersions([]);setVersion(mod.version);setLoading(true);setError('');
    async function load(){
      const [providersResult,configResult]=await Promise.allSettled([
        api('/api/providers'),api(`/api/instances/${profile.id}/configs`)
      ]);
      let candidates=[];
      if(providersResult.status==='fulfilled'){
        candidates=(providersResult.value.providers||[]).filter(item=>item.canInstall&&(!mod.isFramework||item.id==='thunderstore'));
      }
      if(!candidates.some(item=>item.id===currentProvider))
        candidates=[{id:currentProvider,name:providerNames[currentProvider]||currentProvider,canInstall:false},...candidates];
      const checks=await Promise.all(candidates.map(async item=>{
        if(!item.canInstall)return {...item,available:false,versions:[],error:'Instalação direta indisponível neste provedor.'};
        if(String(item.id).toLowerCase()==='nexus'&&!/^\d+$/.test(String(mod.packageId||'')))
          return {...item,available:false,versions:[],error:'O ID deste pacote não é um identificador numérico do Nexus.'};
        try{
          const result=await api(`/api/catalog/${encodeURIComponent(item.id)}/${encodeURIComponent(mod.packageId)}/versions`,{suppressFailureToast:true});
          const entries=(result.versions||[]).map(value=>typeof value==='string'?{version:value}:value).filter(value=>value.version);
          if(!entries.length&&result.currentVersion)entries.push({version:result.currentVersion});
          if(!entries.length)return {...item,available:false,versions:[],error:'O pacote não possui versões disponíveis.'};
          return {...item,available:true,versions:entries.sort((a,b)=>comparePackageVersions(b.version,a.version))};
        }catch(requestError){return {...item,available:false,versions:[],error:requestError.message||'Pacote não encontrado neste provedor.'};}
      }));
      if(!live)return;
      setProviderOptions(checks);
      const current=checks.find(item=>item.id===currentProvider);
      const availableSource=current?.available?current:checks.find(item=>item.available);
      if(availableSource?.available){
        const entries=[...availableSource.versions];
        if(availableSource.id===currentProvider&&!entries.some(item=>String(item.version).toLowerCase()===String(mod.version).toLowerCase()))entries.push({version:mod.version,current:true});
        setProvider(availableSource.id);
        setVersions(entries.sort((a,b)=>comparePackageVersions(b.version,a.version)));
        setVersion(availableSource.id===currentProvider?mod.version:entries[0]?.version||mod.version);
        if(!current?.available)setError(`Este pacote não foi localizado em ${current?.name||providerNames[currentProvider]||currentProvider}, mas está disponível em ${availableSource.name}. Você pode selecionar esse provedor.`);
      }else{
        setVersions([{version:mod.version,current:true}]);setVersion(mod.version);
        setError(current?.error||'Nenhum provedor disponível encontrou este pacote.');
      }
      if(configResult.status==='fulfilled')setConfigFiles(configResult.value.files||[]);
      else setError(previous=>previous?`${previous} ${configResult.reason?.message||'A lista de configurações não carregou.'}`:configResult.reason?.message||'A lista de configurações não carregou.');
      if(providersResult.status==='rejected'&&!current?.available)
        setError(previous=>previous?`${previous} Não foi possível consultar os provedores disponíveis.`:'Não foi possível consultar os provedores disponíveis.');
      setLoading(false);
    }
    load().catch(requestError=>{if(live){setError(requestError.message||'Não foi possível consultar os provedores.');setLoading(false);}});
    return()=>{live=false;};
  },[mod.id,mod.packageId,mod.version,mod.isFramework,profile.id,currentProvider]);
  const requirements=useMemo(()=>packages.filter(item=>item.id!==mod.id).flatMap(parent=>(parent.dependencies||[]).map(dependency=>({name:parent.name,version:getPackageDependencyMinimum(dependency,mod.packageId)})).filter(item=>item.version)).sort((a,b)=>comparePackageVersions(a.version,b.version)),[packages,mod.id,mod.packageId]);
  const minRequired=useMemo(()=>requirements.reduce((min,item)=>!min||comparePackageVersions(item.version,min)>0?item.version:min,null),[requirements]);
  const allowedVersions=mod.locked?versions.filter(item=>String(item.version).toLowerCase()===String(mod.version).toLowerCase()):versions.filter(item=>!minRequired||comparePackageVersions(item.version,minRequired)>=0||String(item.version).toLowerCase()===String(mod.version).toLowerCase());
  const configList=configFilesForMod(mod,configFiles);
  function chooseProvider(nextProvider){
    const source=providerOptions.find(item=>item.id===nextProvider);
    if(!source?.available)return;
    setProvider(nextProvider);setError('');
    const entries=[...source.versions];
    if(nextProvider===currentProvider&&!entries.some(item=>String(item.version).toLowerCase()===String(mod.version).toLowerCase()))entries.push({version:mod.version,current:true});
    entries.sort((a,b)=>comparePackageVersions(b.version,a.version));setVersions(entries);
    const compatible=entries.filter(item=>!minRequired||comparePackageVersions(item.version,minRequired)>=0||
      (nextProvider===currentProvider&&String(item.version).toLowerCase()===String(mod.version).toLowerCase()));
    setVersion(compatible.find(item=>String(item.version).toLowerCase()===String(mod.version).toLowerCase())?.version||compatible[0]?.version||'');
  }
  async function applyVersion(){
    if(mod.locked){toast(`${mod.name} está bloqueado na versão ${mod.version}. Desbloqueie-o para alterar a versão.`,'error');return;}
    const providerChanged=provider!==currentProvider;
    if(!version||(!providerChanged&&version.toLowerCase()===String(mod.version).toLowerCase())||busy)return;
    if(minRequired&&comparePackageVersions(version,minRequired)<0){toast(`A versão mínima aceita pelas dependências é ${minRequired}.`,'error');return;}
    const providerName=providerNames[provider]||provider;
    if(!await confirm({title:providerChanged?'Trocar provedor e versão?':'Alterar versão do mod?',description:`Instalar ${mod.name} ${version} pelo ${providerName}. ${providerChanged?'Os arquivos gerenciados deste mod serão substituídos pela cópia do novo provedor. ':''}As dependências deste perfil exigem pelo menos ${minRequired||allowedVersions.at(-1)?.version||version}.`,confirmLabel:providerChanged?'Trocar provedor':'Aplicar versão'}))return;
    setBusy(true);
    try{const result=await api(`/api/instances/${profile.id}/mods/${mod.id}/version`,{method:'POST',body:json({version,provider})});onApplied(result.message||`${mod.name} atualizado para ${version}.`);}
    catch(e){setError(e.message);toast(e.message,'error');}
    finally{setBusy(false);}
  }
  function openConfig(file){
    activeConfigFiles.set(profile.id,file);
    close();
    navigate(`/games/${profile.gameId||'valheim'}/servers/${profile.id}/bepinex?file=${encodeURIComponent(file)}&focus=editor`);
  }
  return <Modal title={`Gerenciar ${mod.name}`} description={`Versão atual ${mod.version} · origem ${providerNames[currentProvider]||currentProvider}`} close={close} className="mod-settings-modal">
    <section className="mod-version-section"><div className="modal-section-title"><div><h3>Versão do mod</h3><p>Versões abaixo do mínimo exigido pelas dependências ficam bloqueadas.</p></div><span className="state-badge">ATUAL {mod.version}</span></div>
      {mod.locked&&<div className="notice version-locked-notice"><Lock size={15}/> Versão bloqueada em {mod.version}. Desbloqueie este mod no card para instalar versões manualmente ou atender a dependências.</div>}{loading?<Spinner label="Buscando pacote nos provedores e carregando configurações…"/>:<><label className="field-label">Provedor para instalar<select className="field" value={provider} disabled={mod.locked||profile.runtimeStatus==='running'||busy||providerOptions.length<1} onChange={event=>chooseProvider(event.target.value)}>{providerOptions.map(item=><option key={item.id} value={item.id} disabled={!item.available}>{item.name}{item.available?' · pacote encontrado':` · ${item.error||'indisponível'}`}</option>)}</select></label>
      <label className="field-label">Versão para instalar<select className="field" value={version} disabled={mod.locked||profile.runtimeStatus==='running'||busy||!allowedVersions.length} onChange={event=>setVersion(event.target.value)}>{allowedVersions.map(item=><option key={item.version} value={item.version}>{item.version}{String(item.version).toLowerCase()===String(mod.version).toLowerCase()&&provider===currentProvider?' · instalada':''}{minRequired&&comparePackageVersions(item.version,minRequired)===0?' · mínimo permitido':''}</option>)}</select></label>
      <div className="version-minimum-note"><b>Versão mais antiga aceita: {minRequired||allowedVersions.at(-1)?.version||mod.version}</b>{requirements.length?<ul>{requirements.map((item,index)=><li key={`${item.name}-${item.version}-${index}`}>{item.name} exige no mínimo {item.version}</li>)}</ul>:<small>Nenhum mod instalado declara uma versão mínima para este pacote.</small>}</div>
      {error&&<div className="inline-error" role="alert">{error}</div>}
      <div className="modal-actions"><Button onClick={close}>Fechar</Button><Button variant="primary" disabled={mod.locked||busy||profile.runtimeStatus==='running'||!version||(provider===currentProvider&&version.toLowerCase()===String(mod.version).toLowerCase())||!!(minRequired&&comparePackageVersions(version,minRequired)<0)} onClick={applyVersion}>{busy?'Instalando…':provider!==currentProvider?'Trocar provedor e instalar': 'Aplicar versão'}</Button></div></>}
    </section>
    <section className="mod-config-section"><div className="modal-section-title"><div><h3>Arquivos de configuração</h3><p>{configList.matched?'Arquivos associados a este mod.':'Não foi possível associá-los automaticamente; mostrando os arquivos disponíveis no perfil.'}</p></div><span className="state-badge">{configList.files.length}</span></div>
      {configList.files.length?<div className="mod-config-file-list">{configList.files.map(file=><button type="button" key={file} onClick={()=>openConfig(file)} disabled={profile.runtimeStatus==='running'} title={profile.runtimeStatus==='running'?'Pare o servidor para abrir arquivos de configuração':`Abrir ${file} no editor BepInEx`}><FileCog size={15}/><span>{file}</span><ExternalLink size={13}/></button>)}</div>:<Empty title="Nenhum arquivo de configuração encontrado" description="Os arquivos aparecem depois que o mod cria configurações em BepInEx/config."/>}
    </section>
  </Modal>;
}

function comparePackageVersions(left, right) {
  const normalize = value => String(value || '').trim().replace(/^[vV]/, '').split('+')[0];
  const a = normalize(left).split('-'); const b = normalize(right).split('-');
  const aCore = a[0].split('.').map(part => Number.parseInt(part, 10) || 0);
  const bCore = b[0].split('.').map(part => Number.parseInt(part, 10) || 0);
  for (let i=0;i<Math.max(aCore.length,bCore.length);i++) if ((aCore[i]||0)!==(bCore[i]||0)) return (aCore[i]||0)-(bCore[i]||0);
  if (a.length===1&&b.length>1) return 1;
  if (b.length===1&&a.length>1) return -1;
  return String(a[1]||'').localeCompare(String(b[1]||''),undefined,{numeric:true,sensitivity:'base'});
}

function parseCatalogDependency(raw) {
  const value=String(raw||'');const match=value.match(/^(.+)-(\d+\.\d+(?:\.\d+)?(?:[-+][\w.-]+)?)$/);
  return match?{packageId:match[1],minimumVersion:match[2]}:null;
}

function DependencyPopover({ dependencies = [], emptyLabel = 'Sem dependências declaradas' }) {
  const id = `dependency-popover-${useId().replace(/:/g, '')}`;
  const anchor = useRef(null);
  const panel = useRef(null);
  const [open, setOpen] = useState(false);
  const [position, setPosition] = useState(null);
  const entries = dependencies.map(raw => {
    if (raw && typeof raw === 'object') return { packageId: raw.packageId || raw.name || 'Pacote desconhecido', minimumVersion: raw.minimumVersion || raw.version || '' };
    const value = String(raw || '');
    const parsed = parseCatalogDependency(value);
    return { packageId: parsed?.packageId || value || 'Pacote desconhecido', minimumVersion: parsed?.minimumVersion || '' };
  });

  useEffect(() => {
    if (!open) return undefined;
    const place = () => {
      if (!anchor.current || !panel.current) return;
      const rect = anchor.current.getBoundingClientRect();
      const bounds = panel.current.getBoundingClientRect();
      const edge = 12;
      const left = Math.max(edge, Math.min(rect.left + rect.width / 2 - bounds.width / 2, window.innerWidth - bounds.width - edge));
      const top = rect.top >= bounds.height + edge * 2 ? rect.top - bounds.height - edge : Math.min(rect.bottom + edge, window.innerHeight - bounds.height - edge);
      setPosition(current => current?.left === left && current?.top === top ? current : { left, top });
    };
    const frame = requestAnimationFrame(place);
    const onOutside = event => { if (!anchor.current?.contains(event.target) && !panel.current?.contains(event.target)) setOpen(false); };
    const onKey = event => { if (event.key === 'Escape') setOpen(false); };
    window.addEventListener('resize', place);
    window.addEventListener('scroll', place, true);
    document.addEventListener('pointerdown', onOutside);
    document.addEventListener('keydown', onKey);
    return () => {
      cancelAnimationFrame(frame);
      window.removeEventListener('resize', place);
      window.removeEventListener('scroll', place, true);
      document.removeEventListener('pointerdown', onOutside);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  if (!entries.length) return <span className="dependency-empty">{emptyLabel}</span>;
  return <span className={`dependency-popover-anchor ${open ? 'open' : ''}`}>
    <button ref={anchor} type="button" className="dependency-trigger" aria-haspopup="dialog" aria-expanded={open} aria-controls={id} onClick={() => setOpen(value => !value)}>
      <span>{entries.length} {entries.length === 1 ? 'dependência' : 'dependências'}</span><ChevronDown size={13}/>
    </button>
    {open && createPortal(<section ref={panel} id={id} className="dependency-popover-panel" role="dialog" aria-label="Dependências do mod" style={position || { left: -9999, top: -9999 }}>
      <header className="dependency-popover-heading"><div><b>Dependências declaradas</b><small>Pacotes necessários para este mod</small></div><span>{entries.length}</span></header>
      <div className="dependency-popover-list">{entries.map((entry, index) => <div className="dependency-popover-row" key={`${entry.packageId}-${index}`}><span className="dependency-popover-index">{index + 1}</span><div><b>{entry.packageId}</b><small>{entry.minimumVersion ? <>Versão mínima <code>v{entry.minimumVersion}</code></> : 'Versão mínima não informada'}</small></div></div>)}</div>
      <footer>O DSS verifica a compatibilidade antes da instalação.</footer>
    </section>, document.body)}
  </span>;
}

function DependencyVersionList({ rows, onChange }) {
  const [versions,setVersions]=useState(()=>Object.fromEntries(rows.filter(row=>row.selectedVersion).map(row=>[row.packageId,row.selectedVersion])));
  return <div className="dependency-plan-list">{rows.map(row=><article className={`dependency-plan-row ${row.error?'blocked':''}`} key={row.packageId}>
    <div className="dependency-plan-status"><span className={`dependency-check ${row.installed?'installed':''}`}>{row.installed?'✓':'•'}</span><div><b>{row.name||row.packageId}</b><small>{row.packageId} · mínimo v{row.minimumVersion||'não especificado'}</small></div></div>
    {row.error?<span className="dependency-plan-error">{row.error}</span>:<label className="dependency-plan-version">{row.installed?'Instalado':'Versão para instalar'}<select className="field" value={versions[row.packageId]||row.selectedVersion} onChange={event=>{setVersions(current=>({...current,[row.packageId]:event.target.value}));onChange(row.packageId,event.target.value);}}>{row.versions.map(version=><option key={version.version} value={version.version}>v{version.version}{version.version===row.installedVersion?' · instalada':''}</option>)}</select></label>}
  </article>)}</div>;
}

function CatalogStatus({ item, installedByPackage }) {
  const installed = installedByPackage.get(String(item.packageId || '').toLowerCase());
  if (!installed) return null;
  return <div className="mod-tags catalog-status-tags"><b className={`tag ${installed.enabled ? 'tag-active' : 'tag-inactive'}`}>{installed.enabled ? 'ATIVO' : 'INATIVO'}</b></div>;
}

async function installPackagesWithProgress({ profileId, provider, packageIds, dependencyVersions, toast, key, onLegacyResult }) {
  const report = event => {
    if (event.type !== 'progress') return;
    const downloaded = Number(event.downloadedBytes) || 0, total = Number(event.totalBytes) || 0;
    const percent = total > 0 ? Math.min(100, Math.floor(downloaded * 100 / total)) : null;
    const label = event.phase === 'download' ? 'Baixando' : 'Instalando';
    toast(`${label} mod ${event.packageName} · ${event.index} de ${event.total}`, 'progress', {
      key, title: 'Instalação de mods',
      detail: total > 0 ? `${bytes(downloaded)} de ${bytes(total)} baixados` : `${bytes(downloaded)} baixados · tamanho total indisponível`,
      progress: percent, progressText: percent === null ? '' : `${percent}%`
    });
  };
  try {
    return await streamApi(`/api/instances/${profileId}/mods/install-batch`, {
      method: 'POST', body: json({ provider, packageIds, dependencyVersions }), suppressFailureToast: true
    }, report);
  } catch (error) {
    if (error.status !== 405) throw error;
    const installed = new Map();
    for (let index = 0; index < packageIds.length; index++) {
      const packageId = packageIds[index];
      toast(`Instalando mod ${packageId} · ${index + 1} de ${packageIds.length}`, 'progress', {
        key, title: 'Instalação de mods', detail: 'Esta versão do DSS não transmite percentual do download.'
      });
      const response = await api(`/api/instances/${profileId}/mods/install`, {
        method: 'POST', body: json({ provider, packageId, dependencyVersions }), suppressFailureToast: true
      });
      for (const mod of response.installed || []) installed.set(String(mod.packageId || '').toLowerCase(), mod);
      onLegacyResult?.(response, packageId);
    }
    return { installed: [...installed.values()], message: `${packageIds.length} mod(s) instalado(s).` };
  }
}

function Catalog({ profile, canEditProfile = true, groupRunning = false }) {
  const { language } = useDss();
  const [provider,setProvider]=useState('thunderstore'); const [states,setStates]=useState(()=>catalogSessionCache); const [fields,setFields]=useState(['title','creator','description','packageId']); const [sort,setSort]=useState('downloads');const [direction,setDirection]=useState('desc');const [pageSize,setPageSize]=useState(24);const [layout,setLayout]=useState('cards');const [selected,setSelected]=useState([]);const [installingBatch,setInstallingBatch]=useState(false);const toast=useToast();const confirm=useConfirm();
  const [installedPackages,setInstalledPackages]=useState([]);
  const state=states[provider]||{q:'',items:[],total:null,page:1,totalPages:null,searched:false,countLoaded:false,countLoading:false};
  const installedByPackage=useMemo(()=>new Map(installedPackages.map(mod=>[String(mod.packageId||'').toLowerCase(),mod])),[installedPackages]);
  const installedSet=useMemo(()=>new Set(installedByPackage.keys()),[installedByPackage]);
  useEffect(()=>{let active=true;api(`/api/instances/${profile.id}/mods`).then(result=>{if(active)setInstalledPackages(result.packages||[]);}).catch(()=>{});return()=>{active=false;};},[profile.id]);
  function writeProviderState(providerKey,patch){const current=catalogSessionCache[providerKey]||{q:'',items:[],total:null,page:1,totalPages:null,searched:false,countLoaded:false,countLoading:false};const next=typeof patch==='function'?patch(current):{...current,...patch};catalogSessionCache={...catalogSessionCache,[providerKey]:next};setStates(catalogSessionCache);return next;}
  function update(patch){writeProviderState(provider,{...(catalogSessionCache[provider]||{}),...patch});}
  const loading=Boolean(state.loading);
  async function loadCount(providerKey,queryText,selectedFields,requestedPageSize,requestId){try{const p=new URLSearchParams({provider:providerKey,q:queryText,fields:selectedFields,pageSize:String(requestedPageSize)});const result=await api(`/api/catalog/count?${p}`);if((catalogRequestSequence[providerKey]||0)!==requestId)return;const current=catalogSessionCache[providerKey]||{};if(current.q!==queryText||current.searchFields!==selectedFields||current.searchPageSize!==requestedPageSize)return;writeProviderState(providerKey,{total:result.total,totalPages:result.totalPages,countLoaded:true,countLoading:false,countQuery:queryText,countFields:selectedFields,countPageSize:requestedPageSize,countError:''});}catch(error){if((catalogRequestSequence[providerKey]||0)!==requestId)return;writeProviderState(providerKey,{countLoading:false,countError:error.message});}}
  async function search(page=1){const providerKey=provider,queryText=state.q||'',selectedFields=fields.join(','),requestedPageSize=pageSize;const requestId=(catalogRequestSequence[providerKey]||0)+1;catalogRequestSequence[providerKey]=requestId;const reusableCount=state.countLoaded&&state.countQuery===queryText&&state.countFields===selectedFields&&state.countPageSize===requestedPageSize;writeProviderState(providerKey,{page,searched:true,loading:true,error:'',q:queryText,searchFields:selectedFields,searchPageSize:requestedPageSize,countLoaded:reusableCount,countLoading:!reusableCount});try{const p=new URLSearchParams({provider:providerKey,q:queryText,fields:selectedFields,page:String(page),pageSize:String(requestedPageSize),sort,direction,includeTotal:'false'});const r=await api(`/api/catalog?${p}`);if((catalogRequestSequence[providerKey]||0)!==requestId)return;const current=catalogSessionCache[providerKey]||{};const totalReady=r.total!=null||reusableCount;writeProviderState(providerKey,{items:r.items||[],total:r.total??(reusableCount?current.total:null),page:r.page||page,totalPages:r.totalPages??(reusableCount?current.totalPages:null),searched:true,loading:false,error:'',q:queryText,searchFields:selectedFields,searchPageSize:requestedPageSize,countLoaded:totalReady,countLoading:!totalReady});if(!totalReady)void loadCount(providerKey,queryText,selectedFields,requestedPageSize,requestId);}catch(error){if((catalogRequestSequence[providerKey]||0)!==requestId)return;writeProviderState(providerKey,{error:error.message,searched:true,items:[],loading:false,countLoading:false});}}
  useEffect(()=>{const cached=catalogSessionCache[provider]||state;if(!cached.searched){search(1);return;}if(!cached.countLoaded&&!cached.countLoading){const requestId=catalogRequestSequence[provider]||0;void loadCount(provider,cached.q||'',cached.searchFields||fields.join(','),cached.searchPageSize||pageSize,requestId);}},[provider]);
  async function install(item){if((groupRunning || profile.runtimeStatus === 'running'))return toast('Pare o servidor antes de instalar mods.','error');if(installedSet.has(item.packageId.toLowerCase()))return toast('Este pacote já está instalado neste perfil.');const progressKey=`install:${profile.id}:${item.packageId}`;try{
    const installedInventory=await api(`/api/instances/${profile.id}/mods`);
    setInstalledPackages(installedInventory.packages||[]);
    const declared=(item.dependencies||[]).map(raw=>({raw,parsed:parseCatalogDependency(raw)}));
    const rows=await Promise.all(declared.map(async dependency=>{
      if(!dependency.parsed)return {packageId:dependency.raw,name:dependency.raw,minimumVersion:'?',versions:[],selectedVersion:'',installed:false,error:'Formato de dependência não reconhecido.'};
      const {packageId,minimumVersion}=dependency.parsed;
      const framework=packageId.toLowerCase()==='denikson-bepinexpack_valheim';
      const installedPackage=(installedInventory.packages||[]).find(mod=>mod.packageId?.toLowerCase()===packageId.toLowerCase());
      const isInstalled=Boolean(installedPackage?.enabled&&comparePackageVersions(installedPackage.version,minimumVersion)>=0)||(framework&&installedInventory.loaderInstalled);
       if(installedPackage?.locked&&(!installedPackage.enabled||comparePackageVersions(installedPackage.version,minimumVersion)<0))return {packageId,name:installedPackage.name||packageId,minimumVersion,versions:[],selectedVersion:'',installed:false,installedVersion:installedPackage.version,locked:true,error:`${installedPackage.name} está bloqueado na versão ${installedPackage.version}, abaixo do mínimo ${minimumVersion}. Desbloqueie-o para instalar esta dependência.`};
      try{
        const source=framework?'thunderstore':item.provider;
        const available=await api(`/api/catalog/${encodeURIComponent(source)}/${encodeURIComponent(packageId)}/versions`);
        const compatible=(available.versions||[]).filter(version=>comparePackageVersions(version.version,minimumVersion)>=0).sort((a,b)=>comparePackageVersions(b.version,a.version));
        if(!compatible.length)return {packageId,name:available.name||packageId,minimumVersion,versions:[],selectedVersion:'',installed:isInstalled,installedVersion:installedPackage?.version,error:`Nenhuma versão compatível (>= ${minimumVersion}) disponível no catálogo.`};
        const versions=[...compatible];
        if(installedPackage?.version&&!versions.some(version=>version.version===installedPackage.version)&&comparePackageVersions(installedPackage.version,minimumVersion)>=0)versions.unshift({version:installedPackage.version});
        const selectedVersion=isInstalled&&versions.some(version=>version.version===installedPackage?.version)?installedPackage.version:versions[0].version;
        return {packageId,name:available.name||installedPackage?.name||packageId,minimumVersion,versions,selectedVersion,installed:isInstalled,installedVersion:installedPackage?.version||''};
      }catch(error){return {packageId,name:installedPackage?.name||packageId,minimumVersion,versions:[],selectedVersion:'',installed:isInstalled,installedVersion:installedPackage?.version||'',error:error.message||'Não foi possível consultar versões.'};}
    }));
    const invalid=rows.some(row=>row.error||!row.selectedVersion);
    const isFrameworkPackage=String(item.packageId||'').toLocaleLowerCase()==='denikson-bepinexpack_valheim'||String(item.provider||'').toLocaleLowerCase()==='bepinex';
    let choices=Object.fromEntries(rows.filter(row=>row.selectedVersion).map(row=>[row.packageId,row.selectedVersion]));
    const accepted=await confirm({title:'Revisar dependências',description:item.dependencies?.length?`${item.name} declara ${item.dependencies.length} dependência(s). Escolha uma versão igual ou superior ao mínimo solicitado.`:`Instalar ${item.name} no perfil ${profile.name}?`,content:<><DependencyVersionList rows={rows} onChange={(id,version)=>{choices={...choices,[id]:version};}}/>{invalid&&<div className="dependency-plan-warning">Uma ou mais dependências não têm versão compatível disponível. A instalação está bloqueada.</div>}{!isFrameworkPackage&&<div className="notice">Se o BepInEx não estiver ativo neste perfil, o DSS também o instalará automaticamente.</div>}</>,confirmLabel:item.dependencies?.length?'Instalar mod e dependências':'Instalar mod',confirmDisabled:invalid});
    if(!accepted)return;
    toast(`Preparando ${item.name} e verificando dependências.`,'progress',{key:progressKey,title:'Instalação de mod',detail:'Confirmação recebida. O DSS está preparando os pacotes.'});
    const installProvider=isFrameworkPackage?'bepinex':item.provider;
    const r=await installPackagesWithProgress({profileId:profile.id,provider:installProvider,packageIds:[item.packageId],dependencyVersions:choices,toast,key:progressKey});toast(`1 mod instalado: ${item.name}.`,'success',{key:progressKey,title:'Instalação concluída',detail:`${Math.max(0,(r.installed?.length||1)-1)} dependência(s) incluída(s).`,modDetails:[{provider:installProvider,packageId:item.packageId,name:item.name}],durationMs:10000});setInstalledPackages(current=>{const next=new Map(current.map(mod=>[String(mod.packageId||'').toLowerCase(),mod]));for(const mod of r.installed||[])next.set(String(mod.packageId||'').toLowerCase(),mod);return [...next.values()];});setSelected(s=>s.filter(id=>id!==item.packageId));
  }catch(e){toast(e.message,'error',{key:progressKey,title:'Falha ao instalar mod',durationMs:12000});}}
  async function installBatch(){
    const chosen=state.items.filter(item=>selected.includes(item.packageId));
    if(!chosen.length||installingBatch)return;
    if((groupRunning || profile.runtimeStatus === 'running'))return toast('Pare o servidor antes de instalar mods.','error');
    setInstallingBatch(true);
    const progressKey=`install-batch:${profile.id}`;
    try{
      const inventory=await api(`/api/instances/${profile.id}/mods`),installed=inventory.packages||[];
      setInstalledPackages(installed);
      const requirements=new Map(),malformed=new Map();
      for(const item of chosen)for(const raw of item.dependencies||[]){
        const parsed=parseCatalogDependency(raw);
        if(!parsed){malformed.set(String(raw).toLowerCase(),{packageId:raw,name:raw,minimumVersion:'?',versions:[],selectedVersion:'',installed:false,error:'Formato de dependência não reconhecido.'});continue;}
        const key=parsed.packageId.toLowerCase(),previous=requirements.get(key);
        if(!previous||comparePackageVersions(parsed.minimumVersion,previous.minimumVersion)>0)
          requirements.set(key,{packageId:parsed.packageId,minimumVersion:parsed.minimumVersion,provider:item.provider,requiredBy:new Set([item.name])});
        else previous.requiredBy.add(item.name);
      }
      const rows=[...malformed.values(),...await Promise.all([...requirements.values()].map(async dependency=>{
        const framework=dependency.packageId.toLowerCase()==='denikson-bepinexpack_valheim';
        const current=installed.find(mod=>mod.packageId?.toLowerCase()===dependency.packageId.toLowerCase());
        if(current?.locked&&(!current.enabled||comparePackageVersions(current.version,dependency.minimumVersion)<0))return {packageId:dependency.packageId,name:current.name,minimumVersion:dependency.minimumVersion,versions:[],selectedVersion:'',installed:false,installedVersion:current.version,locked:true,error:`${current.name} está bloqueado na versão ${current.version}, abaixo do mínimo ${dependency.minimumVersion}. Desbloqueie-o para instalar esta dependência.`};
        const isInstalled=Boolean(current?.enabled&&comparePackageVersions(current.version,dependency.minimumVersion)>=0)||(framework&&inventory.loaderInstalled);
        try{
          const source=framework?'thunderstore':dependency.provider;
          const available=await api(`/api/catalog/${encodeURIComponent(source)}/${encodeURIComponent(dependency.packageId)}/versions`);
          const versions=(available.versions||[]).filter(version=>comparePackageVersions(version.version,dependency.minimumVersion)>=0).sort((a,b)=>comparePackageVersions(b.version,a.version));
          if(!versions.length)return {packageId:dependency.packageId,name:available.name||current?.name||dependency.packageId,minimumVersion:dependency.minimumVersion,versions:[],selectedVersion:'',installed:isInstalled,installedVersion:current?.version||'',error:`Nenhuma versão compatível (>= ${dependency.minimumVersion}) disponível no catálogo.`};
          if(current?.version&&!versions.some(version=>version.version===current.version)&&comparePackageVersions(current.version,dependency.minimumVersion)>=0)versions.unshift({version:current.version});
          const selectedVersion=isInstalled&&versions.some(version=>version.version===current?.version)?current.version:versions[0].version;
          return {packageId:dependency.packageId,name:available.name||current?.name||dependency.packageId,minimumVersion:dependency.minimumVersion,versions,selectedVersion,installed:isInstalled,installedVersion:current?.version||'',requiredBy:[...dependency.requiredBy]};
        }catch(error){return {packageId:dependency.packageId,name:current?.name||dependency.packageId,minimumVersion:dependency.minimumVersion,versions:[],selectedVersion:'',installed:isInstalled,installedVersion:current?.version||'',error:error.message||'Não foi possível consultar versões.'};}
      }))];
      const invalid=rows.some(row=>row.error||!row.selectedVersion);
      const choices=Object.fromEntries(chosen.map(item=>[item.packageId,item.version]));
      for(const row of rows)if(row.selectedVersion)choices[row.packageId]=row.selectedVersion;
      const dependencyCount=rows.filter(row=>row.selectedVersion&&!row.installed).length;
      const accepted=await confirm({title:`Instalar ${chosen.length} mods em lote`,description:`O DSS vai resolver uma única árvore de dependências para os ${chosen.length} mods selecionados, deduplicando dependências compartilhadas e usando uma versão compatível com todos.`,content:<><DependencyVersionList rows={rows} onChange={(id,version)=>{choices[id]=version;}}/>{invalid&&<div className="dependency-plan-warning">Uma ou mais dependências não têm versão compatível disponível. A instalação está bloqueada.</div>}<div className="notice">Os pacotes serão baixados em sequência para mostrar o progresso e instalados na ordem exigida pelas dependências. {dependencyCount?`${dependencyCount} dependência(s) ainda precisam ser instaladas.`:'As dependências já estão instaladas nas versões compatíveis.'} Dependências compartilhadas são instaladas uma única vez. O BepInEx também será incluído quando necessário.</div></>,confirmLabel:`Instalar lote (${chosen.length} mods)`,confirmDisabled:invalid});
      if(!accepted)return;
      toast(`Preparando ${chosen.length} mods e consolidando dependências.`,'progress',{key:progressKey,title:'Instalação em lote',detail:'Confirmação recebida. O DSS está consultando as versões compatíveis.'});
      const installWithCompatibility=async()=>{
        try{
          const installProvider=chosen.length===1&&String(chosen[0].packageId).toLowerCase()==='denikson-bepinexpack_valheim'?'bepinex':chosen[0].provider;
          return await streamApi(`/api/instances/${profile.id}/mods/install-batch`,{method:'POST',body:json({provider:installProvider,packageIds:chosen.map(item=>item.packageId),dependencyVersions:choices}),suppressFailureToast:true},event=>{
            if(event.type!=='progress')return;
            const downloaded=Number(event.downloadedBytes)||0,total=Number(event.totalBytes)||0,percent=total>0?Math.min(100,Math.floor(downloaded*100/total)):null;
            toast(`Baixando mod ${event.packageName} · ${event.index} de ${event.total}`,'progress',{key:progressKey,title:'Instalação em lote',detail:total>0?`${bytes(downloaded)} de ${bytes(total)} baixados`:`${bytes(downloaded)} baixados · tamanho total indisponível`,progress:percent,progressText:percent===null?'':`${percent}%`});
          });
        }catch(error){
          if(error.status!==405)throw error;
          const installedById=new Map(),completedRoots=[];
          for(let index=0;index<chosen.length;index++){
            const item=chosen[index],percent=Math.floor(index*100/chosen.length);
            toast(`Instalando mod ${item.name} · ${index+1} de ${chosen.length}`,'progress',{key:progressKey,title:'Instalação em lote · compatibilidade',detail:'O DSS atual não oferece progresso de download detalhado; as dependências compartilhadas são reaproveitadas pelo servidor.',progress:percent,progressText:`${index+1}/${chosen.length}`});
            try{
              const itemProvider=String(item.packageId).toLowerCase()==='denikson-bepinexpack_valheim'?'bepinex':item.provider;
              const response=await api(`/api/instances/${profile.id}/mods/install`,{method:'POST',body:json({provider:itemProvider,packageId:item.packageId,dependencyVersions:choices}),suppressFailureToast:true});
              for(const mod of response.installed||[])installedById.set(String(mod.packageId||'').toLowerCase(),mod);
              completedRoots.push(item.packageId);
            }catch(installError){
              throw Object.assign(new Error(`${installError.message} O lote foi interrompido após ${completedRoots.length} de ${chosen.length} mods.`),{partialInstalled:[...installedById.values()],completedRootIds:completedRoots});
            }
          }
          const installed=[...installedById.values()];
          return {installed,message:`Lote concluído: ${chosen.length} mods processados. Dependências compartilhadas foram reaproveitadas.`};
        }
      };
      const result=await installWithCompatibility();
       const installedCount=chosen.length,refs=chosen.map(item=>({provider:item.provider,packageId:item.packageId,name:item.name})),batchDependencyCount=Math.max(0,(result.installed?.length||installedCount)-installedCount);
       toast(`${installedCount} ${installedCount===1?'mod instalado':'mods instalados'}${installedCount>1?' em lote':''}.`,'success',{key:progressKey,title:installedCount>1?'Instalação em lote concluída':'Instalação concluída',detail:`${batchDependencyCount} ${batchDependencyCount===1?'dependência incluída':'dependências incluídas'}; dependências compartilhadas foram deduplicadas.`,modDetails:refs,durationMs:10000});
      setInstalledPackages(current=>{const next=new Map(current.map(mod=>[String(mod.packageId||'').toLowerCase(),mod]));for(const mod of result.installed||[])next.set(String(mod.packageId||'').toLowerCase(),mod);return [...next.values()];});
      setSelected(current=>current.filter(id=>!chosen.some(item=>item.packageId===id)));
    }catch(error){
      if(error.partialInstalled){
        setInstalledPackages(current=>{const next=new Map(current.map(mod=>[String(mod.packageId||'').toLowerCase(),mod]));for(const mod of error.partialInstalled)next.set(String(mod.packageId||'').toLowerCase(),mod);return [...next.values()];});
        setSelected(current=>current.filter(id=>!error.completedRootIds?.includes(id)));
      }
       const completedRoots=(error.completedRootIds||[]).map(id=>chosen.find(item=>item.packageId===id)).filter(Boolean);
       const installedRootCount=completedRoots.length;
       toast(installedRootCount?`${installedRootCount} ${installedRootCount===1?'mod instalado':'mods instalados'} em lote antes da falha.`:'Nenhum mod do lote foi instalado.', 'error',{key:progressKey,title:error.partialInstalled?.length?'Instalação em lote parcialmente concluída':'Instalação em lote interrompida',detail:error.partialInstalled?.length?`${error.partialInstalled.length} ${error.partialInstalled.length===1?'pacote foi instalado':'pacotes foram instalados'}, incluindo dependências. ${error.message}`:error.message,modDetails:completedRoots.map(item=>({provider:item.provider,packageId:item.packageId,name:item.name})),durationMs:12000});
    }
    finally{setInstallingBatch(false);}
  }
  const fieldsMeta=[['title','Título'],['creator','Criador'],['description','Descrição'],['packageId','ID do pacote'],['version','Versão'],['tags','Tags']];
  return <Card className={`catalog-page ${canEditProfile?'':'profile-view-controls'}`}><div className="provider-tabs">{['thunderstore','hexium','nexus'].map(p=><button key={p} className={provider===p?'active':''} onClick={()=>setProvider(p)} title={providerNames[p]} aria-label={providerNames[p]} aria-pressed={provider===p}><ProviderIcon provider={p}/><span>{providerNames[p]}</span></button>)}</div><form className="catalog-search-row" onSubmit={e=>{e.preventDefault();search(1);}}><label className="search-input"><Search size={17}/><input className="field" value={state.q||''} onChange={e=>update({q:e.target.value})} placeholder="Buscar mods…"/><button type="button" aria-label="Limpar busca" onClick={()=>update({q:''})}><X size={15}/></button></label><Button variant="primary">Buscar</Button></form><section className="advanced-search"><div className="eyebrow">BUSCAR EM</div><div className="field-checks">{canEditProfile?fieldsMeta.map(([id,label])=><label key={id} className={`field-check ${fields.includes(id)?'checked':''}`}><input type="checkbox" checked={fields.includes(id)} onChange={e=>setFields(s=>e.target.checked?[...s,id]:s.filter(v=>v!==id))}/><span>{fields.includes(id)?'✓':''}</span>{label}</label>):<span className="read-only-search-fields">Título · Criador · Descrição · ID do pacote</span>}</div><div className="catalog-controls"><label>ORDENAR POR<select className="field" value={sort} onChange={e=>{setSort(e.target.value);if(state.searched)setTimeout(()=>search(1),0);}}><option value="downloads">Downloads</option><option value="recent">Mais recentes</option><option value="title">Título</option><option value="creator">Criador</option></select></label><label>ORDEM<select className="field" value={direction} onChange={e=>setDirection(e.target.value)}><option value="desc">Decrescente</option><option value="asc">Crescente</option></select></label><label>POR PÁGINA<select className="field" value={pageSize} onChange={e=>setPageSize(Number(e.target.value))}><option>12</option><option>24</option><option>36</option><option>48</option></select></label><div className="layout-toggle"><span>VISUALIZAÇÃO</span><button className={layout==='cards'?'active':''} onClick={()=>setLayout('cards')} type="button"><Package size={16}/></button><button className={layout==='list'?'active':''} onClick={()=>setLayout('list')} type="button"><List size={16}/></button></div></div></section><div className="catalog-hint">{provider==='nexus'?<>Catálogo completo do Nexus com paginação. A instalação direta depende das permissões da sua conta. <Link to="/integracoes">Configurar chave ↗</Link></>:'Filtre por um ou mais campos. A busca consulta o catálogo público do provedor.'}{canEditProfile&&<Button onClick={installBatch} disabled={!selected.length||installingBatch}>{installingBatch?<><RefreshCw size={15} className="spin"/> Revisando dependências…</>:<>Instalar selecionados{selected.length?` (${selected.length})`:''}</>}</Button>}</div><div className="catalog-results-head"><b>{state.countLoaded?`${Number(state.total||0).toLocaleString(language)} mods encontrados`:state.countLoading?'Carregando total de mods…':'Quantidade de mods pendente'}</b><small>{provider==='nexus'?'A instalação direta respeita as permissões e limites de download da sua conta Nexus.':canEditProfile?'Selecione vários pacotes para instalação em lote. Dependências compartilhadas serão instaladas uma única vez.':'Consulte os pacotes disponíveis no catálogo público.'}</small></div>{loading?<div className="catalog-loading"><Spinner label={`Buscando no ${providerNames[provider]}…`}/></div>:state.error?<div className="inline-error"><CircleAlert size={16}/>{state.error}</div>:state.items?.length?<div className={`catalog-grid ${layout}`}>{state.items.map(item=>{const installed=installedByPackage.get(item.packageId.toLowerCase());return <article key={item.packageId} className={`catalog-item ${selected.includes(item.packageId)?'selected':''} ${installed&&!installed.enabled?'mod-inactive':''}`}>{canEditProfile&&<label className={`select-mod ${installedSet.has(item.packageId.toLowerCase())?'blocked':''}`} title={installedSet.has(item.packageId.toLowerCase())?'Mod já instalado':'Selecionar mod'}><input type="checkbox" checked={selected.includes(item.packageId)} disabled={installedSet.has(item.packageId.toLowerCase())} onChange={e=>setSelected(s=>e.target.checked?[...s,item.packageId]:s.filter(id=>id!==item.packageId))}/><span/></label>}<CatalogStatus item={item} installedByPackage={installedByPackage}/><a className="catalog-mod-image" href={item.projectUrl||undefined} target="_blank" rel="noreferrer" aria-label={`Abrir página de ${item.name}`} title={`Abrir ${item.name} no provedor`}>{item.iconUrl?<img src={item.iconUrl} alt="" loading="lazy"/>:<Package size={34}/>}</a><div className="catalog-mod-info"><div className="catalog-mod-heading"><div><h3><a href={item.projectUrl||undefined} target="_blank" rel="noreferrer">{item.name}</a></h3><span>por {item.owner||providerNames[item.provider]}</span><code>v{item.version} · {Number(item.totalDownloads||0).toLocaleString()} downloads{item.fileSize?` · ${bytes(item.fileSize)}`:''}</code><span className="catalog-upload-row"><small>Primeiro envio {item.createdAt?fmtDate(item.createdAt):'—'} · Último envio {item.updatedAt?fmtDate(item.updatedAt):'—'}</small><small className="catalog-rating">👍 {Number(item.ratingScore??0).toLocaleString()}</small></span></div><a className="provider-source" href={item.projectUrl||'#'} target="_blank" rel="noreferrer" title={providerNames[item.provider]}><ProviderIcon provider={item.provider}/></a></div><p title={item.description||''}>{item.description}</p><div className="package-tags" title={(item.categories||[]).join(' · ')}>{(item.categories||[]).slice(0,5).map(tag=><span key={tag}>{tag}</span>)}{(item.categories?.length||0)>5&&<span className="more-tags">+{item.categories.length-5}</span>}</div><div className="catalog-mod-actions"><DependencyPopover dependencies={item.dependencies} />{canEditProfile&&<Button variant="primary" size="small" disabled={(groupRunning || profile.runtimeStatus === 'running')||installedSet.has(item.packageId.toLowerCase())} onClick={()=>install(item)}>{installedSet.has(item.packageId.toLowerCase())?'JÁ INSTALADO':'Instalar no perfil'}</Button>}</div></div></article>;})}</div>:<Empty title={state.searched?'Nenhum mod encontrado':'Explore os mods de Valheim'} description={state.searched?'Tente outro termo ou altere os campos de busca.':'Escolha os campos e pressione Buscar para consultar o catálogo completo.'}/>}<NumericPagination page={state.page||1} totalPages={state.totalPages} disabled={!state.countLoaded||loading} onPage={search}/></Card>;
}

function htmlEscape(value) { return String(value ?? '').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'",'&#39;'); }
function highlightIniLine(line) {
  if (/^\s*(?:#|;|\/\/)/.test(line)) return `<span class="syntax-comment">${htmlEscape(line)}</span>`;
  const section=line.match(/^(\s*)(\[)([^\]]*)(\])(.*)$/);
  if(section)return `${htmlEscape(section[1])}<span class="syntax-bracket">[</span><span class="syntax-section">${htmlEscape(section[3])}</span><span class="syntax-bracket">]</span>${highlightIniLine(section[5])}`;
  const ini=line.match(/^(\s*)([^=]+?)(\s*=\s*)(.*)$/);
  if(!ini)return htmlEscape(line).replace(/([{}\[\]])/g,'<span class="syntax-bracket">$1</span>');
  const rawValue=ini[4];let highlightedValue=rawValue.replace(/("(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|\b(?:true|false|yes|no|on|off)\b|\b(?:null|none)\b|-?\b\d+(?:\.\d+)?(?:e[+-]?\d+)?\b|[\[\]{}])/gi,token=>{const lower=token.toLowerCase();const kind=/^[\[\]{}]$/.test(token)?'syntax-bracket':/^(true|false|yes|no|on|off)$/.test(lower)?'syntax-boolean':/^(null|none)$/.test(lower)?'syntax-null':/^['"]/.test(token)?'syntax-string':'syntax-number';return `<span class="${kind}">${htmlEscape(token)}</span>`;});if(highlightedValue===rawValue&&rawValue.trim())highlightedValue=`<span class="syntax-string">${htmlEscape(rawValue)}</span>`;
  return `${htmlEscape(ini[1])}<span class="syntax-key">${htmlEscape(ini[2])}</span>${htmlEscape(ini[3])}${highlightedValue}`;
}
function highlightIni(text) { return `${String(text??'').split('\n').map(highlightIniLine).join('\n')}\n`; }
function markedLine(value,other,side) {
  const text=String(value??'');if(!text)return '&#8203;';
  const mark=line=>line?`<mark class="diff-${side}">${htmlEscape(line)}</mark>`:'';
  if(other==null)return text.split('\n').map(mark).join('<br>');
  return text.split('\n').map((line,index)=>{
    const compare=String(other).split('\n')[index];if(compare==null)return mark(line);
    if(line===compare)return highlightIniLine(line);
    let prefix=0;while(prefix<line.length&&prefix<compare.length&&line[prefix]===compare[prefix])prefix++;
    let suffix=0;while(suffix<line.length-prefix&&suffix<compare.length-prefix&&line[line.length-1-suffix]===compare[compare.length-1-suffix])suffix++;
    const middle=line.slice(prefix,line.length-suffix),comment=/^\s*(?:#|;|\/\/)/.test(line);
    const render=(part,isChanged=false)=>isChanged?mark(part):comment?htmlEscape(part):highlightIniLine(part);
    const output=render(line.slice(0,prefix))+render(middle,true)+render(line.slice(line.length-suffix));
    return comment?`<span class="syntax-comment">${output}</span>`:output;
  }).join('<br>');
}
function buildLineChanges(beforeText,afterText) {
  const before=String(beforeText??'').split('\n'),after=String(afterText??'').split('\n'),changes=new Map();
  if((before.length+1)*(after.length+1)>4_000_000){for(let i=0;i<Math.max(before.length,after.length);i++)if(before[i]!==after[i]){const beforeLines=before[i]==null?[]:[before[i]],afterLines=after[i]==null?[]:[after[i]];changes.set(i,{before:beforeLines.join('\n'),after:afterLines.length?afterLines.join('\n'):null,beforeLines,afterLines,beforeIndex:i,afterIndex:i,beforeCount:before.length,afterCount:after.length,beforeAnchor:i>0?after[i-1]??null:null,afterAnchor:i<after.length?after[i]??null:null});}return changes;}
  const width=after.length+1,dp=new Uint32Array((before.length+1)*width);for(let i=before.length-1;i>=0;i--)for(let j=after.length-1;j>=0;j--){const at=i*width+j;dp[at]=before[i]===after[j]?1+dp[(i+1)*width+j+1]:Math.max(dp[(i+1)*width+j],dp[at+1]);}
  let i=0,j=0;while(i<before.length||j<after.length){if(i<before.length&&j<after.length&&before[i]===after[j]){i++;j++;continue;}const oldStart=i,newStart=j;while(i<before.length||j<after.length){if(i<before.length&&j<after.length&&before[i]===after[j])break;if(i<before.length&&(j>=after.length||dp[(i+1)*width+j]>=dp[i*width+j+1]))i++;else if(j<after.length)j++;}const beforeLines=before.slice(oldStart,i),afterLines=after.slice(newStart,j),change={before:beforeLines.join('\n'),after:afterLines.length?afterLines.join('\n'):null,beforeLines,afterLines,beforeIndex:oldStart,afterIndex:newStart,beforeCount:before.length,afterCount:after.length,beforeAnchor:newStart>0?after[newStart-1]:null,afterAnchor:newStart+afterLines.length<after.length?after[newStart+afterLines.length]:null};if(afterLines.length)for(let k=newStart;k<newStart+afterLines.length;k++)changes.set(k,change);else changes.set(newStart,change);}return changes;
}
function groupReviewChanges(changes,beforeText,afterText,maxContextLines=2){
  const before=String(beforeText??'').split('\n'),after=String(afterText??'').split('\n');
  const hunks=[...new Set(changes.values())].sort((a,b)=>a.afterIndex-b.afterIndex||a.beforeIndex-b.beforeIndex);
  const grouped=[];
  for(const change of hunks){
    const current=grouped.at(-1);
    if(!current){grouped.push({...change});continue;}
    const previousBeforeEnd=current.beforeIndex+current.beforeLines.length,previousAfterEnd=current.afterIndex+current.afterLines.length;
    const beforeGap=change.beforeIndex-previousBeforeEnd,afterGap=change.afterIndex-previousAfterEnd;
    if(beforeGap<0||afterGap<0||beforeGap!==afterGap||afterGap>maxContextLines){grouped.push({...change});continue;}
    const beforeEnd=change.beforeIndex+change.beforeLines.length,afterEnd=change.afterIndex+change.afterLines.length;
    current.beforeLines=before.slice(current.beforeIndex,beforeEnd);
    current.afterLines=after.slice(current.afterIndex,afterEnd);
    current.before=current.beforeLines.join('\n');current.after=current.afterLines.length?current.afterLines.join('\n'):null;
    current.beforeAnchor=current.afterIndex>0?after[current.afterIndex-1]:null;
    current.afterAnchor=afterEnd<after.length?after[afterEnd]:null;
  }
  return grouped;
}

function configOwnersForFile(path, installedMods) {
  const leaf=String(path||'').replaceAll('\\','/').split('/').at(-1)||'';
  const normalizedPath=String(path||'').replaceAll('\\','/').replace(/^\/+|\/+$/g,'').toLocaleLowerCase();
  if(!normalizedPath)return [];
  const normalize=value=>String(value||'').toLocaleLowerCase().replace(/[^a-z0-9]/g,'');
  const stem=normalize(leaf.replace(/\.cfg$/i,''));
  return (installedMods||[]).filter(mod=>{
    const managed=(mod.managedFiles||[]).some(file=>{
      const managedPath=String(file).replaceAll('\\','/').replace(/^\/+|\/+$/g,'').toLocaleLowerCase();
      return managedPath===normalizedPath||managedPath.endsWith(`/bepinex/config/${normalizedPath}`)||managedPath.endsWith(`/config/${leaf}`.toLocaleLowerCase());
    });
    if(managed)return true;
    if(!/\.cfg$/i.test(leaf)||/^bepinex\.cfg$/i.test(leaf))return false;
    const packageId=String(mod.packageId||'').split('-').slice(1).join('-');
    const tokens=[mod.name,packageId,...String(mod.packageId||'').split('-').slice(1)].map(normalize).filter(token=>token.length>=5);
    return tokens.some(token=>stem.includes(token)||token.includes(stem));
  });
}
function configFileIsOrphan(path, installedMods) {
  const leaf=String(path||'').replaceAll('\\','/').split('/').at(-1)||'';
  if(!/\.cfg$/i.test(leaf)||/^bepinex\.cfg$/i.test(leaf))return false;
  return configOwnersForFile(path,installedMods).length===0;
}

function parseBepInExConfig(content) {
  const lines=String(content??'').split('\n'),groups=[];let section='Geral',pending={};
  const groupFor=name=>{let group=groups.find(item=>item.name===name);if(!group){group={name,settings:[]};groups.push(group);}return group;};
  for(let index=0;index<lines.length;index++){
    const line=lines[index],sectionMatch=line.trim().match(/^\[([^\]]+)\]$/);
    if(sectionMatch){section=sectionMatch[1].trim()||'Geral';pending={};groupFor(section);continue;}
    const metadata=line.match(/^\s*#\s*(?:Setting\s+)?(type|description|default value|acceptable values|is advanced|is hidden)\s*:\s*(.*?)\s*$/i);
    if(metadata){pending[metadata[1].toLocaleLowerCase().replaceAll(' ','')]=metadata[2];continue;}
    const settingMatch=line.match(/^(\s*[^#;\s][^=]*?\s*=\s*)(.*)$/);
    if(settingMatch){
      const key=settingMatch[1].slice(0,settingMatch[1].indexOf('=')).trim();
      if(key)groupFor(section).settings.push({key,lineIndex:index,prefix:settingMatch[1],value:settingMatch[2].trim(),type:pending.type||'',description:pending.description||'',defaultValue:pending.defaultvalue,choices:(pending.acceptablevalues||'').split(',').map(value=>value.trim()).filter(Boolean),advanced:/^true$/i.test(pending.isadvanced||''),hidden:/^true$/i.test(pending.ishidden||'')});
      pending={};continue;
    }
    if(line.trim()&&!/^\s*[#;]/.test(line))pending={};
  }
  return groups.filter(group=>group.settings.length);
}
function bepinexSettingChanged(setting) {
  if(setting.defaultValue===undefined)return false;
  const isBoolean=/bool/i.test(setting.type)||/^(true|false)$/i.test(setting.value)||/^(true|false)$/i.test(setting.defaultValue);
  return isBoolean
    ? /^true$/i.test(setting.value)!==/^true$/i.test(setting.defaultValue)
    : setting.value.trim()!==String(setting.defaultValue).trim();
}

function BepInExStructuredEditor({content,onChange,readOnly}){
  const [filter,setFilter]=useState(''),[showAdvanced,setShowAdvanced]=useState(true),[showHidden,setShowHidden]=useState(false),[changedOnly,setChangedOnly]=useState(false),[collapsed,setCollapsed]=useState({});
  const groups=useMemo(()=>parseBepInExConfig(content),[content]),normalizedFilter=filter.trim().toLocaleLowerCase();
  function setValue(setting,value){const lines=String(content??'').split('\n');lines[setting.lineIndex]=setting.prefix+value;onChange(lines.join('\n'));}
  const visibleGroups=groups.map(group=>({...group,settings:group.settings.filter(setting=>{
    if(setting.advanced&&!showAdvanced)return false;if(setting.hidden&&!showHidden)return false;
       if(changedOnly&&!bepinexSettingChanged(setting))return false;
      return !normalizedFilter||`${group.name} ${setting.key} ${setting.description} ${setting.value}`.toLocaleLowerCase().includes(normalizedFilter);
   })})).filter(group=>group.settings.length);
  if(!groups.length)return <div className="bepinex-form-empty"><SlidersHorizontal size={22}/><b>Este arquivo não tem parâmetros .cfg reconhecidos</b><span>Alterne para o editor de texto para editar seu conteúdo livremente.</span></div>;
   return <div className="bepinex-structured-editor"><div className="bepinex-form-toolbar"><label className="bepinex-form-filter"><Search size={15}/><input className="field" value={filter} onChange={event=>setFilter(event.target.value)} placeholder="Filtrar configurações…" aria-label="Filtrar configurações"/></label><label className="bepinex-form-option"><input type="checkbox" checked={showAdvanced} onChange={event=>setShowAdvanced(event.target.checked)}/><span>Avançadas</span></label><label className="bepinex-form-option"><input type="checkbox" checked={showHidden} onChange={event=>setShowHidden(event.target.checked)}/><span>Ocultas</span></label><label className="bepinex-form-option"><input type="checkbox" checked={changedOnly} onChange={event=>setChangedOnly(event.target.checked)}/><span>Alteradas</span></label></div><div className="bepinex-form-groups">{visibleGroups.length?visibleGroups.map(group=>{const isCollapsed=Boolean(collapsed[group.name]);return <section className="bepinex-form-group" key={group.name}><button type="button" className="bepinex-form-group-head" aria-expanded={!isCollapsed} onClick={()=>setCollapsed(current=>({...current,[group.name]:!current[group.name]}))}><span><ChevronDown size={15} className={isCollapsed?'collapsed':''}/><b>{group.name}</b></span><small>{group.settings.length} {group.settings.length===1?'opção':'opções'}</small></button>{!isCollapsed&&<div className="bepinex-form-settings">{group.settings.map(setting=>{const isBoolean=/bool/i.test(setting.type)||/^(true|false)$/i.test(setting.value),isNumber=/int|float|double|single|number/i.test(setting.type)||/^-?\d+(?:\.\d+)?$/.test(setting.value),isChanged=bepinexSettingChanged(setting);return <label className={`bepinex-form-setting ${isChanged?'changed':''}`} key={`${setting.key}:${setting.lineIndex}`}><span className="bepinex-form-label"><b>{setting.key}</b>{setting.description&&<small>{setting.description}</small>}{setting.advanced&&<i>Avançada</i>}{setting.hidden&&<i>Oculta</i>}</span>{isBoolean?<span className="dss-switch"><input type="checkbox" role="switch" checked={/^true$/i.test(setting.value)} disabled={readOnly} onChange={event=>setValue(setting,event.target.checked?'true':'false')} aria-label={setting.key}/><i/></span>:setting.choices.length>0?<select className="field" value={setting.value} disabled={readOnly} onChange={event=>setValue(setting,event.target.value)} aria-label={setting.key}>{!setting.choices.includes(setting.value)&&<option value={setting.value}>{setting.value}</option>}{setting.choices.map(choice=><option key={choice} value={choice}>{choice}</option>)}</select>:<input className="field" type={isNumber?'number':'text'} value={setting.value} disabled={readOnly} onChange={event=>setValue(setting,event.target.value)} aria-label={setting.key}/>}</label>;})}</div>}</section>}):<Empty title="Nenhum parâmetro corresponde ao filtro" description="Altere o filtro ou mostre configurações avançadas e ocultas."/>}</div></div>;
}

function configReviewFieldChanges(beforeContent,afterContent){
  const before=new Map(parseBepInExConfig(beforeContent).flatMap(group=>group.settings.map(setting=>[`${group.name}\0${setting.key}`,{group:group.name,setting}])));
  const after=new Map(parseBepInExConfig(afterContent).flatMap(group=>group.settings.map(setting=>[`${group.name}\0${setting.key}`,{group:group.name,setting}])));
  const keys=new Set([...before.keys(),...after.keys()]);
  return [...keys].flatMap(key=>{
    const old=before.get(key),next=after.get(key);if(old?.setting.value===next?.setting.value)return [];
    return [{section:next?.group||old?.group||'Geral',setting:next?.setting||old?.setting,beforeValue:old?.setting.value??null,afterValue:next?.setting.value??null,beforeLine:old?.setting.lineIndex,afterLine:next?.setting.lineIndex}];
  }).sort((a,b)=>a.section.localeCompare(b.section)||a.setting.key.localeCompare(b.setting.key));
}

function ReviewFieldValue({setting,value}){
  if(value===null)return <span className="save-review-missing">Não definido</span>;
  const boolean=/bool/i.test(setting.type)||/^(true|false)$/i.test(value),choices=setting.choices||[];
  if(boolean)return <span className="save-review-boolean"><span className="dss-switch"><input type="checkbox" role="switch" checked={/^true$/i.test(value)} disabled readOnly/><i/></span>{/^true$/i.test(value)?'Ativado':'Desativado'}</span>;
  if(choices.length)return <select className="field" value={value} disabled aria-label={setting.key}><option value={value}>{value}</option></select>;
  return <input className="field" type="text" value={value} readOnly aria-label={setting.key}/>;
}

function SaveReviewFields({changes}){
  const sections=[];for(const change of changes){let section=sections.find(([name])=>name===change.section);if(!section){section=[change.section,[]];sections.push(section);}section[1].push(change);}
  return <div className="save-review-fields"><div className="save-review-field-heading"><span>Configuração</span><span>Antes</span><span>Depois</span></div>{sections.map(([section,items])=><section className="save-review-field-section" key={section}><header><b>{section}</b><small>{items.length} {items.length===1?'campo alterado':'campos alterados'}</small></header>{items.map((change,index)=><div className="save-review-field-row" key={`${change.setting.key}-${index}`}><div className="save-review-field-label"><b>{change.setting.key}</b>{change.setting.description&&<small>{change.setting.description}</small>}<span>Antes: {change.beforeLine==null?'novo':`linha ${change.beforeLine+1}`} · Depois: {change.afterLine==null?'removido':`linha ${change.afterLine+1}`}</span></div><ReviewFieldValue setting={change.setting} value={change.beforeValue}/><ReviewFieldValue setting={change.setting} value={change.afterValue}/></div>)}</section>)}</div>;
}

function BepInExEditor({ profile, groupRunning = false, accessReadOnly = false }) {
  const location=useLocation(),requestedFile=new URLSearchParams(location.search).get('file');
  const [files,setFiles]=useState([]),[installedMods,setInstalledMods]=useState([]),[file,setFile]=useState(''),[content,setContent]=useState(''),[saved,setSaved]=useState(''),[loading,setLoading]=useState(true),[find,setFind]=useState(''),[replace,setReplace]=useState(''),[hint,setHint]=useState(''),[diffLine,setDiffLine]=useState(null),[diffAnchor,setDiffAnchor]=useState(null),[filesCollapsed,setFilesCollapsed]=useState(false),[saveReviewOpen,setSaveReviewOpen]=useState(false),[reviewMode,setReviewMode]=useState('text'),[saving,setSaving]=useState(false),[editorMode,setEditorMode]=useState(()=>localStorage.getItem('dss-bepinex-editor-mode')||'text'),[configSearch,setConfigSearch]=useState(''),[bulkDeleteOpen,setBulkDeleteOpen]=useState(false),[bulkConfigSearch,setBulkConfigSearch]=useState(''),[bulkSelected,setBulkSelected]=useState(()=>new Set()),[hideInstalledBulkConfigs,setHideInstalledBulkConfigs]=useState(false),[deletingBulk,setDeletingBulk]=useState(false);
  const textarea=useRef(),highlight=useRef(),gutter=useRef(),diffPopover=useRef(),diffAnchorTarget=useRef(),openRequest=useRef(0),initialFileOpened=useRef(false),requestedFileHandled=useRef(''),toast=useToast(),confirm=useConfirm(),dirty=content!==saved;
  const readOnly=accessReadOnly||groupRunning||profile.runtimeStatus==='running';
  const path=`/api/instances/${profile.id}/configs/${file.split('/').map(encodeURIComponent).join('/')}`;
  function positionDiffPopover(){const anchor=diffAnchorTarget.current,frame=textarea.current?.closest('.editor-frame');if(!anchor?.isConnected||!frame)return;const markerRow=anchor.closest('.line-gutter>div'),row=(markerRow||anchor).getBoundingClientRect(),bounds=frame.getBoundingClientRect(),panel=diffPopover.current,width=Math.max(0,Math.min(720,bounds.width-16)),below=Math.max(0,bounds.bottom-row.bottom-8),above=Math.max(0,row.top-bounds.top-8),desired=Math.min(520,Math.max(260,panel?.scrollHeight||380)),placeAbove=below<desired&&above>below,available=placeAbove?above:below,maxHeight=Math.max(0,Math.min(520,available)),top=placeAbove?Math.max(8,row.top-bounds.top-maxHeight-8):Math.max(8,row.bottom-bounds.top);if(width<1||maxHeight<1)return;if(panel){panel.style.width=`${width}px`;panel.style.maxHeight=`${maxHeight}px`;}const left=Math.max(8,(bounds.width-width)/2);setDiffAnchor({left,top,width,maxHeight});}
  function openDiff(event,change){diffAnchorTarget.current=event.currentTarget;setDiffLine(change);requestAnimationFrame(positionDiffPopover);}
  function closeDiff(){setDiffLine(null);setDiffAnchor(null);diffAnchorTarget.current=null;}
  useEffect(()=>{if(!diffLine)return;const reposition=()=>positionDiffPopover(),keydown=event=>{if(event.key==='Escape')closeDiff();},outside=event=>{if(!diffPopover.current?.contains(event.target)&&!event.target.closest?.('.line-gutter button'))closeDiff();};window.addEventListener('resize',reposition);window.addEventListener('scroll',reposition,true);window.addEventListener('keydown',keydown);document.addEventListener('pointerdown',outside);return()=>{window.removeEventListener('resize',reposition);window.removeEventListener('scroll',reposition,true);window.removeEventListener('keydown',keydown);document.removeEventListener('pointerdown',outside);};},[diffLine]);
  const loadFiles=useCallback(async()=>{setLoading(true);try{const [configResult,modResult]=await Promise.all([api(`/api/instances/${profile.id}/configs`),api(`/api/instances/${profile.id}/mods`)]);setFiles(configResult.files||[]);setInstalledMods(modResult.packages||[]);}catch(e){toast(e.message,'error');}finally{setLoading(false);}},[profile.id,toast]);
  useEffect(()=>{loadFiles();},[loadFiles]);
  useEffect(()=>{if(!accessReadOnly&&file){configDraftCache.set(configDraftKey(profile.id,file),{saved,content});notifyConfigDrafts();}},[accessReadOnly,profile.id,file,saved,content]);
  function updateContent(next){if(readOnly)return;if(file){configDraftCache.set(configDraftKey(profile.id,file),{saved,content:next});notifyConfigDrafts();}setContent(next);}
  function isDraftDirty(name){if(accessReadOnly)return false;const draft=configDraftCache.get(configDraftKey(profile.id,name));return draft?draft.content!==draft.saved:file===name&&dirty;}
  async function openFile(next){if(!next||next===file)return;if(file&&!accessReadOnly)configDraftCache.set(configDraftKey(profile.id,file),{saved,content});activeConfigFiles.set(profile.id,next);const request=++openRequest.current;const cached=!accessReadOnly&&configDraftCache.get(configDraftKey(profile.id,next));if(cached&&cached.content!==cached.saved){setFile(next);setSaved(cached.saved);setContent(cached.content);notifyConfigDrafts();return;}try{const r=await api(`/api/instances/${profile.id}/configs/${next.split('/').map(encodeURIComponent).join('/')}`);if(request!==openRequest.current)return;const text=String(r.content??'').replace(/\r\n/g,'\n');setFile(next);setContent(text);setSaved(text);if(!accessReadOnly){configDraftCache.set(configDraftKey(profile.id,next),{saved:text,content:text});notifyConfigDrafts();}}catch(e){toast(e.message,'error');}}
  useEffect(()=>{if(loading||!files.length)return;const explicit=requestedFile&&files.includes(requestedFile)?requestedFile:null;const newRequest=!!explicit&&requestedFileHandled.current!==explicit;if(initialFileOpened.current&&!newRequest)return;initialFileOpened.current=true;if(explicit)requestedFileHandled.current=explicit;const preferred=explicit||activeConfigFiles.get(profile.id);void openFile(preferred&&files.includes(preferred)?preferred:files[0]);},[loading,files,profile.id,requestedFile]);
  useEffect(()=>{if(accessReadOnly||!requestedFile||file!==requestedFile||new URLSearchParams(location.search).get('focus')!=='editor')return;const frame=requestAnimationFrame(()=>textarea.current?.focus({preventScroll:true}));return()=>cancelAnimationFrame(frame);},[accessReadOnly,file,location.search,requestedFile]);
  async function save(){if(!dirty||readOnly||saving)return;setSaving(true);try{await api(path,{method:'PUT',body:json({content})});setSaved(content);configDraftCache.set(configDraftKey(profile.id,file),{saved:content,content});notifyConfigDrafts();setSaveReviewOpen(false);toast('Arquivo salvo e revisão criada.');}catch(e){toast(e.message,'error');}finally{setSaving(false);}}
  async function discard(){if(readOnly)return;if(dirty){updateContent(saved);toast('Alterações descartadas.');}}
  async function deleteConfig(name){if(readOnly)return;if(groupRunning||profile.runtimeStatus==='running')return toast('Pare todos os servidores do perfil antes de excluir um arquivo BepInEx.','error');const hasDraft=isDraftDirty(name);if(!await confirm({title:'Excluir arquivo de configuração',description:`Excluir ${name}? O arquivo poderá ser gerado novamente na próxima inicialização do servidor.${hasDraft?' As alterações locais não salvas também serão descartadas.':''}`,confirmLabel:'Segure 5 s para excluir',confirmHoldMs:5000,danger:true}))return;try{const r=await api(`/api/instances/${profile.id}/configs/${name.split('/').map(encodeURIComponent).join('/')}`,{method:'DELETE'});configDraftCache.delete(configDraftKey(profile.id,name));if(activeConfigFiles.get(profile.id)===name)activeConfigFiles.delete(profile.id);notifyConfigDrafts();if(file===name){setFile('');setContent('');setSaved('');closeDiff();}toast(r.message||'Arquivo excluído.');await loadFiles();}catch(e){toast(e.message,'error');}}
  function toggleBulkConfig(name,checked){if(readOnly)return;setBulkSelected(current=>{const next=new Set(current);if(checked)next.add(name);else next.delete(name);return next;});}
  async function deleteSelectedConfigs(){if(readOnly||deletingBulk)return;const selected=bulkConfigFiles.filter(name=>bulkSelected.has(name));if(!selected.length)return;setDeletingBulk(true);try{const result=await api(`/api/instances/${profile.id}/configs/delete-batch`,{method:'POST',body:json({paths:selected})});const deleted=new Set(result.deleted||[]);for(const name of deleted){configDraftCache.delete(configDraftKey(profile.id,name));if(activeConfigFiles.get(profile.id)===name)activeConfigFiles.delete(profile.id);}notifyConfigDrafts();if(deleted.has(file)){setFile('');setContent('');setSaved('');closeDiff();}setBulkSelected(new Set());setBulkDeleteOpen(false);toast(result.message||`${deleted.size} configuração(ões) excluída(s).`,result.failed?.length?'error':'success');await loadFiles();}catch(error){toast(error.message,'error');}finally{setDeletingBulk(false);}}
  function findNext(){if(!find)return;const el=textarea.current,lower=content.toLocaleLowerCase();let at=lower.indexOf(find.toLocaleLowerCase(),el.selectionEnd);if(at<0)at=lower.indexOf(find.toLocaleLowerCase());if(at<0){setHint('Texto não encontrado.');return;}el.focus();el.setSelectionRange(at,at+find.length);setHint('Correspondência selecionada.');}
  function replaceOne(){if(readOnly)return;const el=textarea.current;if(!find)return;const start=el.selectionStart,end=el.selectionEnd;if(content.slice(start,end).toLowerCase()===find.toLowerCase()){const next=content.slice(0,start)+replace+content.slice(end);updateContent(next);requestAnimationFrame(()=>el.setSelectionRange(start,start+replace.length));setHint('Texto substituído.');}else findNext();}
  function replaceAll(){if(readOnly||!find)return;const escaped=find.replace(/[.*+?^${}()|[\]\\]/g,'\\$&'),rx=new RegExp(escaped,'gi'),count=(content.match(rx)||[]).length;updateContent(content.replace(rx,replace));setHint(`${count} ocorrência(s) substituída(s).`);}  useEffect(()=>{const keydown=e=>{if(document.activeElement!==textarea.current)return;if((e.ctrlKey||e.metaKey)&&!e.altKey&&e.key.toLowerCase()==='f'){e.preventDefault();e.stopImmediatePropagation();document.getElementById('cfg-find')?.focus();}else if((e.ctrlKey||e.metaKey)&&!e.altKey&&e.key.toLowerCase()==='r'){e.preventDefault();e.stopImmediatePropagation();document.getElementById('cfg-replace')?.focus();}};window.addEventListener('keydown',keydown,true);return()=>window.removeEventListener('keydown',keydown,true);},[content]);
  const newLines=content.split('\n'),changes=buildLineChanges(saved,content),markerRows=Math.max(newLines.length,...[...changes.keys()].map(index=>index+1),1);
  function syncScroll(e){const el=e.currentTarget;if(highlight.current)highlight.current.style.transform='translate('+(-el.scrollLeft)+'px,'+(-el.scrollTop)+'px)';if(gutter.current)gutter.current.scrollTop=el.scrollTop;if(diffAnchorTarget.current)requestAnimationFrame(positionDiffPopover);}
  const diffPreview=diffLine?(()=>{const oldLines=saved.split('\n'),newLinesForDiff=content.split('\n'),oldStart=Math.max(0,diffLine.beforeIndex-3),newStart=Math.max(0,diffLine.afterIndex-3),oldEnd=Math.min(oldLines.length,diffLine.beforeIndex+Math.max(diffLine.beforeLines.length,1)+3),newEnd=Math.min(newLinesForDiff.length,diffLine.afterIndex+Math.max(diffLine.afterLines.length,1)+3);return {before:oldLines.slice(oldStart,oldEnd).join('\n'),after:newLinesForDiff.slice(newStart,newEnd).join('\n'),beforeOffset:oldStart,afterOffset:newStart};})():null;
  function revertDraftChange(change,closePopover=false){if(readOnly||!change)return;const lines=content.split('\n');if(lines.length!==change.afterCount){toast('Restauração bloqueada: a quantidade de linhas mudou desde a comparação.','error');return;}const at=change.afterIndex;if(change.afterLines.length){const actual=lines.slice(at,at+change.afterLines.length);if(actual.length!==change.afterLines.length||actual.some((line,index)=>line!==change.afterLines[index])){toast('Restauração bloqueada: o trecho não corresponde ao arquivo atual.','error');return;}}else{const beforeAnchor=at>0?lines[at-1]:null,afterAnchor=at<lines.length?lines[at]:null;if(beforeAnchor!==change.beforeAnchor||afterAnchor!==change.afterAnchor){toast('Restauração bloqueada: não foi possível confirmar as linhas vizinhas.','error');return;}}lines.splice(at,change.afterLines.length,...change.beforeLines);updateContent(lines.join('\n'));if(closePopover)closeDiff();setHint('Trecho revertido; as demais alterações continuam no rascunho.');toast('Linhas revertidas no editor.');}
  function rollbackLine(){revertDraftChange(diffLine,true);}

  const bulkConfigFiles=useMemo(()=>files.filter(name=>/\.cfg$/i.test(name)&&!/^BepInEx\.cfg$/i.test(name.replaceAll('\\','/').split('/').at(-1)||'')),[files]);
  const uninstalledBulkConfigFiles=useMemo(()=>bulkConfigFiles.filter(name=>configOwnersForFile(name,installedMods).length===0),[bulkConfigFiles,installedMods]);
  const filteredBulkConfigFiles=useMemo(()=>{const query=bulkConfigSearch.trim().toLocaleLowerCase();return bulkConfigFiles.filter(name=>(!query||name.toLocaleLowerCase().includes(query))&&(!hideInstalledBulkConfigs||configOwnersForFile(name,installedMods).length===0));},[bulkConfigFiles,bulkConfigSearch,hideInstalledBulkConfigs,installedMods]);
  const filteredConfigFiles=useMemo(()=>{const query=configSearch.trim().toLocaleLowerCase();return query?files.filter(name=>name.toLocaleLowerCase().includes(query)):files;},[files,configSearch]);
  const isCfgFile=/\.cfg$/i.test(file);
  const structuredSettingCount=useMemo(()=>parseBepInExConfig(content).reduce((total,group)=>total+group.settings.length,0),[content]);
  const canUseStructuredMode=isCfgFile&&structuredSettingCount>0;
  const showStructured=editorMode==='form'&&canUseStructuredMode;
  useEffect(()=>{localStorage.setItem('dss-bepinex-editor-mode',editorMode);},[editorMode]);
  const reviewChanges=useMemo(()=>groupReviewChanges(changes,saved,content),[changes.size,saved,content]);
  const reviewFieldChanges=useMemo(()=>/\.cfg$/i.test(file)?configReviewFieldChanges(saved,content):[],[file,saved,content]);
   return <div className={`config-workspace ${accessReadOnly?'profile-view-controls':''}`}>
     <PageHeading eyebrow="GERENCIAMENTO DO PERFIL" title="Arquivos do BepInEx" description={accessReadOnly?"Consulte os arquivos de configuração do perfil.":"Edite arquivos de configuração do servidor. As preferências do DSS ficam em Configurações."} actions={<Button className={accessReadOnly?'profile-view-control':''} onClick={loadFiles} disabled={loading}><RefreshCw size={15} className={loading?'spin':''}/> Atualizar lista</Button>}/>
     {accessReadOnly?<div className="notice">Acesso somente para visualização. Você pode consultar os arquivos e alternar entre texto e campos, sem alterar configurações.</div>:readOnly&&<div className="notice">O servidor está em execução. Os arquivos podem ser consultados, mas ficam em modo de leitura até o servidor ser parado.</div>}
    <div className={`config-grid ${filesCollapsed?'files-collapsed':''}`}>
      <aside className="panel-card config-file-panel">
        <div className="config-file-panel-head"><div><h2>BepInEx/config</h2>{!filesCollapsed&&<p>Arquivos encontrados neste perfil</p>}</div><Button aria-label={filesCollapsed?'Expandir lista de arquivos':'Recolher lista de arquivos'} title={filesCollapsed?'Expandir arquivos':'Recolher arquivos'} onClick={()=>setFilesCollapsed(value=>!value)}>{filesCollapsed?<ChevronRight size={16}/>:<ChevronLeft size={16}/>}</Button></div>
        {!filesCollapsed&&<>
          <div className="config-file-tools"><label className="config-file-search"><Search size={15}/><input className="field" value={configSearch} onChange={event=>setConfigSearch(event.target.value)} placeholder="Buscar configs…" aria-label="Buscar arquivos de configuração"/></label><Button className="config-bulk-delete-trigger" aria-label="Abrir exclusão em massa de configurações" onClick={()=>{setBulkSelected(new Set());setBulkConfigSearch('');setBulkDeleteOpen(true);}} disabled={readOnly||bulkConfigFiles.length===0} title={readOnly?'Pare o servidor para excluir configurações':'Excluir arquivos de configuração em lote'}><Trash2 size={15}/><span>Excluir configurações</span><small>{bulkConfigFiles.length}</small></Button></div>
          <div className="config-file-list">{loading?<Spinner label="Carregando arquivos de configuração…"/>:filteredConfigFiles.length?filteredConfigFiles.map(name=>{const orphan=configFileIsOrphan(name,installedMods);return <div key={name} className={`config-file-row ${file===name?'active':''} ${orphan?'config-file-orphan':''}`}><button type="button" className={`config-file ${file===name?'active':''}`} onClick={()=>openFile(name)} title={name} aria-label={`Abrir ${name}`}><FileCog size={15}/><span className="config-file-label"><span className="config-file-name">{name.split(/([._-])/).map((part,index)=><React.Fragment key={index}>{part}{/[._-]/.test(part)&&<wbr/>}</React.Fragment>)}</span>{orphan&&<i className="config-orphan-badge" title="Nenhum mod instalado no perfil corresponde a este arquivo">Mod não instalado</i>}</span>{isDraftDirty(name)&&<i className="dirty-dot" title="Alterações não salvas"/>}</button><button type="button" className="config-file-delete" onClick={()=>deleteConfig(name)} disabled={readOnly} title={readOnly?'Pare o servidor para excluir o arquivo':'Excluir arquivo de configuração'} aria-label={`Excluir ${name}`}><Trash2 size={15}/></button></div>;}) : files.length?<Empty title="Nenhum arquivo corresponde à busca" description="Tente outro nome ou limpe o campo de busca."/>:<Empty title="Nenhum arquivo encontrado" description="Configure o caminho do servidor e execute o BepInEx uma vez para gerar os arquivos de configuração."/>}</div>
        </>}
      </aside>
      <section className="panel-card config-editor-panel">
        {!file?<div className="config-editor-empty">{loading?<Spinner label="Lendo arquivos do BepInEx…"/>:<Empty title="Selecione um arquivo" description={files.length?'Escolha um arquivo à esquerda para abrir no editor.':'Os arquivos aparecerão aqui quando o BepInEx gerar as configurações deste perfil.'}/>}</div>:<>
           <div className="panel-head"><div><h2>{file}</h2><p>{accessReadOnly?<span>Somente leitura</span>:dirty?<span className="dirty-label">● Alterações não salvas</span>:'Uma revisão é criada ao salvar.'} · {markerRows} linhas · {changes.size} trecho(s) alterado(s){readOnly&&!accessReadOnly?' · Somente leitura':''}</p></div><div className="heading-actions"><div className={`bepinex-editor-mode ${showStructured?'form-active':''}`} title={canUseStructuredMode?'Alternar entre texto e campos':'O modo estruturado está disponível para arquivos .cfg reconhecidos'}><Code2 size={16}/><label className="dss-switch"><input type="checkbox" role="switch" aria-label="Alternar entre editor de texto e campos" checked={showStructured} disabled={!canUseStructuredMode} onChange={event=>setEditorMode(event.target.checked?'form':'text')}/><i/></label><SlidersHorizontal size={16}/></div><LongPressButton className="discard-hold-button" durationMs={5000} title="Segure por 5 segundos para descartar as alterações" onLongPress={discard} disabled={!dirty||readOnly}>Descartar</LongPressButton><Button variant="primary" onClick={()=>setSaveReviewOpen(true)} disabled={!dirty||readOnly}>Revisar e salvar</Button></div></div>
          {!showStructured&&<div className="find-replace"><label>Localizar<input id="cfg-find" className="field" value={find} onChange={event=>setFind(event.target.value)} placeholder="Ctrl + F" onKeyDown={event=>{if(event.key==='Enter'){event.preventDefault();findNext();}}}/></label><label>Substituir por<input id="cfg-replace" className="field" value={replace} onChange={event=>setReplace(event.target.value)} placeholder="Ctrl + R"/></label><div><Button onClick={findNext}>Próxima</Button><Button onClick={replaceOne} disabled={!find||readOnly}>Substituir</Button><Button onClick={replaceAll} disabled={!find||readOnly}>Substituir tudo</Button></div></div>}
          <div className="editor-status"><span>{showStructured?`${structuredSettingCount} configuração(ões) · ${changes.size} trecho(s) alterado(s)`:`${markerRows} linhas · ${changes.size} trecho(s) alterado(s)`}</span><span>{showStructured?'Os campos alteram o conteúdo original do arquivo.':hint||'Clique no ponto amarelo para comparar antes/depois.'}</span></div>
          {showStructured?<BepInExStructuredEditor content={content} onChange={updateContent} readOnly={readOnly}/>:<div className="editor-frame">
            <div className="line-gutter" ref={gutter} aria-hidden="true">{Array.from({length:markerRows},(_,index)=>{const change=changes.get(index);return <div key={index}><span>{index+1}</span>{change?<button type="button" tabIndex={-1} title={`Comparar alterações na linha ${index+1}`} aria-label={`Comparar alterações na linha ${index+1}`} onClick={event=>openDiff(event,change)}>●</button>:<span/>}</div>;})}</div>
            <div className="editor-codepane"><pre className="syntax-preview" ref={highlight} aria-hidden="true" dangerouslySetInnerHTML={{__html:highlightIni(content)}}/><textarea ref={textarea} className="config-textarea" value={content} onChange={event=>updateContent(event.target.value)} onScroll={syncScroll} spellCheck={false} readOnly={readOnly} aria-label={`Editor de ${file}`} /></div>
            {diffLine&&diffPreview&&<div ref={diffPopover} className="editor-line-diff-popover" style={diffAnchor?{left:diffAnchor.left,top:diffAnchor.top,width:diffAnchor.width,maxHeight:diffAnchor.maxHeight}:undefined}><div className="modal-head"><div><h2>Comparação do trecho</h2><p>{file} · antes e depois da última versão salva</p></div><button className="icon-btn" type="button" onClick={closeDiff} aria-label="Fechar comparação"><X size={18}/></button></div><div className="editor-diff-content"><HistoryCompare before={diffPreview.before} after={diffPreview.after} beforeOffset={diffPreview.beforeOffset} afterOffset={diffPreview.afterOffset}/></div><div className="modal-actions"><Button onClick={closeDiff}>Fechar</Button><Button variant="primary" onClick={rollbackLine} disabled={readOnly}>↶ Reverter este trecho</Button></div></div>}
          </div>}
          <small className="helper-text">{accessReadOnly?'Este acesso permite consultar o conteúdo; alterações, exclusões e salvamento estão desativados.':readOnly?'O servidor precisa estar parado para salvar alterações.':showStructured?'Edite as opções nos campos; a revisão mantém o conteúdo completo do arquivo.':'Clique no indicador amarelo da linha para revisar e restaurar o trecho alterado.'}</small>
        </>}
      </section>
    </div>
    {saveReviewOpen&&<Modal title="Revisar alterações do arquivo" description={`${file} · ${changes.size} linha(s) alterada(s) em ${reviewChanges.length} trecho(s). Confira o conteúdo antes de salvar e reverta qualquer trecho.`} close={()=>setSaveReviewOpen(false)} className="config-save-review-modal"><div className="save-review-toolbar"><span><b>Visualização</b><small>{reviewMode==='fields'?'Valores reconhecidos como campos de configuração.':'Linhas originais e alteradas, lado a lado.'}</small></span><div role="group" aria-label="Modo de visualização da revisão"><button type="button" className={`save-review-mode-button ${reviewMode==='text'?'active':''}`} aria-pressed={reviewMode==='text'} onClick={()=>setReviewMode('text')}><Code2 size={15}/> Texto</button><button type="button" className={`save-review-mode-button ${reviewMode==='fields'?'active':''}`} aria-pressed={reviewMode==='fields'} disabled={!reviewFieldChanges.length} onClick={()=>setReviewMode('fields')} title={reviewFieldChanges.length?'Visualizar valores como campos':'Este arquivo não tem campos reconhecidos alterados'}><SlidersHorizontal size={15}/> Campos</button></div></div>{reviewMode==='fields'&&reviewFieldChanges.length?<><SaveReviewFields changes={reviewFieldChanges}/><p className="save-review-fields-note">Esta visualização mostra as opções reconhecidas do arquivo. Use Texto para conferir comentários, formatação ou outras linhas.</p></>:<div className="save-review-list">{reviewChanges.length?reviewChanges.map((change,index)=><section className="save-review-hunk" key={`${change.beforeIndex}-${change.afterIndex}-${index}`}><header><div><b>Trecho {index+1}</b><span>Antes: linhas {change.beforeIndex+1}–{change.beforeIndex+change.beforeLines.length} · Depois: linhas {change.afterIndex+1}–{change.afterIndex+change.afterLines.length}</span></div><Button size="small" onClick={()=>revertDraftChange(change)}>↶ Reverter estas linhas</Button></header><HistoryCompare before={change.before} after={change.after||''} beforeOffset={change.beforeIndex} afterOffset={change.afterIndex}/></section>):<Empty title="Todas as linhas foram revertidas" description="Não há alterações pendentes para salvar."/>}</div>}<div className="modal-actions"><Button onClick={()=>setSaveReviewOpen(false)}>Continuar editando</Button><Button variant="primary" onClick={save} disabled={!dirty||saving}>{saving?'Salvando…':'Confirmar e salvar'}</Button></div></Modal>}
    {bulkDeleteOpen&&<Modal title="Excluir configurações" description={`${bulkConfigFiles.length} arquivo(s) .cfg neste perfil. A lista indica se há um mod correspondente instalado.`} close={()=>!deletingBulk&&setBulkDeleteOpen(false)} className="bepinex-bulk-delete-modal">
      <label className="bulk-config-search"><Search size={16}/><input className="field" value={bulkConfigSearch} onChange={event=>setBulkConfigSearch(event.target.value)} placeholder="Buscar por nome do arquivo…" aria-label="Buscar configurações para excluir"/><span>{filteredBulkConfigFiles.length} de {bulkConfigFiles.length}</span></label>
      <div className="bulk-config-toolbar"><span><b>{bulkSelected.size}</b> selecionada(s)</span><label className="bulk-config-orphan-toggle"><input className="dss-checkbox" type="checkbox" checked={hideInstalledBulkConfigs} disabled={deletingBulk} onChange={event=>setHideInstalledBulkConfigs(event.target.checked)}/><span>Ocultar mods instalados</span></label><div><Button size="small" onClick={()=>setBulkSelected(current=>{const allSelected=uninstalledBulkConfigFiles.length>0&&uninstalledBulkConfigFiles.every(name=>current.has(name));const next=new Set(current);for(const name of uninstalledBulkConfigFiles){if(allSelected)next.delete(name);else next.add(name);}return next;})} disabled={!uninstalledBulkConfigFiles.length||deletingBulk}>{uninstalledBulkConfigFiles.length>0&&uninstalledBulkConfigFiles.every(name=>bulkSelected.has(name))?'Desmarcar':'Selecionar'} configs sem mod instalado ({uninstalledBulkConfigFiles.length})</Button><Button size="small" onClick={()=>setBulkSelected(current=>new Set([...current,...filteredBulkConfigFiles]))} disabled={!filteredBulkConfigFiles.length||deletingBulk}>Selecionar exibidas</Button><Button size="small" onClick={()=>setBulkSelected(new Set())} disabled={!bulkSelected.size||deletingBulk}>Limpar</Button></div></div>
      <div className="bulk-config-list">{filteredBulkConfigFiles.length?filteredBulkConfigFiles.map(name=>{const owners=configOwnersForFile(name,installedMods);const installed=owners.length>0;return <label key={name} className="bulk-config-item"><input className="dss-checkbox" type="checkbox" checked={bulkSelected.has(name)} disabled={deletingBulk} onChange={event=>toggleBulkConfig(name,event.target.checked)}/><FileCog size={16}/><span><b>{name}</b><small>{installed?`Mod instalado · ${owners.map(mod=>mod.name||mod.packageId).join(' · ')}`:'Mod não instalado no perfil'}</small></span><i className={`bulk-config-state ${installed?'installed':'missing'}`}>{installed?'Instalado':'Não instalado'}</i>{isDraftDirty(name)&&<i className="dirty-dot" title="Este arquivo tem alterações locais não salvas"/>}</label>;}) : <Empty title={bulkConfigFiles.length?'Nenhuma configuração corresponde à busca':'Nenhuma configuração .cfg encontrada'} description={bulkConfigFiles.length?'Tente outro nome ou limpe a busca.':'Os arquivos .cfg encontrados aparecerão aqui.'}/>}</div>
      <div className="notice bulk-config-warning"><ShieldAlert size={16}/><span>A exclusão remove os arquivos selecionados do disco. O BepInEx poderá criá-los novamente ao iniciar o servidor. Segure o botão por 5 segundos para confirmar.</span></div>
      <div className="modal-actions"><Button onClick={()=>setBulkDeleteOpen(false)} disabled={deletingBulk}>Cancelar</Button><LongPressButton className="bulk-config-delete-button" variant="danger" durationMs={5000} title="Segure por 5 segundos para excluir as configurações selecionadas" onLongPress={deleteSelectedConfigs} disabled={!bulkSelected.size||readOnly||deletingBulk}><span className="bulk-config-delete-content">{deletingBulk?<><LoaderCircle size={16} className="spin"/><span>Excluindo configurações…</span></>:<><Trash2 size={16}/><span>Segure 5 s para excluir {bulkSelected.size} config(s)</span></>}</span></LongPressButton></div>
    </Modal>}
  </div>;
}

function configDiffRows(beforeText, afterText) {
  const before=String(beforeText??'').split('\n'),after=String(afterText??'').split('\n');
  const operations=[];
  if((before.length+1)*(after.length+1)>4_000_000){
    let i=0;while(i<Math.max(before.length,after.length)){if(before[i]===after[i]){operations.push({kind:'context',oldLine:before[i],newLine:after[i],oldNumber:i+1,newNumber:i+1});i++;continue;}let end=i;while(end<Math.max(before.length,after.length)&&before[end]!==after[end])end++;const removed=before.slice(i,end),added=after.slice(i,end),paired=Math.min(removed.length,added.length);for(let k=0;k<paired;k++)operations.push({kind:'changed',oldLine:removed[k],newLine:added[k],oldNumber:i+k+1,newNumber:i+k+1});for(let k=paired;k<removed.length;k++)operations.push({kind:'removed',oldLine:removed[k],newLine:null,oldNumber:i+k+1,newNumber:null});for(let k=paired;k<added.length;k++)operations.push({kind:'added',oldLine:null,newLine:added[k],oldNumber:null,newNumber:i+k+1});i=end;}
  } else {
    const width=after.length+1,dp=new Uint32Array((before.length+1)*width);
    for(let i=before.length-1;i>=0;i--)for(let j=after.length-1;j>=0;j--){const at=i*width+j;dp[at]=before[i]===after[j]?1+dp[(i+1)*width+j+1]:Math.max(dp[(i+1)*width+j],dp[at+1]);}
    let i=0,j=0;while(i<before.length||j<after.length){if(i<before.length&&j<after.length&&before[i]===after[j]){operations.push({kind:'context',oldLine:before[i],newLine:after[j],oldNumber:i+1,newNumber:j+1});i++;j++;continue;}const oldStart=i,newStart=j;while(i<before.length||j<after.length){if(i<before.length&&j<after.length&&before[i]===after[j])break;if(i<before.length&&(j>=after.length||dp[(i+1)*width+j]>=dp[i*width+j+1]))i++;else if(j<after.length)j++;}const removed=before.slice(oldStart,i),added=after.slice(newStart,j),paired=Math.min(removed.length,added.length);for(let k=0;k<paired;k++)operations.push({kind:'changed',oldLine:removed[k],newLine:added[k],oldNumber:oldStart+k+1,newNumber:newStart+k+1});for(let k=paired;k<removed.length;k++)operations.push({kind:'removed',oldLine:removed[k],newLine:null,oldNumber:oldStart+k+1,newNumber:null});for(let k=paired;k<added.length;k++)operations.push({kind:'added',oldLine:null,newLine:added[k],oldNumber:null,newNumber:newStart+k+1});}
  }
  const changes=operations.flatMap((row,index)=>row.kind==='context'?[]:[index]);if(!changes.length)return [];
  const visible=new Set();for(const at of changes)for(let i=Math.max(0,at-3);i<=Math.min(operations.length-1,at+3);i++)visible.add(i);
  const rows=[];let hidden=0;for(let i=0;i<operations.length;i++){if(operations[i].kind==='context'&&!visible.has(i)){hidden++;continue;}if(hidden){rows.push({kind:'gap',count:hidden});hidden=0;}rows.push(operations[i]);}if(hidden)rows.push({kind:'gap',count:hidden});return rows;
}
function HistoryCompare({ before, after, beforeOffset=0, afterOffset=0 }) {
  const rows=configDiffRows(before,after);
  return <div className="line-diff-view"><div className="line-diff-head"><span>Antes</span><span>Depois</span></div>{rows.length?rows.map((row,index)=>row.kind==='gap'?<div key={`gap-${index}`} className="line-diff-gap">⋯ {row.count} linha(s) de contexto ocultas ⋯</div>:<div key={`${row.oldNumber}-${row.newNumber}-${index}`} className={`line-diff-row ${row.kind}`}><code><i>{row.oldNumber==null?'':row.oldNumber+beforeOffset}</i><b className="line-diff-sign">{row.kind==='context'?'':row.oldLine==null?'':'−'}</b><span dangerouslySetInnerHTML={{__html:row.oldLine==null?'':markedLine(row.oldLine,row.newLine,'removed')}}/></code><code><i>{row.newNumber==null?'':row.newNumber+afterOffset}</i><b className="line-diff-sign">{row.kind==='context'?'':row.newLine==null?'':'+'}</b><span dangerouslySetInnerHTML={{__html:row.newLine==null?'':markedLine(row.newLine,row.oldLine,'added')}}/></code></div>):<div className="line-diff-gap">Nenhuma diferença de linha.</div>}</div>;
}

const PROFILE_HISTORY_TYPES = [
  ['all', 'Todos os tipos'],
  ['server-profile', 'Configurações do perfil'],
  ['profile-image', 'Imagem do perfil'],
  ['profile-action', 'Ações do servidor'],
  ['mod-package', 'Mods'],
  ['bepinex-file', 'Arquivos BepInEx']
];

function RevisionActor({ row }) {
  return <span className="revision-actor">{row.actorImageUrl ? <img src={row.actorImageUrl} alt=""/> : <UserRound size={13}/>}<span>{row.actorName || 'Autor não registrado'}</span></span>;
}

function ProfileHistory({ profile, canEditProfile = true }) {
  const [rows,setRows]=useState(null),[logToggles,setLogToggles]=useState(false),[detail,setDetail]=useState(null),[filter,setFilter]=useState(''),[typeFilter,setTypeFilter]=useState('all'),[pageSize,setPageSize]=useState(10),[page,setPage]=useState(1),[onlineMods,setOnlineMods]=useState({});
  const toast=useToast(),confirm=useConfirm();
  const refresh=useCallback(()=>api(`/api/instances/${profile.id}/history`).then(r=>{setRows(r.revisions||[]);setLogToggles(Boolean(r.logModToggleActions));}).catch(e=>toast(e.message,'error')),[profile.id]);
  useEffect(()=>{refresh();},[refresh]);
  useEffect(()=>{setPage(1);},[filter,typeFilter,pageSize]);
  const filteredRows=useMemo(()=>{
    const query=filter.trim().toLocaleLowerCase();
    return (rows||[]).filter(row=>{
      const matchesType=typeFilter==='all'||row.kind===typeFilter;
      const matchesQuery=!query||`${row.summary||''} ${row.target||''} ${(row.modToggleNames||[]).join(' ')} ${row.kind||''} ${fmtDate(row.createdAt)||''}`.toLocaleLowerCase().includes(query);
      return matchesType&&matchesQuery;
    });
  },[rows,filter,typeFilter]);
  const pageCount=Math.max(1,Math.ceil(filteredRows.length/pageSize));
  useEffect(()=>{setPage(current=>Math.min(current,pageCount));},[pageCount]);
  const pageRows=filteredRows.slice((page-1)*pageSize,page*pageSize);
  const visibleModRows=pageRows.filter(row=>row.kind==='mod-package'&&row.modProvider&&row.modPackageId);
  const visibleModKey=visibleModRows.map(row=>`${row.modProvider}:${row.modPackageId}`).join('|');
  useEffect(()=>{
    if(!visibleModKey)return;
    let cancelled=false;
    const groups=new Map();
    for(const row of visibleModRows){const provider=String(row.modProvider).toLowerCase();if(!groups.has(provider))groups.set(provider,new Set());groups.get(provider).add(row.modPackageId);}
    Promise.all([...groups].map(async([provider,ids])=>{
      const query=new URLSearchParams({ids:[...ids].join(',')});
      try{return [provider,(await api(`/api/catalog/details/${encodeURIComponent(provider)}?${query}`,{cache:'no-store',suppressFailureToast:true})).items||[],[...ids]];}
      catch{return [provider,[],[...ids]];}
    })).then(results=>{
      if(cancelled)return;
      setOnlineMods(current=>{const next={...current};for(const[provider,items,ids]of results)for(const id of ids)next[`${provider}:${id}`]=items.find(item=>String(item.packageId).toLowerCase()===String(id).toLowerCase())||null;return next;});
    });
    return()=>{cancelled=true;};
  },[visibleModKey]);
  async function open(row){try{const d=await api(`/api/instances/${profile.id}/history/${row.id}`);setDetail(d);}catch(e){toast(e.message,'error');}}
  async function restore(){if(!detail)return;if(!await confirm({title:'Restaurar revisão?',description:'O DSS verificará se o estado atual ainda corresponde ao trecho editado antes de restaurá-lo.',confirmLabel:'Restaurar linhas'}))return;try{const result=await api(`/api/instances/${profile.id}/history/${detail.id}/restore-lines`,{method:'POST',body:'{}'});toast(result.message||'Linhas restauradas.');setDetail(null);refresh();}catch(e){toast(e.message,'error');}}
  return <Card className={canEditProfile?'':'profile-view-controls'}>
    <div className="panel-head history-head"><div><h2>Atividade do perfil</h2><p>Ações e alterações vinculadas somente a este perfil.</p></div>{canEditProfile&&<label className="history-toggle"><span>Registrar ativações e inativações de mods<small>Desativado por padrão.</small></span><input type="checkbox" checked={logToggles} onChange={async e=>{const value=e.target.checked;setLogToggles(value);try{await api(`/api/instances/${profile.id}/history/settings`,{method:'PUT',body:json({logModToggleActions:value})});}catch(err){toast(err.message,'error');setLogToggles(!value);}}}/></label>}</div>
    {rows===null?<Spinner label="Carregando histórico do perfil…"/>:rows.length?<>
      <div className="history-controls">
        <label className="search-input"><Search size={16}/><input className="field" value={filter} onChange={event=>setFilter(event.target.value)} placeholder="Filtrar por ação, arquivo ou destino…" aria-label="Filtrar histórico"/>{filter&&<button type="button" aria-label="Limpar filtro" onClick={()=>setFilter('')}><X size={15}/></button>}</label>
        <label className="history-page-size">Tipo de alteração<select className="field" value={typeFilter} onChange={event=>setTypeFilter(event.target.value)} aria-label="Filtrar por tipo de alteração">{PROFILE_HISTORY_TYPES.map(([value,label])=><option key={value} value={value}>{label}</option>)}</select></label>
        <label className="history-page-size">Resultados por página<select className="field" value={pageSize} onChange={event=>setPageSize(Number(event.target.value))}><option value={10}>10</option><option value={20}>20</option><option value={50}>50</option><option value={100}>100</option></select></label>
      </div>
      {pageRows.length?<div className="revision-list">{pageRows.map(row=>{
        if(row.modToggleNames?.length){
          const enabled=row.modToggleEnabled??String(row.summary||'').toLowerCase().includes('ativado');
          return <button className="revision-item mod-toggle-batch-entry" key={row.id} onClick={()=>open(row)}><span className="revision-icon"><Package size={17}/></span><span className="revision-main"><b>{row.summary||(enabled?'Mods ativados em lote':'Mods desativados em lote')}</b><small>{fmtDate(row.createdAt)} · {row.modToggleNames.length} {row.modToggleNames.length===1?'mod':'mods'} no lote</small><RevisionActor row={row}/></span><span className="revision-hint">Ver mods ↗</span></button>;
        }
        if(row.kind==='mod-package'&&(row.modProvider||row.modPackageId)){
          const key=`${String(row.modProvider||'').toLowerCase()}:${row.modPackageId||''}`;
          const online=onlineMods[key];
          const filesInstalled=row.filesInstalled||[],filesRemoved=row.filesRemoved||[],filesPreserved=row.filesPreserved||[];
          return <article className="revision-item mod-history-item" key={row.id}>
            <span className="revision-icon mod-history-icon">{online?.iconUrl||row.modIconUrl?<img src={online?.iconUrl||row.modIconUrl} alt="" loading="lazy"/>:<Package size={17}/>}</span>
            <div className="revision-main mod-history-main">
              <div className="mod-history-heading"><span><b>{online?.name||row.modName||row.target}</b><small>{providerNames[row.modProvider]||row.modProvider} · {online?.owner||'Mod do catálogo'}{(online?.version||row.modVersion)?` · v${online?.version||row.modVersion}`:''}</small></span><button type="button" className="button small" disabled={!row.modPackageId} onClick={()=>window.dispatchEvent(new CustomEvent('dss-open-mod-details',{detail:{mods:[{provider:row.modProvider,packageId:row.modPackageId,name:online?.name||row.modName||row.target}]}}))}><ExternalLink size={13}/> Ver mod online</button></div>
              {online?.description&&<p className="mod-history-description">{online.description}</p>}
              {online===undefined&&<small className="mod-history-loading">Carregando dados atuais do provedor…</small>}{online===null&&<small className="mod-history-loading">O provedor não disponibilizou os dados atuais deste mod.</small>}
              <div className="mod-history-files">
                {filesInstalled.length>0&&<div><b>Instalados <small>({filesInstalled.length})</small></b><ul>{filesInstalled.map((file,index)=><li key={`${file}-${index}`}><code>{file}</code></li>)}</ul></div>}
                {filesRemoved.length>0&&<div className="removed"><b>Removidos <small>({filesRemoved.length})</small></b><ul>{filesRemoved.map((file,index)=><li key={`${file}-${index}`}><code>{file}</code></li>)}</ul></div>}
                {filesPreserved.length>0&&<div className="preserved"><b>Preservados por terem sido alterados <small>({filesPreserved.length})</small></b><ul>{filesPreserved.map((file,index)=><li key={`${file}-${index}`}><code>{file}</code></li>)}</ul></div>}
                {!filesInstalled.length&&!filesRemoved.length&&!filesPreserved.length&&<small>Nenhum arquivo gerenciado foi alterado nesta ação.</small>}
              </div>
              <small className="mod-history-date">{row.summary} · {fmtDate(row.createdAt)} · {row.target}</small><RevisionActor row={row}/>
            </div>
          </article>;
        }
        return <button className="revision-item" key={row.id} onClick={()=>open(row)}><span className="revision-icon"><Clock3 size={17}/></span><span className="revision-main"><b>{row.summary}</b><small>{fmtDate(row.createdAt)} · {row.kind==='bepinex-file'?`BepInEx/${row.target}`:row.target}</small><RevisionActor row={row}/></span><span className="revision-hint">Ver antes e depois ↗</span></button>;
      })}</div>:<Empty title="Nenhuma ação corresponde aos filtros" description="Altere o tipo, tente outro termo ou limpe a busca para ver o histórico."/>}
      <div className="pagination history-pagination"><span>{filteredRows.length?`${(page-1)*pageSize+1}–${Math.min(page*pageSize,filteredRows.length)} de ${filteredRows.length} registros`:'0 registros'} · página {page} de {pageCount}</span><div><Button disabled={page<=1} onClick={()=>setPage(current=>Math.max(1,current-1))}><ArrowLeft size={14}/> Anterior</Button><Button disabled={page>=pageCount} onClick={()=>setPage(current=>Math.min(pageCount,current+1))}>Próxima <ArrowRight size={14}/></Button></div></div>
    </>:<Empty title="Nenhuma ação registrada" description="Alterações de configuração, mods, arquivos BepInEx e atividades do servidor aparecerão aqui."/>}
    {detail&&<Modal title={detail.modToggleNames?.length?(detail.modToggleEnabled?'Mods ativados em lote':'Mods desativados em lote'):'Detalhes da alteração'} description={`${detail.target} · ${fmtDate(detail.createdAt)}`} close={()=>setDetail(null)} className={`history-detail-modal ${detail.modToggleNames?.length?'mod-toggle-batch-modal':''}`}><RevisionActor row={detail}/>{detail.modToggleNames?.length?<section className="mod-toggle-batch-detail"><header><span className={`mod-toggle-batch-mark ${detail.modToggleEnabled?'enabled':'disabled'}`}><Package size={19}/></span><span><b>{detail.modToggleNames.length} {detail.modToggleNames.length===1?'mod':'mods'} no lote</b><small>{detail.modToggleEnabled?'Todos foram ativados nesta ação.':'Todos foram desativados nesta ação.'}</small></span><span className={`state-badge ${detail.modToggleEnabled?'running':'stopped'}`}>{detail.modToggleEnabled?'ATIVADOS':'DESATIVADOS'}</span></header><div className="mod-toggle-batch-list">{detail.modToggleNames.map((name,index)=><div className="mod-toggle-batch-mod" key={`${name}-${index}`}><span>{String(index+1).padStart(2,'0')}</span><Package size={15}/><b>{name}</b></div>)}</div></section>:detail.kind==='bepinex-file'?<HistoryCompare before={detail.before} after={detail.after}/>:<div className="history-diff"><section><h3>Antes</h3><pre>{detail.before||'(vazio)'}</pre></section><section><h3>Depois</h3><pre>{detail.after||'(vazio)'}</pre></section></div>}<div className="modal-actions">{canEditProfile&&detail.kind==='bepinex-file'&&detail.canRollback&&<Button variant="primary" onClick={restore}>↶ Reverter linhas deste arquivo</Button>}<Button onClick={()=>setDetail(null)}>Fechar</Button></div></Modal>}
  </Card>;
}
function GlobalHistoryPage() {
  const [rows,setRows]=useState(null),[filter,setFilter]=useState(''),[pageSize,setPageSize]=useState(10),[page,setPage]=useState(1);
  const {auth}=useDss();
  const toast=useToast(),confirm=useConfirm();
  const refresh=useCallback(()=>api('/api/history').then(r=>setRows(r.revisions||[])).catch(e=>toast(e.message,'error')),[toast]);
  useEffect(()=>{refresh();},[refresh]);
  useEffect(()=>{setPage(1);},[filter,pageSize]);
  const filteredRows=useMemo(()=>{const query=filter.trim().toLocaleLowerCase();if(!query)return rows||[];return (rows||[]).filter(row=>`${row.summary||''} ${row.target||''} ${row.kind||''} ${row.actorName||''} ${fmtDate(row.createdAt)||''}`.toLocaleLowerCase().includes(query));},[rows,filter]);
  const pageCount=Math.max(1,Math.ceil(filteredRows.length/pageSize));
  useEffect(()=>{setPage(current=>Math.min(current,pageCount));},[pageCount]);
  const pageRows=filteredRows.slice((page-1)*pageSize,page*pageSize);
  async function clearHistory(){
    const accepted=await confirm({title:'Limpar todo o histórico?',description:'Esta ação apaga permanentemente as revisões do DSS e os históricos de todos os perfis neste computador.',confirmLabel:'Limpar histórico',cancelLabel:'Manter histórico',danger:true});
    if(!accepted)return;
    try{const result=await api('/api/history',{method:'DELETE',body:'{}'});setRows([]);setFilter('');setPage(1);toast(result.message||'Histórico limpo.');}
    catch(error){toast(error.message,'error');}
  }
  return <><PageHeading eyebrow="REVISÕES LOCAIS" title="Histórico do DSS" description="Ações gerais do estúdio e do host." actions={<><Button onClick={refresh}><RefreshCw size={15}/> Atualizar</Button>{auth.user?.role==='admin'&&<Button variant="danger" onClick={clearHistory} disabled={!rows?.length}><Trash2 size={15}/> Limpar histórico</Button>}</>}/><Card><div className="panel-head"><div><h2>Atividade do DSS</h2><p>A criação e importação de perfis também aparecem aqui. Alterações de servidor ficam no histórico de cada perfil.</p></div></div>{rows===null?<Spinner label="Carregando histórico…"/>:rows.length?<><div className="history-controls"><label className="search-input"><Search size={16}/><input className="field" value={filter} onChange={event=>setFilter(event.target.value)} placeholder="Filtrar por ação, arquivo ou destino…" aria-label="Filtrar histórico do DSS"/>{filter&&<button type="button" aria-label="Limpar filtro" onClick={()=>setFilter('')}><X size={15}/></button>}</label><label className="history-page-size">Resultados por página<select className="field" value={pageSize} onChange={event=>setPageSize(Number(event.target.value))}><option value={10}>10</option><option value={20}>20</option><option value={50}>50</option><option value={100}>100</option></select></label></div>{pageRows.length?<div className="revision-list">{pageRows.map(row=><article className="revision-item" key={row.id}><span className="revision-icon"><FileClock size={17}/></span><span className="revision-main"><b>{row.summary}</b><small>{fmtDate(row.createdAt)} · {row.target}</small><RevisionActor row={row}/></span></article>)}</div>:<Empty title="Nenhuma ação corresponde ao filtro" description="Tente outro termo ou limpe a busca para ver todo o histórico."/>}<div className="pagination history-pagination"><span>{filteredRows.length?`${(page-1)*pageSize+1}–${Math.min(page*pageSize,filteredRows.length)} de ${filteredRows.length} registros`:'0 registros'} · página {page} de {pageCount}</span><div><Button disabled={page<=1} onClick={()=>setPage(current=>Math.max(1,current-1))}><ArrowLeft size={14}/> Anterior</Button><Button disabled={page>=pageCount} onClick={()=>setPage(current=>Math.min(pageCount,current+1))}>Próxima <ArrowRight size={14}/></Button></div></div></>:<Empty title="Seu histórico começará com a primeira ação do DSS" description="Este histórico reúne atividades do estúdio. Ações e alterações de servidor ficam no histórico de cada perfil."/>}</Card></>;
}
function SettingsPage({ preferences = false }) {
  const { dashboard, language, setLanguage, setAuth, auth } = useDss();
  const [steam, setSteam] = useState(null);
  const [busy, setBusy] = useState(false);
  const [accessBusy, setAccessBusy] = useState(false);
  const [firewallBusy, setFirewallBusy] = useState(false);
  const [languageSaving, setLanguageSaving] = useState(false);
  const [panelFirewall, setPanelFirewall] = useState(null);
  const [accessMode, setAccessMode] = useState(dashboard?.listenMode || 'local');
  const toast = useToast();
  const confirm = useConfirm();

  useEffect(() => { api('/api/settings/steamcmd').then(setSteam).catch(error => toast(error.message, 'error')); }, []);
  useEffect(() => { if (dashboard?.listenMode) setAccessMode(dashboard.listenMode); }, [dashboard?.listenMode]);
  useEffect(() => { api('/api/settings/firewall').then(setPanelFirewall).catch(() => setPanelFirewall(null)); }, []);

  async function saveSteam(event) {
    event.preventDefault();
    setBusy(true);
    try {
      const directory = new FormData(event.currentTarget).get('directory');
      const result = await api('/api/settings/steamcmd', { method: 'PUT', body: json({ directory }) });
      setSteam(result);
      toast('Pasta do SteamCMD salva.');
    } catch (error) { toast(error.message, 'error'); }
    finally { setBusy(false); }
  }

  async function saveLanguage(event) {
    const next = event.target.value;
    if (languageSaving || next === language) return;
    setLanguageSaving(true);
    try {
      const result = await api('/api/settings/language', { method: 'PUT', body: json({ language: next }) });
      const savedLanguage = result.language || next;
      document.documentElement.lang = savedLanguage;
      setLanguage(savedLanguage);
      setAuth(current => current.user ? { ...current, user: { ...current.user, uiLanguage: savedLanguage } } : current);
      toast('Idioma atualizado.');
    }
    catch (error) { toast(error.message, 'error'); }
    finally { setLanguageSaving(false); }
  }

  async function changeAccessMode(nextMode) {
    if (accessBusy || nextMode === accessMode) return;
    let acknowledgePublicRisk = false;
    if (nextMode === 'public') {
      const accepted = await confirm({
        title: 'Habilitar acesso público ao painel?',
        description: 'O painel será acessível por outras interfaces de rede. Para chegar pela internet, ainda será necessário configurar o Firewall e o roteador.',
        content: <div className="public-access-risks"><b>Antes de continuar</b><ul><li>O tráfego atual usa HTTP, sem criptografia TLS.</li><li>Qualquer pessoa que alcance o endereço poderá tentar entrar; uma conta comprometida dá controle dos perfis e servidores.</li><li>Exposição pública aumenta tentativas automatizadas de senha, varreduras e risco de indisponibilidade.</li></ul><small>Prefira uma VPN. Para acesso direto, configure HTTPS em um proxy reverso e proteja o host.</small></div>,
        confirmLabel: 'Entendi, habilitar acesso público',
        cancelLabel: 'Manter protegido',
        danger: true
      });
      if (!accepted) return;
      acknowledgePublicRisk = true;
    }
    if (nextMode === 'local') {
      const accepted = await confirm({
        title: 'Voltar ao acesso local?',
        description: 'O painel reiniciará e aceitará conexões somente pelo próprio computador. Uma sessão aberta em outra máquina será desconectada.',
        confirmLabel: 'Voltar ao acesso local',
        cancelLabel: 'Cancelar'
      });
      if (!accepted) return;
    }
    setAccessBusy(true);
    try {
      const result = await api('/api/settings/access', { method: 'PUT', body: json({ listenMode: nextMode, acknowledgePublicRisk }) });
      setAccessMode(nextMode);
      toast(result.message || 'Modo de acesso salvo. O painel será reiniciado.', 'success', { durationMs: 10000 });
    } catch (error) { toast(error.message, 'error', { durationMs: 10000 }); }
    finally { setAccessBusy(false); }
  }

  async function changePanelFirewall(enabled) {
    if (firewallBusy || !panelFirewall) return;
    setFirewallBusy(true);
    try {
      const result = await api('/api/settings/firewall', { method: 'PUT', body: json({ enabled }) });
      setPanelFirewall(result);
      toast(result.message || (enabled ? 'Acesso local liberado no Firewall.' : 'Regra local removida do Firewall.'));
    } catch (error) {
      toast(error.message, 'error', { durationMs: 10000 });
      api('/api/settings/firewall').then(setPanelFirewall).catch(() => {});
    } finally { setFirewallBusy(false); }
  }

  const dataDirectory = dashboard?.dataDirectory || '%LOCALAPPDATA%\\DedicatedServerStudio';
  const logsDirectory = dashboard?.logsDirectory || '%DSS_FOLDER%\\logs';
  const isAdmin = auth?.user?.role === 'admin';
  const modeLabel = accessMode === 'public' ? 'ACESSO PÚBLICO' : accessMode === 'lan' ? 'REDE LOCAL' : 'SOMENTE NESTE COMPUTADOR';

  return <>
    <div className="settings-tabs"><Link className={!preferences ? 'active' : ''} to="/configuracoes">Configurações do DSS</Link><Link className={preferences ? 'active' : ''} to="/configuracoes/preferencias">Preferências</Link></div>
    {preferences ? <>
      <PageHeading eyebrow="PREFERÊNCIAS DO DSS" title="Preferências" description="Personalize a interface do Dedicated Server Studio."/>
      <Card className="language-card" aria-busy={languageSaving}><div><span className="eyebrow">IDIOMA DA INTERFACE</span><h2>Idioma da interface</h2><p>Idioma aplicado ao painel e salvo nas preferências do DSS.</p></div><label className="field-label">Idioma<select className="field user-personal-control" value={language} onChange={saveLanguage} disabled={languageSaving} translate="no"><option value="pt-BR">Português (Brasil)</option><option value="en-US">English</option><option value="es-ES">Español</option><option value="fr-FR">Français</option><option value="de-DE">Deutsch</option><option value="ru-RU">Русский</option><option value="zh-CN">简体中文</option></select>{languageSaving&&<small className="language-save-status"><LoaderCircle size={13} className="spin"/> Aplicando idioma…</small>}</label></Card>
    </> : <>
      <PageHeading eyebrow="CONFIGURAÇÕES DO DEDICATED SERVER STUDIO" title="Configurações do DSS" description="Ajustes do painel e do host que executa os servidores."/>
      <Card className="dss-settings-card">
        <div className="panel-head"><div><h2>Acesso ao painel</h2><p>Escolha de onde o DSS poderá ser aberto. A alteração é salva em <code>dss-config.json</code> e reinicia o painel.</p></div><span className={`state-badge ${accessMode === 'public' ? 'available' : accessMode === 'lan' ? 'running' : 'stopped'}`}>{modeLabel}</span></div>
        <div className="dss-access-options">
          <label className={`dss-access-option ${accessMode !== 'local' ? 'enabled' : ''}`}>
            <span className="dss-access-option-copy"><b>Acesso pela rede local</b><small>Permite abrir o painel em outros computadores conectados à mesma rede.</small></span>
            <span className="dss-switch"><input type="checkbox" role="switch" checked={accessMode !== 'local'} disabled={accessBusy || !isAdmin} onChange={event => changeAccessMode(event.target.checked ? 'lan' : 'local')}/><i aria-hidden="true"/></span>
          </label>
          <label className={`dss-access-option public ${accessMode === 'public' ? 'enabled' : ''}`}>
            <span className="dss-access-option-copy"><b>Acesso pela internet</b><small>Escuta em todas as interfaces. O acesso externo depende das regras do Firewall e do roteador.</small></span>
            <span className="dss-switch"><input type="checkbox" role="switch" checked={accessMode === 'public'} disabled={accessBusy || !isAdmin} onChange={event => changeAccessMode(event.target.checked ? 'public' : 'lan')}/><i aria-hidden="true"/></span>
          </label>
        </div>
        {accessMode === 'public' ? <div className="notice public-access-warning"><ShieldAlert size={17}/><span><b>O painel está em modo público.</b> O DSS não abre portas do roteador nem cria regra pública do Firewall. O endereço usa HTTP sem TLS; prefira uma VPN ou um proxy reverso com HTTPS.</span></div> : <div className="notice dss-access-note"><ShieldCheck size={17}/><span>{accessMode === 'lan' ? 'O painel aceita conexões de rede local. A senha do administrador continua obrigatória.' : 'O modo local é o mais restrito: somente este computador pode abrir o painel.'} Configure a senha de administrador antes de habilitar acesso remoto.</span></div>}
        {accessMode !== 'local' && <div className="notice dss-panel-address"><Network size={17}/><span><b>Endereço para abrir em outro computador</b><span>{dashboard?.panelAccessUrls?.length ? dashboard.panelAccessUrls.map(url => <code key={url}>{url}</code>) : 'Não foi possível detectar um endereço IPv4 ativo. Confira o IP deste computador com ipconfig.'}</span></span></div>}
        <label className={`dss-access-option dss-firewall-option ${panelFirewall?.enabled ? 'enabled' : ''}`}>
          <span className="dss-access-option-copy"><b>Liberação no Firewall do Windows</b><small>{panelFirewall?.enabled ? `Porta TCP ${panelFirewall.port} liberada somente para a sub-rede local.` : `Autoriza a porta TCP ${panelFirewall?.port || 5089} do painel na rede local. Disponível quando o DSS está como administrador.`}</small>{panelFirewall && !panelFirewall.isAdministrator && <small>Reinicie o painel com privilégios de administrador para alterar esta regra.</small>}</span>
          <span className="dss-switch"><input type="checkbox" role="switch" checked={Boolean(panelFirewall?.enabled)} disabled={!panelFirewall?.supported || !panelFirewall?.isAdministrator || firewallBusy || !isAdmin} onChange={event => changePanelFirewall(event.target.checked)}/><i aria-hidden="true"/></span>
        </label>
        {!isAdmin && <small className="helper-text">Somente o administrador da conta pode alterar o modo de acesso.</small>}
        <div className="settings-block"><h3>Arquivos de log</h3><p>Os registros do painel, do SteamCMD e dos consoles são salvos junto aos arquivos do DSS. Dados de conta, perfis, saves e mods continuam na pasta de dados do usuário.</p><code className="code-inline">{logsDirectory}</code><small className="helper-text">Painel: dss-*.log · SteamCMD: steamcmd\ · Servidores: servers\&lt;id&gt;\server.log. Um servidor que já estava em execução pode continuar no caminho antigo até ser reiniciado.</small></div>
        <div className="settings-block"><h3>Dados locais</h3><p>Configurações de conta, perfis e saves ficam fora da pasta do programa.</p><code className="code-inline">{dataDirectory}</code></div>
        <div className="settings-block"><h3>SteamCMD</h3><p>Por padrão, o DSS usa a pasta <code>steamcmd</code> ao lado do DSS. Este caminho é do host que executa o painel.</p><form onSubmit={saveSteam}><label className="field-label">Pasta de instalação no host<input className="field" name="directory" defaultValue={steam?.directory || ''} placeholder={steam?.defaultDirectory || 'C:\\caminho\\para\\steamcmd'}/><small>Informe a pasta absoluta que contém steamcmd.exe. Se necessário, o DSS baixa o SteamCMD.</small></label><div className="form-actions"><Button variant="primary" disabled={!steam || busy}>Salvar pasta</Button><Button type="button" onClick={async () => { try { const result = await api('/api/settings/steamcmd', { method: 'PUT', body: json({ directory: null }) }); setSteam(result); toast('Pasta padrão restaurada.'); } catch (error) { toast(error.message, 'error'); } }}>Usar pasta padrão do DSS</Button></div></form><p className="helper-text">{steam?.executableFound ? `steamcmd.exe encontrado · ${steam.source}.` : 'steamcmd.exe será baixado pelo DSS quando necessário.'}</p></div>
      </Card>
    </>}
  </>;
}
function IntegrationsPage() {
  const [providers,setProviders]=useState([]),[settings,setSettings]=useState({}),[keys,setKeys]=useState({}),[checking,setChecking]=useState(false),[busy,setBusy]=useState({}),[testResults,setTestResults]=useState({});
  const [nxmDownloadLink,setNxmDownloadLink]=useState('');
  const [nexusApplication,setNexusApplication]=useState(()=>localStorage.getItem('dss-nexus-application')||''),[nexusSsoBusy,setNexusSsoBusy]=useState(false);
  const toast=useToast(),nexusSsoRef=useRef(null);
  const load=useCallback(async()=>{setChecking(true);try{const [status,config]=await Promise.all([api('/api/providers/status'),api('/api/providers/settings')]);setProviders(status.providers||status||[]);setSettings(Object.fromEntries((config.providers||[]).map(item=>[item.id,item.configured])));}catch(e){toast(e.message,'error');}finally{setChecking(false);}},[toast]);
  useEffect(()=>{load();},[load]);
  useEffect(()=>()=>{const current=nexusSsoRef.current;if(current){clearTimeout(current.timer);current.ws.close();if(!current.popup.closed)current.popup.close();}},[]);
  async function saveKey(id,clear=false,providedKey=null){setBusy(current=>({...current,[id]:'save'}));try{const apiKey=providedKey??keys[id]??'';const result=await api(`/api/providers/${id}/settings`,{method:'PUT',body:json({apiKey:apiKey||null,clear})});toast(result.message);setKeys(current=>({...current,[id]:''}));await load();return true;}catch(e){toast(e.message,'error');return false;}finally{setBusy(current=>({...current,[id]:''}));}}
  async function testKey(id){setBusy(current=>({...current,[id]:'test'}));try{const result=await api(`/api/providers/${id}/test`,{method:'POST',body:'{}'});setTestResults(current=>({...current,[id]:result}));if(result.connectivity==='reachable')toast('Chave validada.');else toast(result.message,'error');}catch(e){toast(e.message,'error');}finally{setBusy(current=>({...current,[id]:''}));}}
  async function authorizeNexusDownload(event){event.preventDefault();setBusy(current=>({...current,nexusDownload:'save'}));try{const result=await api('/api/integrations/nexus/download-link',{method:'POST',body:json({link:nxmDownloadLink.trim()})});setNxmDownloadLink('');toast(`${result.message} Mod ${result.modId}, arquivo ${result.fileId}; válido até ${fmtDate(result.expiresAt)}.`);}catch(e){toast(e.message,'error');}finally{setBusy(current=>({...current,nexusDownload:''}));}}
  function finishNexusSso(message){const current=nexusSsoRef.current;if(!current)return;nexusSsoRef.current=null;clearTimeout(current.timer);try{current.ws.close();}catch{}try{if(!current.popup.closed)current.popup.close();}catch{}setNexusSsoBusy(false);if(message)toast(message,'error');}
  function startNexusSso(){
    const application=nexusApplication.trim();if(!application)return toast('Informe a referência de aplicativo Nexus aprovada antes de iniciar o login.','error');
    const popup=window.open('about:blank','dss-nexus-sso','popup,width=650,height=760');if(!popup)return toast('O navegador bloqueou a janela de login. Permita pop-ups para o DSS e tente novamente.','error');
    popup.opener=null;const id=createClientId(),token=createClientId();let ws;
    try{ws=new WebSocket('wss://sso.nexusmods.com');}catch{popup.close();return toast('Não foi possível abrir a conexão segura do Nexus.','error');}
    const timer=setTimeout(()=>finishNexusSso('O login Nexus expirou. Tente novamente.'),180000);nexusSsoRef.current={ws,popup,timer};setNexusSsoBusy(true);
    ws.onopen=()=>{ws.send(JSON.stringify({id,token,protocol:2}));popup.location.href=`https://www.nexusmods.com/sso?id=${encodeURIComponent(id)}&application=${encodeURIComponent(application)}`;};
    ws.onmessage=async event=>{let result;try{result=JSON.parse(event.data);}catch{return;}if(result?.api_key){const key=result.api_key;finishNexusSso('');const saved=await saveKey('nexus',false,key);if(saved)toast('Conta Nexus conectada e chave salva neste computador.');}else if(result?.success===false)finishNexusSso(result.message||'O Nexus não concluiu o login.');};
    ws.onerror=()=>finishNexusSso('A conexão de login do Nexus falhou. Verifique a rede e tente novamente.');
    ws.onclose=()=>{if(nexusSsoRef.current?.ws===ws)finishNexusSso('A conexão de login foi encerrada antes de receber a chave.');};
  }
  const descriptions={thunderstore:'Catálogo público, versões, dependências e instalação direta. Não exige conta.',hexium:'Catálogo público de mods para Valheim, compatível com o formato Thunderstore.',nexus:'A busca paginada usa a API Nexus. Downloads respeitam as permissões da sua conta; algumas contas também podem autorizar um arquivo com o link temporário NXM.',curseforge:'A chave da API vem do portal CurseForge for Studios e exige habilitação de desenvolvedor.'};
  return <><PageHeading eyebrow="CONECTORES DE MODS" title="Integrações" description="Estado dos adaptadores e requisitos para acessar cada catálogo." actions={<Button onClick={load} disabled={checking}><RefreshCw size={15} className={checking?'spin':''}/> {checking?'Verificando…':'Verificar APIs'}</Button>}/><div className="notice">As chaves ficam criptografadas no armazenamento local e nunca são devolvidas à interface. Não informe a senha da sua conta.</div><div className="integration-grid">{['thunderstore','hexium','nexus','curseforge'].map(id=>{const p=providers.find(x=>x.provider?.id===id||x.id===id)||{},base=p.provider||p,state=testResults[id]?.connectivity||p.connectivity||p.status||base.state||'unknown',live=['reachable','rate-limited','ready'].includes(state);return <article className={`integration-card provider-card-${id}`} key={id}><div className="integration-card-head"><span className={`provider-logo ${id}`}><ProviderIcon provider={id}/></span><div><h2>{providerNames[id]}</h2><span className={`provider-status ${live?'ready':'pending'}`}><i/>{testResults[id]?.message||p.message||base.detail||(live?'API pública disponível':'Verificando conexão')}</span></div></div><p>{descriptions[id]}</p>{['nexus','curseforge'].includes(id)&&<div className="provider-config"><label className="field-label">Chave de API<input className="field" type="password" autoComplete="new-password" spellCheck="false" value={keys[id]||''} onChange={event=>setKeys(current=>({...current,[id]:event.target.value}))} placeholder={settings[id]?'Configurada · deixe vazio para manter':'Cole aqui sua chave de API'}/></label><div className="provider-config-actions"><Button size="small" variant="primary" disabled={!!busy[id]||!keys[id]?.trim()} onClick={()=>saveKey(id)}>{busy[id]==='save'?'Salvando…':'Salvar chave'}</Button><Button size="small" disabled={!!busy[id]||!settings[id]} onClick={()=>testKey(id)}>{busy[id]==='test'?'Testando…':'Testar conexão'}</Button>{settings[id]&&<Button size="small" variant="danger" disabled={!!busy[id]} onClick={()=>saveKey(id,true)}>Remover chave</Button>}</div><small>{settings[id]?'Chave salva neste computador.':'Nenhuma chave configurada.'}</small></div>}{id==='nexus'&&<><div className="integration-guide"><b>Como obter a chave Nexus</b><ol><li>Entre na sua conta Nexus Mods.</li><li>Abra <a href="https://www.nexusmods.com/users/myaccount?tab=api%20access" target="_blank" rel="noreferrer">My account → API Access</a> e gere uma chave pessoal.</li><li>Salve a chave acima. Se o Nexus exigir aprovação para um aplicativo público, solicite-a pelo suporte oficial.</li></ol><label className="field-label">Referência do aplicativo Nexus para login redirecionado<input className="field" value={nexusApplication} onChange={event=>{setNexusApplication(event.target.value);localStorage.setItem('dss-nexus-application',event.target.value);}} placeholder="Fornecida pelo Nexus após aprovar o DSS"/></label><small>O login por redirecionamento usa o SSO oficial do Nexus e só funciona com uma referência de aplicativo aprovada.</small><Button size="small" onClick={startNexusSso} disabled={nexusSsoBusy||!nexusApplication.trim()}>{nexusSsoBusy?<><LoaderCircle size={14} className="spin"/>Aguardando login…</>:'Entrar com Nexus'}</Button>{nexusSsoBusy&&<Button size="small" onClick={()=>finishNexusSso('Login Nexus cancelado.')}>Cancelar login</Button>}<a href="https://github.com/Nexus-Mods/sso-integration-demo" target="_blank" rel="noreferrer">Documentação do SSO Nexus ↗</a></div><form className="integration-guide nexus-download-auth" onSubmit={authorizeNexusDownload}><b>Autorizar download com link NXM</b><p>No Nexus, copie o endereço do link <strong>Download with Manager</strong> do arquivo desejado e cole aqui. Ele autoriza somente esse arquivo até expirar.</p><label className="field-label">Link NXM do arquivo<input className="field" type="text" autoComplete="off" spellCheck="false" value={nxmDownloadLink} onChange={event=>setNxmDownloadLink(event.target.value)} placeholder="nxm://valheim/mods/123/files/456?key=…&expires=…"/></label><Button type="submit" size="small" variant="primary" disabled={!!busy.nexusDownload||!nxmDownloadLink.trim()}>{busy.nexusDownload==='save'?<><LoaderCircle size={14} className="spin"/>Autorizando…</>:'Autorizar arquivo'}</Button><small>O DSS guarda a autorização temporariamente em memória. A chave de API continua necessária; o Nexus ainda controla a permissão de download.</small></form></>}{id==='curseforge'&&<div className="integration-guide"><b>Como obter a chave CurseForge</b><ol><li>Crie uma conta no portal <a href="https://console.curseforge.com/" target="_blank" rel="noreferrer">CurseForge for Studios</a>.</li><li>Cadastre um projeto de jogo em <a href="https://docs.curseforge.com/docs/getting-started/setting-up-your-game/" target="_blank" rel="noreferrer">Set up your game</a>. A chave fica disponível quando o projeto entra no estado de teste ou é aprovado.</li><li>Copie a chave de API do painel e cole no campo acima. Se não aparecer, solicite acesso pelo <a href="https://support.curseforge.com/support/solutions/articles/9000208346-about-the-curseforge-api-and-how-to-apply-for-a-key" target="_blank" rel="noreferrer">suporte CurseForge</a>.</li></ol><a href="https://docs.curseforge.com/rest-api/" target="_blank" rel="noreferrer">Documentação da API CurseForge ↗</a><small>O CurseForge não oferece um login genérico de usuário para este catálogo; o acesso é feito por chave oficial de desenvolvedor.</small></div>}<div className="integration-foot"><span>{base.accountRequired?'Credencial necessária':'Sem login necessário'}</span>{id==='thunderstore'&&<Link to="/games/valheim/servers">Explorar catálogo ↗</Link>}</div></article>;})}</div><Card className="section-gap"><div className="panel-head"><div><h2>Política de acesso</h2><p>Downloads respeitam as regras e cotas publicadas por cada provedor.</p></div></div><p>O DSS usa catálogos públicos quando disponíveis, respeita limites e não contorna filas. Nexus e CurseForge mantêm suas próprias permissões para API e download; a chave validada habilita apenas as funções disponibilizadas pelo adaptador.</p><p><a href="https://api-docs.nexusmods.com/" target="_blank" rel="noreferrer">API Nexus Mods</a> · <a href="https://docs.curseforge.com/rest-api/" target="_blank" rel="noreferrer">API CurseForge</a></p></Card></>;
}

function NotificationModDetailsHost() {
  const [open, setOpen] = useState(false), [items, setItems] = useState([]), [loading, setLoading] = useState(false);
  const requestId = useRef(0);
  useEffect(() => {
    const show = event => {
      const id = ++requestId.current;
      const requested = Array.isArray(event.detail?.mods) ? event.detail.mods.slice(0, 24) : [];
      setOpen(true); setLoading(true); setItems(requested.map(item => ({ ...item, loading: true })));
      const groups = new Map();
      for (const item of requested) {
        const provider = String(item.provider || '').toLowerCase() === 'bepinex' ? 'thunderstore' : String(item.provider || '').toLowerCase();
        if (!groups.has(provider)) groups.set(provider, []);
        groups.get(provider).push(item);
      }
      Promise.all([...groups].map(async ([provider, mods]) => {
        const query = new URLSearchParams({ ids: mods.map(item => item.packageId).join(',') });
        try {
          const result = await api(`/api/catalog/details/${encodeURIComponent(provider)}?${query}`, { cache: 'no-store', suppressFailureToast: true });
          return { provider, items: result.items || [], error: '' };
        } catch (error) { return { provider, items: [], error: error.message || 'Não foi possível carregar os dados atuais deste mod.' }; }
      })).then(groupsResult => {
        if (requestId.current !== id) return;
        const details = new Map(groupsResult.flatMap(group => group.items.map(item => [`${group.provider}:${String(item.packageId).toLowerCase()}`, item])));
        const errors = new Map(groupsResult.map(group => [group.provider, group.error]));
        setItems(requested.map(item => {
          const provider = String(item.provider || '').toLowerCase() === 'bepinex' ? 'thunderstore' : String(item.provider || '').toLowerCase();
          return { ...item, ...(details.get(`${provider}:${String(item.packageId).toLowerCase()}`) || {}), provider, loading: false,
            error: details.has(`${provider}:${String(item.packageId).toLowerCase()}`) ? '' : errors.get(provider) || 'O provedor não retornou mais este pacote.' };
        }));
        setLoading(false);
      });
    };
    window.addEventListener('dss-open-mod-details', show);
    return () => window.removeEventListener('dss-open-mod-details', show);
  }, []);
  if (!open) return null;
  const close = () => { ++requestId.current; setOpen(false); setLoading(false); };
  return <Modal title={items.length === 1 ? 'Detalhes do mod' : 'Mods da operação'} description={loading ? 'Consultando agora os dados publicados pelo provedor.' : 'Informações atuais obtidas diretamente do provedor do mod.'} close={close} className="notification-mod-details-modal">
    {items.map((item,index) => <article className="notification-mod-detail" key={`${item.provider}:${item.packageId}:${index}`}>
      {item.iconUrl ? <img src={item.iconUrl} alt="" loading="lazy"/> : <span className="notification-mod-detail-icon"><Package size={20}/></span>}
      <div className="notification-mod-detail-body"><div className="notification-mod-detail-title"><div><small>{providerNames[item.provider] || item.provider}</small><h3>{item.name || item.packageId}</h3></div>{item.version&&<b>v{item.version}</b>}</div>
        {item.loading&&<Spinner label="Atualizando detalhes pela internet…"/>}{item.error&&<p className="inline-error"><CircleAlert size={15}/>{item.error}</p>}
        {!item.loading&&!item.error&&<><p>{item.description || 'O provedor não publicou uma descrição para este pacote.'}</p><div className="notification-mod-detail-meta"><span>por {item.owner || 'Autor não informado'}</span>{Number.isFinite(Number(item.totalDownloads))&&<span>{Number(item.totalDownloads).toLocaleString()} downloads</span>}</div>{item.categories?.length>0&&<div className="notification-mod-detail-tags">{item.categories.slice(0,8).map(tag=><span key={tag}>{tag}</span>)}</div>}{item.projectUrl&&<a className="button small" href={item.projectUrl} target="_blank" rel="noreferrer"><ExternalLink size={14}/> Abrir no provedor</a>}</>}
      </div>
    </article>)}
  </Modal>;
}

export default function RootApp() { return <ToastProvider><NotificationModDetailsHost/><App/></ToastProvider>; }
