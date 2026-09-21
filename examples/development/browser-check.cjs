// Headed Playwright fixture for scripts/test-development.ps1, run inside Anode's seat via `anode exec` (needs a desktop lease), never on the parent desktop.
// Arguments: <folder containing node_modules/playwright> <output folder>. After browser-ready.json it waits up to 30 s for a release-browser marker from the harness.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const { chromium } = require(path.join(process.argv[2], 'node_modules', 'playwright'));
const output = process.argv[3];
const html = `<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>Anode development fixture</title><style>
body{margin:0;background:#101923;color:#e9f1f9;font:18px system-ui}main{max-width:720px;padding:40px;margin:auto}
h1{font-size:38px}label,input,button{display:block}input,button{box-sizing:border-box;font:inherit;padding:14px;border-radius:8px;margin:12px 0;max-width:100%;width:100%}
button{background:#92e7c4;color:#112b25;border:0;cursor:pointer}canvas{width:100%;border-radius:12px;background:#1d3042}
small{color:#9fb6ca}#result{min-height:28px}</style><main><small>ANODE / APPLICATION TEST</small><h1>A browser in the background seat</h1>
<p>Form input, HTTP requests and canvas rendering, while the main desktop stays available.</p>
<form><label for="name">Build name</label><input id="name" required><button>Run preview</button></form><p id="result" role="status">Ready for a test.</p>
<canvas width="640" height="180" aria-label="Preview chart"></canvas></main><script>
const context=document.querySelector('canvas').getContext('2d'); context.fillStyle='#92e7c4';
[44,96,65,127,142,108,157].forEach((h,i)=>context.fillRect(24+i*87,170-h,58,h));
document.querySelector('form').addEventListener('submit',async event=>{event.preventDefault();
const response=await fetch('/preview?name='+encodeURIComponent(document.querySelector('input').value));
document.querySelector('#result').textContent=await response.text();});</script></html>`;
async function main() {
  fs.mkdirSync(output, { recursive: true });
  const server = http.createServer((request, response) => {
    const url = new URL(request.url, 'http://localhost');
    if (url.pathname === '/preview') { response.end('Preview complete: ' + url.searchParams.get('name')); return; }
    response.setHeader('Content-Type', 'text/html; charset=utf-8'); response.end(html);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ channel: 'chrome', headless: false, args: ['--force-renderer-accessibility'] });
    const context = await browser.newContext({ viewport: { width: 1000, height: 650 } });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const address = `http://127.0.0.1:${server.address().port}`;
    await page.goto(address);
    assert.equal(await page.title(), 'Anode development fixture');
    await page.getByLabel('Build name').fill('Anode background build');
    await page.getByRole('button', { name: 'Run preview' }).click();
    await page.getByRole('status').filter({ hasText: 'Preview complete: Anode background build' }).waitFor();
    await page.screenshot({ path: path.join(output, 'browser.png') });
    await page.setViewportSize({ width: 390, height: 780 });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await page.screenshot({ path: path.join(output, 'browser-mobile.png') });
    assert.deepEqual(errors, []);
    await page.setViewportSize({ width: 1000, height: 650 });
    await page.getByLabel('Build name').focus();
    const report = { passed: true, nodePid: process.pid, address, checks: ['headed Chrome in seat', 'isolated browser context', 'form input', 'HTTP preview response', 'canvas screenshot', 'mobile layout', 'no page errors'] };
    fs.writeFileSync(path.join(output, 'browser-ready.json'), JSON.stringify(report, null, 2));
    // Give the parent test harness time to verify the native window's session and
    // capture it through Anode. This marker is only a fixture coordination signal.
    const deadline = Date.now() + 30000;
    while (!fs.existsSync(path.join(output, 'release-browser')) && Date.now() < deadline) await new Promise(r => setTimeout(r, 100));
    if (fs.existsSync(path.join(output, 'verify-input'))) {
      assert.equal(await page.getByLabel('Build name').inputValue(), 'Anode raw input confirmed');
      await page.screenshot({ path: path.join(output, 'browser-input.png') });
      report.checks.push('Anode mouse and keyboard reached the form');
    }
    console.log(JSON.stringify(report));
  } finally { if (browser) await browser.close(); await new Promise(resolve => server.close(resolve)); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
