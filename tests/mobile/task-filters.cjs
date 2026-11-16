// Run after FloorRegression --tasks-only <output> emits the real desktop protocol fixture.
// PLAYWRIGHT_MODULE may point to an existing Playwright install; BROWSER_PATH may select Edge.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');
const assert = require('node:assert/strict');

(async () => {
  const output = path.resolve(process.argv[2]);
  const assets = path.resolve(__dirname, '../../Assets');
  let state = JSON.parse(await fs.readFile(path.join(output, 'task-state.json'), 'utf8'));
  const tasks = structuredClone(state.tasks);
  const actions = [], errors = [];
  let checks = 0;
  const check = (value, label) => { assert(value, label); checks++; };
  const server = http.createServer(async (req, res) => {
    try {
      const url = new URL(req.url, 'http://localhost');
      if (url.pathname === '/api/state') { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(state)); return; }
      if (url.pathname === '/api/action') {
        let body = ''; for await (const chunk of req) body += chunk;
        const action = JSON.parse(body); actions.push(action);
        res.end('{}');
        // Real HTTP endpoint acknowledges enqueueing, before the next UI timer applies the action.
        setTimeout(() => {
          if (action.type === 'setTaskFilter') {
            const { field, value } = JSON.parse(action.value);
            if (field === 'reset') state.taskFilters = { search: '', region: 'current', status: 1, trader: '', category: '', playerLevel: null, faction: '', kappa: null, lightkeeper: null };
            else state.taskFilters[field] = field === 'status' ? Number(value) : field === 'playerLevel' ? value ? Number(value) : null : ['kappa','lightkeeper'].includes(field) ? value === '' ? null : value === 'true' : value;
          } else if (action.type === 'selectTask') {
            state.sel = action.value || null;
            state.detail = { obj: ['测试目标：核对任务详情'], items: [] };
            state.taskDestinations = [{ key: 'customs', name: '海关' }];
          }
          state.rev++;
        }, 600);
        return;
      }
      let file;
      if (url.pathname.startsWith('/img/')) {
        const [folder, stem] = url.pathname.slice(5).split('/').map(decodeURIComponent);
        const base = path.join(assets, 'maps', folder, stem);
        file = await fs.access(base + '.jpg').then(() => base + '.jpg').catch(() => base + '.png');
      } else file = path.join(assets, 'mobile', url.pathname === '/' ? 'index.html' : path.basename(url.pathname));
      const type = { '.js':'text/javascript', '.css':'text/css', '.html':'text/html', '.json':'application/json', '.jpg':'image/jpeg', '.png':'image/png' }[path.extname(file)];
      res.setHeader('Content-Type', (type || 'application/octet-stream') + '; charset=utf-8');
      res.end(await fs.readFile(file));
    } catch { res.statusCode = 404; res.end(); }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ headless: true, executablePath: process.env.BROWSER_PATH });
  try {
    const context = await browser.newContext({ viewport: { width: 360, height: 800 }, deviceScaleFactor: 2 });
    const page = await context.newPage(), peer = await context.newPage();
    for (const p of [page, peer]) p.on('pageerror', error => errors.push(error.message));
    const address = 'http://127.0.0.1:' + server.address().port;
    await Promise.all([page.goto(address), peer.goto(address)]);
    await page.waitForFunction(() => document.querySelectorAll('.task').length > 100);
    await page.locator('#handle').click(); await peer.locator('#handle').click();
    check(await page.locator('.task-tag').count() > 1000, 'Shared task tags did not render');
    await page.locator('#taskFilterToggle').click();
    check(await page.locator('#taskAdvanced').isVisible(), 'Advanced filters did not expand');
    check(await page.evaluate(() => [...document.querySelectorAll('#taskFilters select,.filter-input,.filter-button')]
      .filter(el => el.getBoundingClientRect().height).every(el => el.getBoundingClientRect().height >= 44)), 'Touch target smaller than 44px');
    const trader = state.taskOptions.traders[1].value;
    await page.locator('#taskTrader').selectOption(trader);
    await peer.waitForFunction(value => document.getElementById('taskTrader').value === value, trader);
    check(state.taskFilters.trader === trader, 'Phone filter did not reach shared state');
    state.taskFilters.faction = 'USEC'; state.rev++;
    await page.waitForFunction(() => document.getElementById('taskFaction').value === 'USEC');
    check(await peer.locator('#taskFaction').inputValue() === 'USEC', 'Desktop-side changes did not reach both clients');

    const search = page.locator('#taskSearch');
    await search.scrollIntoViewIfNeeded(); await search.focus();
    const before = actions.length;
    await search.dispatchEvent('compositionstart'); await search.fill('医疗包 KEY');
    await page.waitForTimeout(300);
    check(actions.length === before, 'Half-composed IME text was sent');
    await search.dispatchEvent('compositionend');
    await search.evaluate(el => el.setSelectionRange(1, 1));
    await page.waitForTimeout(400);
    check(await search.inputValue() === '医疗包 KEY', 'Old poll overwrote a pending search');
    check(await search.evaluate(el => el.selectionStart === 1 && document.activeElement === el), 'Poll reset edit focus or caret');
    await peer.waitForFunction(() => document.getElementById('taskSearch').value === '医疗包 KEY');
    check(actions.filter(a => a.type === 'setTaskFilter' && JSON.parse(a.value).field === 'search').length === 1, 'Debounce sent duplicate searches');

    await page.locator('#taskLevel').fill('20');
    await peer.waitForFunction(() => document.getElementById('taskLevel').value === '20');
    check(state.taskFilters.playerLevel === 20, 'Level filter did not synchronize');
    await page.locator('#taskKappa').selectOption('true');
    await peer.waitForFunction(() => document.getElementById('taskKappa').value === 'true');
    check(state.taskFilters.kappa === true, 'Kappa filter did not synchronize');

    const firstId = state.tasks[0].id;
    await page.locator('#taskFilterToggle').click();
    await page.locator('.task').first().click();
    await page.waitForFunction(() => document.querySelector('#taskDetail button')?.textContent === '前往海关');
    check(actions.some(a => a.type === 'selectTask' && a.value === firstId) && !actions.some(a => a.type === 'setMap'), 'Task selection automatically changed maps');
    await page.locator('#taskDetail button').click();
    await page.waitForFunction(() => true);
    await page.waitForTimeout(100);
    check(actions.some(a => a.type === 'jumpTaskMap' && a.value === 'customs'), 'Explicit map destination action missing');

    state.tasks[0].tags.push('最低 99 级'); state.rev++;
    await page.waitForFunction(() => [...document.querySelectorAll('.task-tag')].some(el => el.textContent === '最低 99 级'));
    check(true, 'Same-ID metadata changes rerender tags');
    await page.locator('#taskList').evaluate(el => el.scrollTop = 300);
    state.rev++; await page.waitForTimeout(450);
    check(await page.locator('#taskList').evaluate(el => el.scrollTop >= 290), 'Unrelated state polling reset task scroll');

    for (const [width, height] of [[360,800],[320,640]]) {
      await page.setViewportSize({ width, height });
      await page.locator('#taskSearch').scrollIntoViewIfNeeded();
      check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Page overflows horizontally at ' + width);
      check(await page.locator('#taskList').evaluate(el => el.clientHeight > 60), 'Task list collapsed at ' + width);
      await page.screenshot({ path: path.join(output, 'task-panel-mobile-' + width + '.png') });
    }
    state.taskStates = { 'hidden-by-search': 1 }; state.tasks = []; state.sel = null; state.rev++;
    await page.waitForFunction(() => S.taskIndex['hidden-by-search'] === 1 && S.data.tasks.length === 0);
    check(await page.locator('#taskList button').textContent() === '清除筛选', 'Empty results have no recovery action');
    await page.locator('#taskReset').click();
    await peer.waitForFunction(() => document.getElementById('taskRegion').value === 'current' && document.getElementById('taskSearch').value === '');
    check(state.taskFilters.status === 1 && state.taskFilters.playerLevel === null, 'Reset did not restore defaults');
    state = { ...state, map: null, tasks: [], zones: [], marks: [] }; state.rev++;
    await page.waitForTimeout(500);
    check(errors.length === 0, 'Browser errors: ' + errors.join('; '));
    console.log('PASS: ' + checks + ' mobile task filters, IME, synchronization, metadata tags and narrow-screen checks.');
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
