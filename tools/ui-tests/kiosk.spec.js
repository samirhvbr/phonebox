const { test, expect } = require('@playwright/test');
const fs = require('node:fs/promises');
const path = require('node:path');

const root = path.resolve(__dirname, '..', '..');
const origin = 'https://phonebox.test';
const invitation = 'aA_0-forkInvitation';
const dashboard = '.page[data-id="paired-dashboard"] .w-clock';

test.use({ viewport: { width: 390, height: 844 }, locale: 'pt-BR', serviceWorkers: 'block' });

// Serve the real shell and shared panel modules; only the Mac API is simulated. No live agent is contacted.
async function kiosk(page, { paired = false, name = 'Test iPhone' } = {}) {
  const state = { paired, name, sessionStatus: 200, posts: [], requests: [], deniedApiRequests: [], errors: [] };
  page.on('pageerror', (error) => state.errors.push(error.message));
  const json = (route, status, value) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
  await page.route('**/*', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    state.requests.push(request.url());
    if (url.origin !== origin) return json(route, 503, {});
    if (request.method() === 'POST') {
      state.posts.push({ path: url.pathname, header: request.headers()['x-phonebox-panel'], body: request.postDataJSON() });
      if (request.headers()['x-phonebox-panel'] !== '1') return json(route, 403, {});
    }
    if (url.pathname === '/kiosk/session') {
      return json(route, state.paired ? state.sessionStatus : 401, { paired: state.paired, name: state.name, controls: false });
    }
    if (url.pathname === '/kiosk/pair') {
      const body = request.postDataJSON();
      if (body.code !== invitation) return json(route, 401, {});
      state.paired = true;
      state.name = body.name;
      return json(route, 200, { paired: true });
    }
    if (url.pathname === '/kiosk/logout') {
      state.paired = false;
      return json(route, 200, { paired: false });
    }
    if (url.pathname.startsWith('/agent/api/')) {
      if (!state.paired) {
        state.deniedApiRequests.push(url.pathname);
        return json(route, 401, {});
      }
      if (url.pathname === '/agent/api/config') {
        return json(route, 200, { version: 1, config: {
          night: { enabled: false }, weather: {}, shortcuts: [],
          pages: [{ id: 'paired-dashboard', title: 'Private dashboard', enabled: true,
            widgets: [{ id: 'clock', type: 'clock', x: 0, y: 0, w: 12, h: 6, options: {} }] }],
        } });
      }
      if (url.pathname === '/agent/api/state') {
        return json(route, 200, { ts: Date.now(), evSeq: 0, events: [], pc: {}, cfgVersion: 1 });
      }
      return json(route, 200, { items: [], sessions: [], events: [], providers: [] });
    }
    const panel = url.pathname.startsWith('/kiosk/panel/');
    if (!panel && !url.pathname.startsWith('/kiosk/')) return route.fulfill({ status: 404, body: '' });
    const base = path.join(root, panel ? 'android/app/src/main/assets/www' : 'agent/PhoneBox.Agent/kiosk');
    const prefix = panel ? '/kiosk/panel/' : '/kiosk/';
    const file = url.pathname.slice(prefix.length) || 'index.html';
    const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml',
      '.png': 'image/png', '.webmanifest': 'application/manifest+json', '.woff2': 'font/woff2' };
    try {
      return route.fulfill({ status: 200, contentType: types[path.extname(file)] || 'application/octet-stream',
        body: await fs.readFile(path.join(base, file)) });
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
      return route.fulfill({ status: 404, body: '' });
    }
  });
  return state;
}

async function expectPanel(page) {
  await expect(page).toHaveURL(`${origin}/kiosk/`);
  await expect(page.frameLocator('iframe').locator(dashboard)).toBeVisible();
}

