const sentRecently = new Map();

async function settings() {
  return chrome.storage.local.get({ enabled: false, port: 17654, token: '', minBytes: 1048576 });
}

chrome.downloads.onCreated.addListener(async (item) => {
  const config = await settings();
  if (!config.enabled || !config.token) return;
  const url = item.finalUrl || item.url;
  if (!/^https?:\/\//i.test(url)) return;
  if (item.fileSize > 0 && item.fileSize < config.minBytes) return;
  const last = sentRecently.get(url);
  if (last && Date.now() - last < 5000) return;
  sentRecently.set(url, Date.now());
  try {
    const response = await fetch(`http://127.0.0.1:${config.port}/capture`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${config.token}` },
      body: JSON.stringify({ url, name: (item.filename || '').split(/[\\/]/).pop() || '', referrer: item.referrer || '' }),
    });
    if (response.status === 202) await chrome.downloads.cancel(item.id);
  } catch {
    return;
  }
});
