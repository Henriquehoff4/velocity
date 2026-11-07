const field = (id) => document.getElementById(id);

chrome.storage.local.get({ enabled: false, port: 17654, token: '', minBytes: 1048576 }).then((config) => {
  field('enabled').checked = config.enabled;
  field('port').value = config.port;
  field('token').value = config.token;
  field('min').value = Math.round(config.minBytes / 1048576);
});

field('save').addEventListener('click', async () => {
  const status = field('status');
  const port = Number(field('port').value);
  const token = field('token').value.trim();
  await chrome.storage.local.set({
    enabled: field('enabled').checked,
    port,
    token,
    minBytes: Math.max(0, Number(field('min').value) || 0) * 1048576,
  });
  try {
    const response = await fetch(`http://127.0.0.1:${port}/capture`, { method: 'GET', headers: { Authorization: `Bearer ${token}` } });
    status.textContent = response.status === 404 ? 'Conectado ao Velocity.' : 'O código de acesso não confere.';
  } catch {
    status.textContent = 'Velocity não respondeu. Abra o app e ligue a captura em Ajustes.';
  }
});
