const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');

(async () => {
  const output = path.resolve(process.argv[2]);
  const root = path.resolve(__dirname, '../../Assets');
  const enabled = JSON.parse(await fs.readFile(path.join(output, 'story-state.json'), 'utf8'));
  const off = structuredClone(enabled);
  off.taskFilters = { ...off.taskFilters, showStory:false, trader:'', status:1 };
  off.zones = off.zones.filter(z => !z.story); off.sel = null; off.tasks = [];
  off.detail = { obj: [], items: [] }; off.taskSummary = '主线默认关闭';
  let state = structuredClone(off), checks = 0;
  const actions = [], errors = [];
  const check = (value, label) => { assert(value, label); checks++; };
  const server = http.createServer(async (req,res) => {
    try {
      const url = new URL(req.url,'http://localhost');
      if (url.pathname === '/api/state') { res.setHeader('Content-Type','application/json'); res.end(JSON.stringify(state)); return; }
      if (url.pathname === '/api/action') {
        let body=''; for await (const chunk of req) body += chunk;
        const action = JSON.parse(body); actions.push(action);
        if (action.type === 'setTaskFilter') {
          const {field,value} = JSON.parse(action.value);
          // Real desktop-generated snapshots, not a duplicate of its filtering predicate.
          if (field === 'showStory') state = structuredClone(value === 'true' ? enabled : off);
          if (field === 'storySeries') {
            state = structuredClone(enabled); state.taskFilters.storySeries = value;
            state.tasks = []; state.zones = state.zones.filter(z => !z.story); state.sel = null;
          }
        }
        state.rev++; res.end('{}'); return;
      }
      let file;
      if (url.pathname.startsWith('/img/')) {
        const [folder,stem]=url.pathname.slice(5).split('/').map(decodeURIComponent);
        const base=path.join(root,'maps',folder,stem);
        file=await fs.access(base+'.jpg').then(()=>base+'.jpg').catch(()=>base+'.png');
      } else file=path.join(root,'mobile',url.pathname==='/'?'index.html':path.basename(url.pathname));
      res.setHeader('Content-Type',({'.js':'text/javascript','.css':'text/css','.html':'text/html','.json':'application/json','.jpg':'image/jpeg','.png':'image/png'}[path.extname(file)]||'application/octet-stream'));
      res.end(await fs.readFile(file));
    } catch {res.statusCode=404;res.end();}
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const browser=await chromium.launch({headless:true,executablePath:process.env.BROWSER_PATH});
  try {
    const context=await browser.newContext({viewport:{width:360,height:800},deviceScaleFactor:2});
    const page=await context.newPage(), peer=await context.newPage();
    for (const p of [page,peer]) p.on('pageerror',error=>errors.push(error.message));
    await Promise.all([page.goto('http://127.0.0.1:'+server.address().port),peer.goto('http://127.0.0.1:'+server.address().port)]);
    await page.waitForFunction(()=>S.data?.taskFilters);
    await page.locator('#handle').click(); await peer.locator('#handle').click();
    check(await page.locator('#taskStoryToggle').textContent()==='主线：关闭','Story enabled by default');
    check(await page.locator('#taskStorySeries').isDisabled(),'Series selector not disabled initially');
    check(await page.evaluate(()=>!S.data.zones.some(z=>z.story)),'Default state contains story overlays');
    await page.locator('#taskStoryToggle').click();
    await peer.waitForFunction(()=>S.data?.taskFilters?.showStory===true);
    await page.waitForFunction(()=>document.querySelectorAll('.task').length>0);
    check(await peer.locator('#taskStoryToggle').textContent()==='主线：已开启','Manual activation did not synchronize');
    check(await page.locator('#taskTrader').inputValue()==='story','Story merchant not selected');
    check(await page.locator('#taskStatus').isDisabled(),'Automatic progress filter remains active in story mode');
    check(await page.locator('#taskStorySeries option').count()===10,'Story series missing');
    check(await page.locator('.task-tag').allTextContents().then(tags=>tags.includes('手动显示')&&!tags.includes('进行中')),'Story claims an automatic status');
    const colors=await page.evaluate(()=>{
      const zone=structuredClone(S.data.zones.find(z=>z.story));
      const fake={...S.data,zones:[{...zone,p:[90,90,110,90,110,110,90,110],f:0}],sel:null,map:{...S.data.map,floor:0}};
      const previous={scale:S.scale,tx:S.tx,ty:S.ty,w:S.w,h:S.h,sel:S.sel,onlyActive:S.onlyActive};
      S.scale=1;S.tx=S.ty=0;S.w=360;S.h=800;S.sel=null;S.onlyActive=true;
      function pixel(show){ctx.setTransform(1,0,0,1,0,0);ctx.clearRect(0,0,200,200);drawZones({...fake,taskFilters:{...fake.taskFilters,showStory:show}});return Array.from(ctx.getImageData(100,100,1,1).data);}
      const on=pixel(true),off=pixel(false);Object.assign(S,previous);draw();return {on,off};
    });
    check(colors.on[0]>240 && colors.on[1]>85 && colors.on[1]<130 && colors.on[2]<90,'Story is not orange-red with OnlyActive enabled');
    check(colors.off[3]===0,'Renderer reveals story without explicit enabled flag');
    await page.setViewportSize({width:320,height:640});
    await page.waitForTimeout(250);
    check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Story controls overflow narrow screen');
    await page.screenshot({path:path.join(output,'story-panel-mobile-320.png')});
    await page.locator('#handle').click(); await page.waitForTimeout(250);
    await page.screenshot({path:path.join(output,'story-map-mobile-320.png')});
    await page.locator('#handle').click(); await page.waitForTimeout(250);
    await page.locator('#taskStorySeries').selectOption('the-ticket');
    await peer.waitForFunction(()=>document.getElementById('taskStorySeries').value==='the-ticket');
    await page.waitForFunction(()=>S.data?.taskFilters?.storySeries==='the-ticket');
    check(await page.evaluate(()=>!S.data.zones.some(z=>z.story)),'Previous series markers were retained');
    await page.locator('#taskStoryToggle').click();
    await peer.waitForFunction(()=>S.data?.taskFilters?.showStory===false);
    await page.waitForFunction(()=>S.data?.taskFilters?.showStory===false);
    check(await peer.locator('#taskStorySeries').isDisabled(),'Disabling story did not synchronize');
    check(await page.evaluate(()=>!S.data.zones.some(z=>z.story)),'Disabling story left overlays');
    check(!actions.some(a=>a.type==='setMap'),'Story activation automatically changed the viewed map');
    check(errors.length===0,'Browser errors: '+errors.join('; '));
    console.log('PASS: '+checks+' mobile story opt-in, series, manual-only status, orange-red rendering and narrow-screen checks.');
  } finally {await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
