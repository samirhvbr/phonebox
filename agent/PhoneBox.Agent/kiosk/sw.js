// A navigation fallback only. The panel, API responses and credentials are never cached.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', (event) => {
  const url = new URL(event.request.url);
  if (url.origin !== self.location.origin || event.request.mode !== 'navigate' || url.pathname !== '/kiosk/') return;
  event.respondWith(fetch(event.request).catch(() => {
    const portuguese = /^pt/i.test(event.request.headers.get('Accept-Language') || '');
    const title = portuguese ? 'O Mac está desconectado' : 'The Mac is disconnected';
    const message = portuguese
      ? 'Confira se o PhoneBox está aberto no Mac e se os dispositivos têm acesso à mesma rede. Depois, tente novamente.'
      : 'Check that PhoneBox is running on the Mac and both devices can reach the same network, then try again.';
    const retry = portuguese ? 'Tentar novamente' : 'Try again';
    return new Response(`<!doctype html><html lang="${portuguese ? 'pt-BR' : 'en'}"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>PhoneBox</title><body><main><h1>${title}</h1><p>${message}</p><a href="/kiosk/">${retry}</a></main></body></html>`, {
      status: 503,
      headers: { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store', 'Content-Security-Policy': "default-src 'none'; base-uri 'none'; frame-ancestors 'none'" },
    });
  }));
});
