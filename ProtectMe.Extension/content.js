(() => {
  const STYLE_ID = 'protectme-safe-browsing-style';
  const OVERLAY_ID = 'protectme-block-overlay';

  function normalizeDomain(raw) {
    let s = (raw || '').trim().toLowerCase();
    s = s.replace(/^https?:\/\//, '').replace(/^www\./, '');
    const slash = s.indexOf('/');
    if (slash >= 0) s = s.slice(0, slash);
    const colon = s.indexOf(':');
    if (colon >= 0) s = s.slice(0, colon);
    return s;
  }

  function isWhitelisted(url, host, list) {
    if (!Array.isArray(list) || list.length === 0) return false;
    const normHost = normalizeDomain(host);
    const normUrl = (url || '').trim().toLowerCase().replace(/\/+$/, '');
    for (const item of list) {
      if (!item || !item.value) continue;
      if (item.scope === 'WholeWebsite') {
        const d = normalizeDomain(item.value);
        if (d && (normHost === d || normHost.endsWith('.' + d))) return true;
      } else if (item.scope === 'SpecificWebpage') {
        const target = item.value.trim().toLowerCase().replace(/\/+$/, '');
        if (target && normUrl === target) return true;
      }
    }
    return false;
  }

  function setSafeBrowsingActive(active) {
    const existing = document.getElementById(STYLE_ID);
    if (active) {
      if (!existing) {
        const style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = `
          img, picture, video, canvas, svg[role="img"] {
            filter: brightness(0) !important;
            background-color: #000000 !important;
            pointer-events: none !important;
          }
          video { opacity: 1 !important; }
        `;
        (document.head || document.documentElement).appendChild(style);
      }
      document.querySelectorAll('video').forEach(v => {
        try { v.pause(); v.removeAttribute('src'); v.load(); } catch {}
      });
    } else if (existing) {
      existing.remove();
    }
  }

  function removeBlockOverlay() {
    const el = document.getElementById(OVERLAY_ID);
    if (el) el.remove();
  }

  function showBlockOverlay(domain, fullUrl, hasPassword) {
    if (document.getElementById(OVERLAY_ID)) return;
    const overlay = document.createElement('div');
    overlay.id = OVERLAY_ID;
    overlay.style.cssText = `
      position: fixed; inset: 0; z-index: 2147483647;
      background: #090909; color: #F3E7C4;
      display: flex; align-items: center; justify-content: center;
      font-family: 'Segoe UI', system-ui, sans-serif;
    `;
    overlay.innerHTML = `
      <div style="width:420px;max-width:92vw;background:#14120E;border:1.5px solid #D4AF37;border-radius:20px;padding:30px 26px;text-align:center;box-shadow:0 20px 60px rgba(0,0,0,0.85);">
        <div style="font-size:13px;letter-spacing:3px;color:#D4AF37;font-weight:800;margin-bottom:10px;">PROTECTME · SITE BLOCKED</div>
        <div style="font-size:20px;font-weight:700;color:#FFF8E7;margin-bottom:8px;word-break:break-word;">${domain}</div>
        <div style="font-size:13px;color:#A89F8D;margin-bottom:20px;">This website is not on your whitelist.</div>
        ${hasPassword ? `<input id="pm-pw" type="password" placeholder="Enter Master Password / PIN" style="width:100%;box-sizing:border-box;padding:11px 14px;border-radius:12px;border:1px solid #3A311E;background:#0A0A0A;color:#F3E7C4;font-size:14px;margin-bottom:14px;outline:none;"/>` : ''}
        <div id="pm-err" style="color:#F87171;font-size:12px;min-height:16px;margin-bottom:10px;"></div>
        <button id="pm-allow-site" style="width:100%;padding:12px;border:none;border-radius:14px;background:linear-gradient(90deg,#C59B27,#F3D98B,#C59B27);color:#0A0A0A;font-weight:800;font-size:14px;cursor:pointer;margin-bottom:10px;">Allow Whole Website (${domain})</button>
        <button id="pm-allow-page" style="width:100%;padding:11px;border:1px solid #D4AF37;border-radius:14px;background:transparent;color:#F3D98B;font-weight:700;font-size:13px;cursor:pointer;">Allow This Webpage Only</button>
      </div>
    `;
    (document.body || document.documentElement).appendChild(overlay);

    const submit = (scope) => {
      const pw = document.getElementById('pm-pw')?.value || '';
      const err = document.getElementById('pm-err');
      chrome.runtime.sendMessage({
        type: 'add_whitelist',
        value: scope === 'WholeWebsite' ? domain : fullUrl,
        scope,
        password: pw
      }, (resp) => {
        if (resp && resp.ok) {
          removeBlockOverlay();
          location.reload();
        } else if (err) {
          err.textContent = resp?.message || 'Incorrect password.';
        }
      });
    };

    document.getElementById('pm-allow-site')?.addEventListener('click', () => submit('WholeWebsite'));
    document.getElementById('pm-allow-page')?.addEventListener('click', () => submit('SpecificWebpage'));
  }

  function applyStatus(status) {
    if (!status || !status.locked) {
      setSafeBrowsingActive(false);
      removeBlockOverlay();
      return;
    }

    const host = location.hostname || '';
    const url = location.href || '';
    const allowed = isWhitelisted(url, host, status.whitelist);

    if (allowed) {
      setSafeBrowsingActive(false);
      removeBlockOverlay();
      return;
    }

    if (status.safeBrowsing) {
      removeBlockOverlay();
      setSafeBrowsingActive(true);
    } else if (status.whitelistOnly) {
      setSafeBrowsingActive(false);
      showBlockOverlay(normalizeDomain(host), url, status.hasPassword);
    } else {
      setSafeBrowsingActive(false);
      removeBlockOverlay();
    }
  }

  function refresh() {
    try {
      chrome.runtime.sendMessage({ type: 'get_status' }, (status) => {
        if (!chrome.runtime.lastError) applyStatus(status);
      });
    } catch {}
  }

  refresh();
  setInterval(refresh, 1500);
})();
