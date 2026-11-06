/* scan.js - one scanner for the whole admin side. It reads a QR code or barcode three ways:
   1) the phone or laptop camera (html5-qrcode, loaded only when the admin presses "Start camera"),
   2) a USB / Bluetooth scanner (it types the code and presses Enter, so the box below is always focused),
   3) typing the code by hand.
   SyScan.open({ title, hint, keepOpen, onCode(code) }) - onCode may return a message that is shown under the box. */
(function () {
  const LIB = 'https://cdnjs.cloudflare.com/ajax/libs/html5-qrcode/2.3.8/html5-qrcode.min.js';
  let libPromise = null;
  const loadLib = () => libPromise || (libPromise = new Promise((ok, no) => { if (window.Html5Qrcode) return ok(); const s = document.createElement('script'); s.src = LIB; s.onload = ok; s.onerror = () => { libPromise = null; no(new Error('The camera scanner could not be loaded. Use a USB scanner or type the code.')); }; document.head.appendChild(s); }));
  window.SyScan = {
    open(o) {
      const m = showModal(`<h2>${ico('upc-scan')} ${esc(o.title || 'Scan a code')}</h2><p class="sy-sub">${esc(o.hint || 'Use the camera, a USB scanner, or type the code.')}</p>
        <div id="sy-reader" class="sy-reader" hidden></div>
        <div class="sy-scanrow"><input id="sy-scan-in" class="sy-input" placeholder="Scan or type a code, then press Enter" autocomplete="off" autocapitalize="characters" aria-label="Code">
        <button type="button" class="sy-btn accent small" id="sy-scan-go">Use code</button></div>
        <div class="sy-scanrow"><button type="button" class="sy-btn ghost small" id="sy-cam">${ico('camera')} Start camera</button></div>
        <p id="sy-scan-msg" class="sy-sub" role="status" style="margin-top:10px"></p>`, () => stop());
      const $ = s => m.el.querySelector(s); let qr = null, busy = false;
      const stop = async () => { if (qr) { try { await qr.stop(); qr.clear(); } catch (e) { } qr = null; } };
      const take = async raw => {
        const code = String(raw || '').trim(); if (!code || busy) return; busy = true;
        try { const r = await o.onCode(code); if (!o.keepOpen) { await stop(); m.close(); return; } $('#sy-scan-msg').textContent = r || ''; $('#sy-scan-in').value = ''; $('#sy-scan-in').focus(); }
        catch (e) { $('#sy-scan-msg').textContent = e.message || 'That code did not work.'; }
        finally { setTimeout(() => { busy = false; }, 700); }
      };
      $('#sy-scan-in').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); take(e.target.value); } });
      $('#sy-scan-go').addEventListener('click', () => take($('#sy-scan-in').value));
      $('#sy-cam').addEventListener('click', async () => {
        const btn = $('#sy-cam');
        if (qr) { await stop(); $('#sy-reader').hidden = true; btn.innerHTML = ico('camera') + ' Start camera'; return; }
        try {
          await loadLib(); $('#sy-reader').hidden = false; qr = new Html5Qrcode('sy-reader');
          await qr.start({ facingMode: 'environment' }, { fps: 10, qrbox: { width: 230, height: 230 } }, txt => take(txt), () => { });
          btn.innerHTML = ico('camera-video-off') + ' Stop camera';
        } catch (e) { qr = null; $('#sy-reader').hidden = true; $('#sy-scan-msg').textContent = (e && e.message) || 'The camera could not start. Allow camera access, or use a USB scanner.'; }
      });
      setTimeout(() => $('#sy-scan-in').focus(), 50);
      return m;
    }
  };
})();
