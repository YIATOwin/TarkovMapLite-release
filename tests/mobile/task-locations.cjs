// Run after FloorRegression --locations-only <output> emits locations-state.json.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');
const assert = require('node:assert/strict');

(async () => {
  const output = path.resolve(process.argv[2]), assets = path.resolve(__dirname, '../../Assets');
  const state = JSON.parse(await fs.readFile(path.join(output, 'locations-state.json'), 'utf8'));
  let checks = 0;
  const errors = [];
  const check = (ok, label) => { assert(ok, label); checks++; };
  const server = http.createServer(async (req, res) => {
    try {
      const url = new URL(req.url, 'http://localhost');
      if (url.pathname === '/api/state') { res.setHeader('Content-Type','application/json'); res.end(JSON.stringify(state)); return; }
      let file;
      if (url.pathname.startsWith('/img/')) {
        const [folder, stem] = url.pathname.slice(5).split('/').map(decodeURIComponent);
        const base = path.join(assets, 'maps', folder, stem);
        file = await fs.access(base+'.jpg').then(()=>base+'.jpg').catch(()=>base+'.png');
      } else file = path.join(assets, 'mobile', url.pathname === '/' ? 'index.html' : path.basename(url.pathname));
      res.setHeader('Content-Type', ({'.js':'text/javascript','.css':'text/css','.html':'text/html','.png':'image/png','.jpg':'image/jpeg'}[path.extname(file)] || 'application/octet-stream'));
      res.end(await fs.readFile(file));
    } catch { res.statusCode=404; res.end(); }
  });
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  const browser = await chromium.launch({headless:true, executablePath:process.env.BROWSER_PATH});
  try {
    const page = await browser.newPage({viewport:{width:360,height:800}});
    page.on('pageerror', e => errors.push(e.message));
    await page.goto('http://127.0.0.1:'+server.address().port);
    await page.waitForFunction(()=>S.data?.zones?.some(z=>z.i==='59675ea386f77414b32bded2'));
    check(await page.evaluate(()=>S.data.map.key==='factory' && S.data.zones.find(z=>z.i==='59675ea386f77414b32bded2').f===0),'Real Postman coordinate or floor missing from mobile snapshot');
    const rendering = await page.evaluate(() => {
      const zone = S.data.zones.find(z=>z.i==='59675ea386f77414b32bded2');
      const before = {scale:S.scale, tx:S.tx, ty:S.ty, w:S.w, h:S.h, sel:S.sel, onlyActive:S.onlyActive, taskIndex:S.taskIndex};
      S.scale=1; S.tx=S.ty=0; S.w=360; S.h=800; S.sel=null; S.onlyActive=true;
      const p=[90,190,110,190,110,210,90,210];
      const fake={...S.data,zones:[{...zone,p},{...zone,p:p.map((v,i)=>v+(i%2===0?100:0))}],sel:zone.i};
      function pixels(status,selected,connector=false) {
        S.taskIndex={[zone.i]:status}; ctx.setTransform(1,0,0,1,0,0);ctx.clearRect(0,0,360,800);
        const data={...fake,sel:selected?zone.i:null};
        if(connector)drawConnectors(data);else drawZones(data);
        const pixels=ctx.getImageData(0,0,360,800).data;
        let count=0;for(let i=3;i<pixels.length;i+=4)if(pixels[i])count++;
        return count;
      }
      const result={unlogged:pixels(0,true),completed:pixels(2,true),failed:pixels(3,true),hidden:pixels(0,false),active:pixels(1,false),link:pixels(0,true,true),hiddenLink:pixels(0,false,true)};
      Object.assign(S,before);draw();return result;
    });
    check(rendering.unlogged>50,'Selected unlogged task not painted');
    check(rendering.completed>50 && rendering.failed>50,'Selected completed/failed task not painted');
    check(rendering.hidden===0 && rendering.active>50,'Deselecting failed to restore active-only filter');
    check(rendering.link>50 && rendering.hiddenLink===0,'Connector visibility disagrees with selected polygons');
    await page.screenshot({path:path.join(output,'postman-mobile.png')});
    check(errors.length===0,'Browser errors: '+errors.join('; '));
    console.log('PASS: '+checks+' mobile task coordinate, floor, selected state, active-only reset and connector rendering checks.');
  } finally {await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
