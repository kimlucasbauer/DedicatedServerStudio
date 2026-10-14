export async function readFullLog(path) {
  const response = await fetch(path, { credentials: 'same-origin' });
  if (!response.ok) {
    const body = await response.text();
    let message = `Não foi possível ler o log completo (${response.status}).`;
    try { message = JSON.parse(body).error || message; } catch { if (body.trim()) message = body.trim(); }
    throw new Error(message);
  }
  return response.text();
}

export async function copyFullLog(path, fallback = '') {
  const text = path ? await readFullLog(path) : fallback;
  if (!text) throw new Error('Ainda não há linhas de console para copiar.');
  if (!navigator.clipboard?.writeText) throw new Error('A área de transferência não está disponível neste navegador.');
  await navigator.clipboard.writeText(text);
  return text.length;
}

export async function downloadFullLog(path, fallback, fileName) {
  const text = path ? await readFullLog(path) : fallback;
  if (!text) throw new Error('Ainda não há um log para baixar.');
  const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.append(link);
  link.click();
  link.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
  return text.length;
}
