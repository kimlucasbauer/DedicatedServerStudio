import { useEffect, useRef, useState } from 'react';
import { Copy, Download } from 'lucide-react';
import { copyFullLog, downloadFullLog } from './consoleLogs.js';

export default function SteamCommandOutput({ profileId, operation, toast }) {
  const [autoScroll, setAutoScroll] = useState(true);
  const [busy, setBusy] = useState(false);
  const output = useRef(null);
  const hasCurrentLine = Boolean(operation.currentOutputLine);
  const text = [...(operation.output || []).slice(hasCurrentLine ? -999 : -1000), operation.currentOutputLine].filter(Boolean).join('\n');
  const path = profileId && operation.operationId
    ? `/api/instances/${profileId}/game/update-log?operationId=${encodeURIComponent(operation.operationId)}`
    : '';

  async function copyLog() {
    setBusy(true);
    try {
      const count = await copyFullLog('', text);
      toast?.(`Saída visível do SteamCMD copiada (${count.toLocaleString()} caracteres).`, 'success', { title:'Console do SteamCMD copiado' });
    } catch (error) { toast?.(error.message, 'error'); }
    finally { setBusy(false); }
  }

  async function downloadLog() {
    setBusy(true);
    try {
      const count = await downloadFullLog(path, text, `steamcmd-${profileId || 'dss'}.log`);
      toast?.(`Log completo do SteamCMD baixado (${count.toLocaleString()} caracteres).`, 'success', { title:'Download do SteamCMD' });
    } catch (error) { toast?.(error.message, 'error'); }
    finally { setBusy(false); }
  }

  useEffect(() => {
    if (autoScroll && output.current) output.current.scrollTop = output.current.scrollHeight;
  }, [text, autoScroll]);

  return <div className="steamcmd-output-wrap">
    <label className="check-row"><input type="checkbox" checked={autoScroll} onChange={event => setAutoScroll(event.target.checked)}/> Rolar automaticamente para o último registro</label>
    <small className="console-history-limit">Exibindo as últimas 1.000 linhas. Baixe o log para acessar o histórico completo.</small>
    <div className="console-log-frame steamcmd-log-frame"><pre ref={output} className="steamcmd-output">{text || 'Aguardando saída do SteamCMD…'}</pre><div className="console-log-actions"><button type="button" title="Copiar console exibido" aria-label="Copiar console exibido do SteamCMD" disabled={busy || !text} onClick={copyLog}><Copy size={15}/></button><button type="button" title="Baixar log completo" aria-label="Baixar log completo do SteamCMD" disabled={busy || (!path && !text)} onClick={downloadLog}><Download size={15}/></button></div></div>
  </div>;
}
