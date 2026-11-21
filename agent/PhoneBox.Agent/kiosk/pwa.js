// Browser pairing and lifecycle for the shared panel. Credentials stay in an HttpOnly cookie.
(() => {
  const portuguese = (navigator.languages || [navigator.language]).some((language) => /^pt(?:-|$)/i.test(language));
  const text = {
    connecting: ['Connecting…', 'Conectando…'],
    connected: ['Connected', 'Conectado'],
    monitoring: ['Monitoring', 'Monitoramento'],
    controlsEnabled: ['Computer controls enabled', 'Controles do computador habilitados'],
    disconnected: ['Connection interrupted', 'Conexão interrompida'],
    connection: ['Connection', 'Conexão'],
    showControls: ['Show panel controls', 'Mostrar controles do painel'],
    toolbarHelp: ['Controls hide after five seconds. Tap the top of the panel to show them again.', 'Os controles se escondem após cinco segundos. Toque no topo do painel para mostrá-los novamente.'],
    keepAwake: ['Keep screen awake', 'Manter tela acesa'],
    screenAwake: ['Screen stays awake', 'Tela mantida acesa'],
    unpair: ['Disconnect this browser', 'Desconectar este navegador'],
    pairTitle: ['Connect to PhoneBox', 'Conectar ao PhoneBox'],
    pairHelp: ['Open PhoneBox settings on your Mac and generate a browser pairing code.', 'Abra as configurações do PhoneBox no Mac e gere um código para parear o navegador.'],
    code: ['Pairing code', 'Código de pareamento'],
    deviceName: ['Name for this browser', 'Nome deste navegador'],
    connect: ['Connect', 'Conectar'],
    pairScope: ['The Mac decides which controls this browser may use. You can revoke its access from the Mac.', 'O Mac define quais controles este navegador pode usar. Você pode revogar o acesso pelo Mac.'],
    installHelp: ['In Safari, choose Share → Add to Home Screen to open this panel as an app.', 'No Safari, escolha Compartilhar → Adicionar à Tela de Início para abrir o painel como um app.'],
    homePairHelp: ['If you add PhoneBox to your Home Screen, open it there and pair that app as well.', 'Se adicionar o PhoneBox à Tela de Início, abra por lá e faça o pareamento também nesse app.'],
    pairFailed: ['The code was refused or expired. Generate a new code in PhoneBox settings on the Mac.', 'O código foi recusado ou expirou. Gere outro código nas configurações do PhoneBox no Mac.'],
    tryLater: ['Too many attempts. Wait a moment before trying again.', 'Muitas tentativas. Aguarde um pouco antes de tentar novamente.'],
    networkError: ['Cannot reach the Mac. Check that PhoneBox is running and both devices can reach the same network.', 'Não foi possível conectar ao Mac. Confira se o PhoneBox está aberto e se os dois dispositivos têm acesso à mesma rede.'],
    logoutFailed: ['Could not disconnect. Try again when the Mac is reachable.', 'Não foi possível desconectar. Tente novamente quando houver conexão com o Mac.'],
    wakeUnavailable: ['Keeping the screen awake requires a compatible browser. Home Screen apps support it on iOS 18.4 or later.', 'Manter a tela acesa requer um navegador compatível. Apps na Tela de Início oferecem esse recurso a partir do iOS 18.4.'],
    wakeFailed: ['The system released the screen lock. Low Power Mode or switching apps may prevent it. Tap again to retry.', 'O sistema liberou a tela. O Modo Pouca Energia ou a troca de app podem impedir o recurso. Toque novamente para tentar.'],
    secureRequired: ['Open PhoneBox using its trusted HTTPS address before pairing.', 'Abra o PhoneBox pelo endereço HTTPS confiável antes de parear.'],
    panelTitle: ['PhoneBox panel', 'Painel PhoneBox'],
  };
  const t = (key) => text[key][portuguese ? 1 : 0];
  const byId = (id) => document.getElementById(id);
  document.documentElement.lang = portuguese ? 'pt-BR' : 'en';
  document.querySelectorAll('[data-text]').forEach((element) => { element.textContent = t(element.dataset.text); });

  function showError(key) {
    const error = byId('error');
    error.hidden = !key;
    error.textContent = key ? t(key) : '';
  }

  async function request(path, body, readJson = false) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 8000);
    try {
      const response = await fetch(path, {
        method: body === undefined ? 'GET' : 'POST',
        credentials: 'same-origin',
        cache: 'no-store',
        signal: controller.signal,
        headers: body === undefined ? {} : { 'Content-Type': 'application/json', 'X-PhoneBox-Panel': '1' },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      const data = readJson && response.ok ? await response.json() : null;
      return { response, data };
    } finally {
      clearTimeout(timer);
    }
  }

  if (!window.isSecureContext) {
    showError('secureRequired');
    if (byId('pairSubmit')) byId('pairSubmit').disabled = true;
    return;
  }

  if ('serviceWorker' in navigator) {
    navigator.serviceWorker.register('/kiosk/sw.js', { scope: '/kiosk/' }).catch(() => {});
  }

  if (document.body.dataset.page === 'pair') {
    document.title = t('pairTitle');
    // A one-time invitation stays out of HTTP requests and is removed before the user submits it.
    function consumeInvitation() {
      const invitation = new URLSearchParams(location.hash.slice(1)).get('code');
      if (location.hash) history.replaceState(null, '', location.pathname);
      if (invitation && invitation.length <= 128) byId('pairCode').value = invitation;
    }
    consumeInvitation();
    window.addEventListener('hashchange', consumeInvitation);
    byId('pairForm').addEventListener('submit', async (event) => {
      event.preventDefault();
      const submit = byId('pairSubmit');
      if (submit.disabled) return;
      submit.disabled = true;
      showError(null);
      try {
        const { response } = await request('/kiosk/pair', { code: byId('pairCode').value.trim(), name: byId('deviceName').value.trim() });
        if (!response.ok) { showError(response.status === 429 ? 'tryLater' : 'pairFailed'); return; }
        byId('pairCode').value = '';
        location.replace('/kiosk/');
      } catch (_) {
        showError('networkError');
      } finally {
        submit.disabled = false;
      }
    });
    return;
  }

  let frame = null;
  let checking = false;
  let leaving = false;
  const status = byId('connection');
  const toolbar = byId('toolbar');
  const reveal = byId('showToolbar');
  reveal.setAttribute('aria-label', t('showControls'));
  let connected = false;
  let toolbarTimer = null;
  let toolbarPressed = false;
  let keyboardNavigation = false;

  function toolbarInUse() {
    return !connected || !byId('connectionDetails').hidden || !byId('error').hidden || toolbarPressed
      || (keyboardNavigation && toolbar.contains(document.activeElement));
  }

  function scheduleToolbarHide() {
    clearTimeout(toolbarTimer);
    if (toolbarInUse() || document.hidden) return;
    toolbarTimer = setTimeout(() => {
      if (toolbarInUse() || document.hidden) return;
      toolbar.hidden = true;
      reveal.hidden = false;
      document.body.classList.add('toolbar-hidden');
    }, 5000);
  }

  function revealToolbar() {
    toolbar.hidden = false;
    reveal.hidden = true;
    document.body.classList.remove('toolbar-hidden');
    scheduleToolbarHide();
  }

  // The reveal target has no static pixels. A top-edge tap is consumed before it can activate a widget.
  reveal.addEventListener('click', revealToolbar);
  document.addEventListener('keydown', (event) => {
    keyboardNavigation = true;
    if (event.key !== 'Tab' || !toolbar.hidden) return;
    // Safari can skip buttons in its default Tab order; revealing controls must not depend on that preference.
    event.preventDefault();
    revealToolbar();
    (event.shiftKey || byId('wakeLock').hidden ? byId('detailsToggle') : byId('wakeLock')).focus();
  }, true);
  document.addEventListener('pointerdown', () => { keyboardNavigation = false; }, true);
  reveal.addEventListener('focus', () => {
    if (!keyboardNavigation) return;
    revealToolbar();
    (byId('wakeLock').hidden ? byId('detailsToggle') : byId('wakeLock')).focus();
  });
  toolbar.addEventListener('focusin', scheduleToolbarHide);
  toolbar.addEventListener('focusout', () => queueMicrotask(scheduleToolbarHide));
  toolbar.addEventListener('pointerdown', () => { toolbarPressed = true; clearTimeout(toolbarTimer); });
  const releaseToolbar = () => { if (toolbarPressed) { toolbarPressed = false; scheduleToolbarHide(); } };
  document.addEventListener('pointerup', releaseToolbar);
  document.addEventListener('pointercancel', releaseToolbar);
  window.addEventListener('blur', releaseToolbar);
  toolbar.addEventListener('click', scheduleToolbarHide);

  function expired() {
    if (leaving) return;
    leaving = true;
    frame?.remove();
    location.replace('/kiosk/pair.html');
  }

  async function checkSession() {
    if (checking || leaving || document.hidden) return;
    checking = true;
    try {
      const { response, data: session } = await request('/kiosk/session', undefined, true);
      if (response.status === 401) { expired(); return; }
      if (!response.ok) throw new Error('Session unavailable');
      if (session.paired !== true) { expired(); return; }
      byId('sessionName').textContent = session.name || '';
      byId('capabilities').textContent = t(session.controls === true ? 'controlsEnabled' : 'monitoring');
      status.textContent = t('connected');
      showError(null);
      const recovered = !connected;
      connected = true;
      if (recovered) revealToolbar();
      if (!frame) {
        frame = document.createElement('iframe');
        frame.title = t('panelTitle');
        frame.src = '/kiosk/panel/';
        byId('panelHost').replaceChildren(frame);
      }
    } catch (_) {
      connected = false;
      status.textContent = t('disconnected');
      showError('networkError');
      revealToolbar();
    } finally {
      checking = false;
    }
  }

  window.addEventListener('message', (event) => {
    if (event.origin !== location.origin || event.source !== frame?.contentWindow) return;
    if (event.data?.type === 'phonebox-kiosk-session-expired') expired();
  });
  window.addEventListener('online', checkSession);
  window.addEventListener('offline', () => { connected = false; status.textContent = t('disconnected'); showError('networkError'); revealToolbar(); });
  document.addEventListener('visibilitychange', () => { if (!document.hidden) { revealToolbar(); checkSession(); } });
  setInterval(checkSession, 60_000);
  checkSession();

  byId('detailsToggle').addEventListener('click', () => {
    const details = byId('connectionDetails');
    details.hidden = !details.hidden;
    byId('detailsToggle').setAttribute('aria-expanded', String(!details.hidden));
    scheduleToolbarHide();
  });
  byId('logout').addEventListener('click', async () => {
    const button = byId('logout');
    button.disabled = true;
    try {
      const { response } = await request('/kiosk/logout', {});
      if (response.ok || response.status === 401) expired();
      else showError('logoutFailed');
    } catch (_) {
      showError('logoutFailed');
    } finally {
      button.disabled = false;
    }
  });

  const wakeButton = byId('wakeLock');
  let screenLock = null;
  let keepAwake = false;
  let wakeGeneration = 0;
  function renderWake() {
    const held = !!screenLock && !screenLock.released;
    wakeButton.setAttribute('aria-pressed', String(held));
    wakeButton.textContent = t(held ? 'screenAwake' : 'keepAwake');
  }
  async function releaseWake() {
    wakeGeneration++;
    const current = screenLock;
    screenLock = null;
    renderWake();
    if (current) { try { await current.release(); } catch (_) {} }
  }
  async function acquireWake() {
    if (!keepAwake || document.hidden || screenLock) return;
    const generation = ++wakeGeneration;
    try {
      const lock = await navigator.wakeLock.request('screen');
      if (generation !== wakeGeneration || !keepAwake || document.hidden) { await lock.release(); return; }
      screenLock = lock;
      byId('wakeNote').textContent = '';
      lock.addEventListener('release', () => {
        if (screenLock !== lock) return;
        screenLock = null;
        if (!document.hidden) { keepAwake = false; byId('wakeNote').textContent = t('wakeFailed'); }
        renderWake();
      });
      renderWake();
    } catch (_) {
      if (generation !== wakeGeneration) return;
      keepAwake = false;
      byId('wakeNote').textContent = t('wakeFailed');
      renderWake();
    }
  }
  if (!navigator.wakeLock) {
    wakeButton.hidden = true;
    byId('wakeNote').textContent = t('wakeUnavailable');
  } else {
    wakeButton.addEventListener('click', () => {
      keepAwake = !keepAwake;
      if (keepAwake) acquireWake();
      else releaseWake();
    });
    document.addEventListener('visibilitychange', () => { if (document.hidden) releaseWake(); else acquireWake(); });
    window.addEventListener('pagehide', releaseWake);
  }
})();
