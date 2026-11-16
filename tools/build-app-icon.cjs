// Render the editable SVG into Windows ICO frames. Dev-only: npm install playwright.
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

(async () => {
  const root = path.resolve(__dirname, '..');
  const svg = await fs.readFile(path.join(root,'Assets/app.svg'),'utf8');
  const browser = await chromium.launch({headless:true,executablePath:process.env.BROWSER_PATH});
  try {
    const page = await browser.newPage({deviceScaleFactor:1});
    const sizes = [16,20,24,32,40,48,64,128,256], frames=[];
    for (const size of sizes) {
      await page.setViewportSize({width:size,height:size});
      await page.setContent('<style>html,body{margin:0;background:transparent}svg{width:100vw;height:100vh;display:block}</style>'+svg);
      const png=await page.screenshot({omitBackground:true}); frames.push(png);
      if (size===256) await fs.writeFile(path.join(root,'Assets/app.png'),png);
    }
    const header = Buffer.alloc(6+16*sizes.length);
    header.writeUInt16LE(1,2);header.writeUInt16LE(sizes.length,4);
    let offset=header.length;
    sizes.forEach((size,i)=>{
      const at=6+16*i;header[at]=header[at+1]=size===256?0:size;
      header.writeUInt16LE(1,at+4);header.writeUInt16LE(32,at+6);
      header.writeUInt32LE(frames[i].length,at+8);header.writeUInt32LE(offset,at+12);
      offset+=frames[i].length;
    });
    await fs.writeFile(path.join(root,'Assets/app.ico'),Buffer.concat([header,...frames]));
    if(process.argv[2]){
      await page.setViewportSize({width:760,height:310});
      const tile=(color)=>'<div style="display:flex;align-items:center;gap:24px;padding:24px;background:'+color+'">'+[16,24,32,48,64,96].map(size=>'<div style="text-align:center;font:11px sans-serif;color:#96a0ac"><div style="width:'+size+'px;height:'+size+'px;margin:0 auto 12px">'+svg.replace('<svg ','<svg style="width:100%;height:100%" ')+'</div>'+size+'px</div>').join('')+'</div>';
      await page.setContent('<style>html,body{margin:0}body{background:#14171a}</style>'+tile('#14171a')+tile('#f3f5f7'));
      await page.screenshot({path:path.resolve(process.argv[2])});
    }
    console.log('App icon written: 9 sizes (16–256px), ICO + PNG + editable SVG.');
  } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