test('pairing consumes a fragment without submitting it until Connect and sends the required header', async ({ page }) => {
  const state = await kiosk(page);
  await page.goto(`${origin}/kiosk/`);
  await expect(page).toHaveURL(`${origin}/kiosk/pair.html`);
  await page.locator('#pairCode').fill('expired-code');
  await page.locator('#pairSubmit').click();
  await expect(page.locator('#error')).toContainText('recusado');
  await expect(page.locator('#pairSubmit')).toBeEnabled();

  // Also exercise an invitation opened while the pairing page is already visible (same-document navigation).
  await page.goto(`${origin}/kiosk/pair.html#code=${invitation}`);
  await expect(page).toHaveURL(`${origin}/kiosk/pair.html`);
  await expect(page.locator('#pairCode')).toHaveValue(invitation);
  expect(state.posts).toHaveLength(1);
  expect(state.requests.some((url) => url.includes(invitation))).toBe(false);
  await page.locator('#deviceName').fill('My iPhone');
  await page.locator('#pairSubmit').click();
  await expectPanel(page);
  expect(state.posts).toEqual([
    { path: '/kiosk/pair', header: '1', body: { code: 'expired-code', name: 'iPhone' } },
    { path: '/kiosk/pair', header: '1', body: { code: invitation, name: 'My iPhone' } },
  ]);
  expect(state.errors).toEqual([]);
});

test('a panel API 401 and a successful logout return the shell to pairing', async ({ page }) => {
  const state = await kiosk(page, { paired: true });
  await page.goto(`${origin}/kiosk/`);
  await expectPanel(page);
  state.paired = false;
  // A 401 immediately removes the iframe. Observe the redirect and served refusal, not its response event.
  await expect(page).toHaveURL(`${origin}/kiosk/pair.html`);
  expect(state.deniedApiRequests.length).toBeGreaterThan(0);
  await expect(page.locator('iframe')).toHaveCount(0);

  await page.locator('#pairCode').fill(invitation);
  await page.locator('#pairSubmit').click();
  await expectPanel(page);
  await page.locator('#detailsToggle').click();
  await page.locator('#logout').click();
  await expect(page).toHaveURL(`${origin}/kiosk/pair.html`);
  expect(state.paired).toBe(false);
  expect(state.posts.at(-1)).toEqual({ path: '/kiosk/logout', header: '1', body: {} });
  await page.goto(`${origin}/kiosk/`);
  await expect(page).toHaveURL(`${origin}/kiosk/pair.html`);
  expect(state.errors).toEqual([]);
});

test('session names render as text and the real panel never persists its fetched configuration', async ({ page }) => {
  const name = '<img src=x onerror="window.__kioskInjected=1">';
  const state = await kiosk(page, { paired: true, name });
  await page.addInitScript(() => {
    window.__localStorageWrites = [];
    const setItem = Storage.prototype.setItem;
    Storage.prototype.setItem = function (key, value) {
      if (this === window.localStorage) window.__localStorageWrites.push(key);
      return setItem.call(this, key, value);
    };
  });
  await page.goto(`${origin}/kiosk/`);
  await expectPanel(page);
  await page.locator('#detailsToggle').click();
  await expect(page.locator('#sessionName')).toHaveText(name);
  await expect(page.locator('#sessionName img')).toHaveCount(0);
  await expect(page.locator('#capabilities')).toHaveText('Monitoramento');
  expect(await page.evaluate(() => window.__kioskInjected)).toBeUndefined();
  const frame = page.frames().find((candidate) => candidate.url() === `${origin}/kiosk/panel/`);
  // The shared language preference is non-sensitive; fetched layouts and widget data must not be persisted.
  expect(await frame.evaluate(() => ({ config: localStorage.getItem('cfg'), writes: window.__localStorageWrites.filter((key) => key !== 'pb-lang') })))
    .toEqual({ config: null, writes: [] });
  expect(state.posts).toEqual([]);
  expect(state.errors).toEqual([]);
});

async function timedPanel(page) {
  await page.clock.install();
  const state = await kiosk(page, { paired: true });
  await page.goto(`${origin}/kiosk/`);
  await expectPanel(page);
  await page.clock.pauseAt(await page.evaluate(() => Date.now() + 100));
  if (await page.locator('#showToolbar').isVisible()) await page.locator('#showToolbar').click();
  // Start the idle period at a user interaction, independent of asset loading time under parallel test load.
  await page.locator('#connection').click();
  return state;
}

