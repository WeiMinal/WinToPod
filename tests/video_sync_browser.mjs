// SPDX-License-Identifier: GPL-3.0-or-later
// Real browser/VP8/compositor test. Only the extension message transport is mocked.
import { createRequire } from 'node:module';
import { createServer } from 'node:http';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const { PNG } = require('pngjs');
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const fixture = await readFile(resolve(root, 'tests/fixtures/video-counter.webm'));
const checks = [], measurements = [], errors = [];
function check(name, result) { if (!result) throw Error(name); checks.push(name); }
const html = `<!doctype html><html><head><style>body{margin:0}.wrap{position:relative;width:640px;height:360px}video{position:absolute;z-index:4;width:640px;height:360px}</style></head><body><div class="wrap"><video src="/counter.webm" muted autoplay playsinline></video></div></body></html>`;
const server = createServer((req, res) => {
  res.setHeader('Content-Type', req.url === '/counter.webm' ? 'video/webm' : 'text/html');
  res.end(req.url === '/counter.webm' ? fixture : html);
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
let browser;
const readCounter = (data, width, scale) => {
  let n = 0;
  for (let bit = 0; bit < 8; bit++) if (data[((90 * scale) * width + (40 * bit + 20) * scale) * 4] > 128) n |= 1 << bit;
  return n;
};
try {
  browser = await chromium.launch({ executablePath: process.env.WINTOPOD_BROWSER, headless: true,
    args: ['--no-sandbox', '--disable-dev-shm-usage', '--autoplay-policy=no-user-gesture-required'] });
  const page = await browser.newPage({ viewport: { width: 720, height: 440 }, deviceScaleFactor: 1 });
  page.on('pageerror', e => errors.push(String(e)));
  await page.addInitScript(() => {
    window.testAudio = { connected: true, delayMs: 2000 }; window.testReports = [];
    window.chrome = { runtime: {
      sendMessage: async m => { if (m.report) window.testReports.push(m.report); return window.testAudio; },
      onMessage: { addListener() {} }
    } };
  });
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
  await page.waitForFunction(() => document.querySelector('video').readyState >= 2);
  check('real VP8 codec APIs available', await page.evaluate(() => ['VideoFrame', 'VideoEncoder', 'VideoDecoder'].every(k => k in window)));
  await page.addScriptTag({ path: resolve(root, 'browser/WinToPod.VideoSync/content.js') });
  await page.waitForSelector('canvas[data-wintopod-sync="active"]', { timeout: 12000 });
  for (let i = 0; i < 6; i++) {
    await page.waitForTimeout(240);
    measurements.push(await page.evaluate(() => {
      const v = document.querySelector('video'), c = document.querySelector('canvas[data-wintopod-sync]');
      const t = document.createElement('canvas'); t.width = 320; t.height = 180;
      const x = t.getContext('2d'); x.drawImage(v, 0, 0, 320, 180);
      const value = (ctx) => { let n = 0; for (let b = 0; b < 8; b++) if (ctx.getImageData(b * 40 + 20, 90, 1, 1).data[0] > 128) n |= 1 << b; return n; };
      const current = value(x), delayed = value(c.getContext('2d'));
      return { current, delayed, lagFrames: (current - delayed + 256) % 256 };
    }));
  }
  check('six real encoded frame counters delayed by two seconds', measurements.every(m => m.lagFrames >= 56 && m.lagFrames <= 65));
  check('overlay participates above positioned video', await page.evaluate(() => {
    const v = document.querySelector('video'), c = document.querySelector('canvas[data-wintopod-sync]');
    return getComputedStyle(c).zIndex === getComputedStyle(v).zIndex && c.previousElementSibling === v;
  }));
  const current = await page.evaluate(() => {
    const c = document.createElement('canvas'); c.width = 320; c.height = 180;
    const x = c.getContext('2d'); x.drawImage(document.querySelector('video'), 0, 0, 320, 180);
    let n = 0; for (let b = 0; b < 8; b++) if (x.getImageData(b * 40 + 20, 90, 1, 1).data[0] > 128) n |= 1 << b; return n;
  });
  const png = PNG.sync.read(await page.screenshot({ clip: { x: 0, y: 0, width: 640, height: 360 } }));
  const composited = readCounter(png.data, png.width, 2), compositedLag = (current - composited + 256) % 256;
  check('actual composited screenshot contains delayed pixels', compositedLag >= 55 && compositedLag <= 65);
  check('heartbeat requires presented frames and observed delay', await page.evaluate(() => window.testReports.some(r => r.phase === 'active' && r.framesPresented > 0 && r.observedDelayMs >= 1980)));
  const canvas = await page.$('canvas[data-wintopod-sync]');
  await page.evaluate(() => window.testAudio.delayMs = 2150); await page.waitForTimeout(1200);
  check('live delay update preserves existing pipeline', await canvas.evaluate(c => c.isConnected && c.dataset.wintopodSync === 'active'));
  await page.evaluate(() => document.querySelector('video').pause()); await page.waitForTimeout(2450);
  check('pause drains delayed frames and retains last image', await canvas.evaluate(c => c.isConnected && c.dataset.wintopodSync === 'active'));
  await page.evaluate(() => { const v = document.querySelector('video'); v.currentTime = 9; return v.play(); });
  await page.waitForTimeout(2700);
  check('seek resumes real codec presentation', await canvas.evaluate(c => c.isConnected && c.dataset.wintopodSync === 'active'));
  await page.evaluate(() => window.testAudio = { connected: false, delayMs: 0 }); await page.waitForTimeout(800);
  check('disconnect releases overlay without browser errors', await page.locator('canvas[data-wintopod-sync]').count() === 0 && errors.length === 0);
  const report = { passed: true, checks, frame_counter_measurements: measurements,
    composited_screenshot_frame_lag: compositedLag, browser_version: browser.version(),
    actual_browser_and_codec_tested: true, full_mv3_extension_tested: false,
    extension_transport_mocked: true, live_bilibili_tested: false,
    windows_execution_tested: false, real_homepod_acoustic_sync_tested: false };
  const serialized = JSON.stringify(report, null, 2) + '\n';
  const output = process.env.WINTOPOD_VALIDATION_DIRECTORY || resolve(root, 'artifacts/validation');
  await mkdir(output, { recursive: true }); await writeFile(resolve(output, 'video-browser.json'), serialized); process.stdout.write(serialized);
} finally {
  await browser?.close(); await new Promise(r => server.close(r));
}
