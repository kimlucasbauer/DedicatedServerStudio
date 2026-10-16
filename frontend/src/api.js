function announceRequestFailure(message) {
  if (typeof window !== 'undefined') {
    window.dispatchEvent(new CustomEvent('dss-api-error', { detail: { message } }));
  }
}

async function attachAntiforgeryToken(headers, body) {
  if (!(body instanceof FormData) || headers.has('X-DSS-CSRF')) return;
  const response = await fetch('/api/security/antiforgery', { credentials: 'same-origin', cache: 'no-store' });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok || !payload.requestToken) {
    throw new Error(payload.error || 'Não foi possível validar a sessão para enviar o arquivo. Entre novamente e tente de novo.');
  }
  headers.set('X-DSS-CSRF', payload.requestToken);
}

export async function api(path, options = {}) {
  const { suppressFailureToast = false, ...requestOptions } = options;
  let response;
  const headers = new Headers(requestOptions.headers || {});
  await attachAntiforgeryToken(headers, requestOptions.body);
  if (!(requestOptions.body instanceof FormData) && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
  try {
    response = await fetch(path, {
      credentials: 'same-origin',
      ...requestOptions,
      headers
    });
  } catch (error) {
    if (error?.name === 'AbortError') throw error;
    const message = 'Não foi possível conectar ao DSS. Verifique se o servidor está em execução.';
    if (!suppressFailureToast) announceRequestFailure(message);
    throw new Error(message, { cause: error });
  }
  if (response.status === 204) return null;
  const body = await response.json().catch(() => ({}));
  if (response.status === 401 && path !== '/api/auth/login') {
    window.dispatchEvent(new CustomEvent('dss-auth-expired'));
    const message = 'Sua sessão expirou. Entre novamente.';
    announceRequestFailure(message);
    throw new Error(message);
  }
  if (!response.ok) {
    const message = body.error || (response.status === 405
      ? 'O DSS em execução não reconhece esta operação. Reinicie com a versão atualizada do DSS.'
      : `Falha na solicitação (${response.status}).`);
    if (!suppressFailureToast) announceRequestFailure(message);
    throw Object.assign(new Error(message), { status: response.status });
  }
  return body;
}

export async function streamApi(path, options = {}, onEvent = () => {}) {
  const { suppressFailureToast = false, ...requestOptions } = options;
  let response;
  const headers = new Headers(requestOptions.headers || {});
  await attachAntiforgeryToken(headers, requestOptions.body);
  if (!(requestOptions.body instanceof FormData) && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
  try {
    response = await fetch(path, {
      credentials: 'same-origin',
      ...requestOptions,
      headers
    });
  } catch (error) {
    if (error?.name === 'AbortError') throw error;
    const message = 'Não foi possível conectar ao DSS. Verifique se o servidor está em execução.';
    if (!suppressFailureToast) announceRequestFailure(message);
    throw new Error(message, { cause: error });
  }
  if (response.status === 401) {
    window.dispatchEvent(new CustomEvent('dss-auth-expired'));
    const message = 'Sua sessão expirou. Entre novamente.';
    if (!suppressFailureToast) announceRequestFailure(message);
    throw new Error(message);
  }
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    const message = body.error || (response.status === 405
      ? 'O DSS em execução não reconhece a instalação em lote. Reinicie com a versão atualizada do DSS.'
      : `Falha na solicitação (${response.status}).`);
    if (!suppressFailureToast) announceRequestFailure(message);
    throw Object.assign(new Error(message), { status: response.status });
  }

  const decoder = new TextDecoder();
  const reader = response.body?.getReader();
  if (!reader) throw new Error('O navegador não conseguiu abrir o fluxo de progresso do DSS.');
  let pending = '';
  let result = null;
  const consumeLine = line => {
    if (!line.trim()) return;
    const event = JSON.parse(line);
    if (event.type === 'error') {
      result = event;
      return;
    }
    if (event.type === 'completed') result = event;
    onEvent(event);
  };
  try {
    while (true) {
      const { done, value } = await reader.read();
      pending += decoder.decode(value || new Uint8Array(), { stream: !done });
      const lines = pending.split('\n');
      pending = lines.pop() || '';
      for (const line of lines) consumeLine(line);
      if (done) break;
    }
    if (pending.trim()) consumeLine(pending);
  } catch (error) {
    if (error instanceof SyntaxError) {
      const message = 'O DSS enviou um retorno de progresso inválido.';
      if (!suppressFailureToast) announceRequestFailure(message);
      throw new Error(message, { cause: error });
    }
    throw error;
  } finally {
    reader.releaseLock();
  }
  if (result?.type === 'error') throw new Error(result.message || 'A instalação em lote falhou.');
  if (!result) throw new Error('A conexão foi encerrada antes do DSS confirmar o fim da instalação.');
  return result;
}

export const json = value => JSON.stringify(value);
export const fmtDate = value => {
  if (!value) return '—';
  const uiLocale = typeof document !== 'undefined' ? document.documentElement.lang : '';
  return new Intl.DateTimeFormat(uiLocale || 'pt-BR', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value));
};
export function bytes(value = 0) {
  const n = Number(value) || 0;
  if (n < 1024) return `${n} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let amount = n / 1024, index = 0;
  while (amount >= 1024 && index < units.length - 1) { amount /= 1024; index++; }
  return `${amount.toFixed(amount >= 10 ? 0 : 1)} ${units[index]}`;
}
export const humanDuration = seconds => {
  const s = Math.max(0, Math.floor(Number(seconds) || 0));
  return s >= 86400 ? `${Math.floor(s / 86400)}d ${Math.floor(s % 86400 / 3600)}h` : s >= 3600 ? `${Math.floor(s / 3600)}h ${Math.floor(s % 3600 / 60)}min` : `${Math.floor(s / 60)}min`;
};