test('idle controls hide after five seconds and a tap restores them with a new idle period', async ({ page }) => {
  const state = await timedPanel(page);
  const toolbar = page.locator('#toolbar');
  const reveal = page.locator('#showToolbar');
  const panelLoads = () => state.requests.filter((url) => url === `${origin}/kiosk/panel/`).length;
  expect(panelLoads()).toBe(1);
  await page.clock.runFor(4000);
  await expect(toolbar).toBeVisible();
  await page.clock.runFor(1500);
  await expect(toolbar).toBeHidden();
  await expect(reveal).toHaveAccessibleName('Mostrar controles do painel');
  expect((await reveal.boundingBox()).height).toBeGreaterThanOrEqual(44);
  const panel = await page.locator('#panelHost').boundingBox();
  expect(panel.y).toBe(0);
  expect(panel.height).toBe(844);

  await reveal.click();
  await expect(toolbar).toBeVisible();
  await expect(reveal).toBeHidden();
  await page.clock.runFor(4000);
  await expect(toolbar).toBeVisible();
  await page.clock.runFor(1500);
  await expect(toolbar).toBeHidden();
  expect(panelLoads()).toBe(1);
  expect(state.errors).toEqual([]);
});

test('open details, keyboard focus, and a held pointer keep controls visible', async ({ page }) => {
  const state = await timedPanel(page);
  const toolbar = page.locator('#toolbar');
  const details = page.locator('#connectionDetails');
  const clock = page.frameLocator('iframe').locator(dashboard);
  await page.locator('#detailsToggle').click();
  await clock.click();
  await page.clock.runFor(6000);
  await expect(details).toBeVisible();
  await expect(toolbar).toBeVisible();

  await page.locator('#detailsToggle').click();
  await page.keyboard.press('Shift');
  await page.locator('#detailsToggle').focus();
  await expect(page.locator('#toolbar :focus')).toHaveCount(1);
  await page.clock.runFor(6000);
  await expect(toolbar).toBeVisible();

  await clock.click();
  const status = await page.locator('#connection').boundingBox();
  await page.mouse.move(status.x + status.width / 2, status.y + status.height / 2);
  await page.mouse.down();
  await page.clock.runFor(6000);
  await expect(toolbar).toBeVisible();
  await page.mouse.up();
  await page.clock.runFor(5500);
  await expect(toolbar).toBeHidden();

  await page.keyboard.press('Tab');
  await expect(toolbar).toBeVisible();
  await expect(page.locator('#toolbar :focus')).toHaveCount(1);
  await page.clock.runFor(6000);
  await expect(toolbar).toBeVisible();
  expect(state.errors).toEqual([]);
});

test('offline and failed session checks reveal controls until the connection recovers', async ({ page }) => {
  const state = await timedPanel(page);
  const toolbar = page.locator('#toolbar');
  await page.clock.runFor(5500);
  await expect(toolbar).toBeHidden();
  await page.evaluate(() => window.dispatchEvent(new Event('offline')));
  await expect(toolbar).toBeVisible();
  await expect(page.locator('#error')).toBeVisible();
  await page.clock.runFor(6000);
  await expect(toolbar).toBeVisible();

  state.sessionStatus = 503;
  const failed = page.waitForResponse(`${origin}/kiosk/session`);
  await page.evaluate(() => window.dispatchEvent(new Event('online')));
  expect((await failed).status()).toBe(503);
  await expect(page.locator('#connection')).toHaveText('Conexão interrompida');
  await page.clock.runFor(5500);
  await expect(toolbar).toBeVisible();

  state.sessionStatus = 200;
  await page.evaluate(() => window.dispatchEvent(new Event('online')));
  await expect(page.locator('#connection')).toHaveText('Conectado');
  await expect(page.locator('#error')).toBeHidden();
  await page.clock.runFor(5500);
  await expect(toolbar).toBeHidden();
  expect(state.errors).toEqual([]);
});
