let cachedStatus = {
  locked: false,
  safeBrowsing: false,
  whitelistOnly: false,
  blockPorn: false,
  blockImageVideo: false,
  hasPassword: false,
  whitelist: []
};

async function pollStatus() {
  try {
    const res = await fetch('http://127.0.0.1:8787/api/status', { cache: 'no-store' });
    if (res.ok) {
      cachedStatus = await res.json();
    }
  } catch {
    // ProtectMe service not running — leave protection off
    cachedStatus = { locked: false, safeBrowsing: false, whitelistOnly: false, blockPorn: false, blockImageVideo: false, hasPassword: false, whitelist: [] };
  }
}

pollStatus();
setInterval(pollStatus, 1500);

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.type === 'get_status') {
    pollStatus().then(() => sendResponse(cachedStatus));
    return true;
  }
  if (msg?.type === 'add_whitelist') {
    fetch('http://127.0.0.1:8787/api/whitelist', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        value: msg.value,
        scope: msg.scope,
        password: msg.password || ''
      })
    })
      .then(r => r.json())
      .then(async data => {
        await pollStatus();
        sendResponse(data);
      })
      .catch(() => sendResponse({ ok: false, message: 'Could not reach ProtectMe service.' }));
    return true;
  }
});
