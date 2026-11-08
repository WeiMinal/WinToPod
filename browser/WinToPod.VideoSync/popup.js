// SPDX-License-Identifier: GPL-3.0-or-later
const status = document.querySelector('#status'), details = document.querySelector('#videoStatus');
const metrics = document.querySelector('#metrics'), enable = document.querySelector('#enable'), disable = document.querySelector('#disable');
const phases = { idle: '等待音箱连接', waiting: '等待视频播放', buffering: '正在缓冲画面', active: '画面补偿正在运行', paused: '视频已暂停' };
const reasons = {
  'no-video': '未找到可处理的视频，请先播放视频。', drm: '此视频受保护，已保留原画面。',
  'native-controls': '此视频使用原生控制条，暂不支持补偿。', 'no-codec': '浏览器缺少所需的视频编码能力，请更新浏览器。',
  'cross-origin': '浏览器不允许读取此视频画面。', 'codec-error': '视频处理失败，已恢复原画面。',
  'buffer-limit': '画面缓冲达到上限，已恢复原画面。', fullscreen: '画中画或视频原生全屏暂不支持，可退出后重试。',
  hidden: '页面在后台，返回页面后重新缓冲。'
};
let origin, id, tabId, busy = false, refreshing = false, alive = true;
enable.disabled = disable.disabled = true;
async function refresh() {
  if (!alive || refreshing || busy || !origin) return;
  refreshing = true;
  try {
    const disabled = (await chrome.storage.local.get('disabledOrigins')).disabledOrigins || [];
    const paused = disabled.includes(origin);
    const permitted = await chrome.permissions.contains({origins: [origin + '/*']});
    const state = await chrome.runtime.sendMessage({type: 'WinToPod.status', dark: matchMedia('(prefers-color-scheme: dark)').matches});
    if (!alive || busy) return;
    status.textContent = state.bridgeReachable === false ? '主程序未响应' : state.protocolCompatible === false ? '主程序与助手协议不兼容' : state.connected ? '已连接音箱' : '主程序在线 · 未连接音箱';
    metrics.textContent = state.connected ? `建议画面延后：${state.delayMs} 毫秒` : '';
    enable.textContent = paused ? '恢复此网站' : permitted ? '重新检测此页面' : '为此网站启用';
    disable.disabled = paused || !permitted;
    if (paused) { details.textContent = '此网站已停用，保留原画面。'; return; }
    if (!permitted) { details.textContent = '此网站尚未授权，点击启用后由浏览器确认。'; return; }
    let report;
    try { report = await chrome.tabs.sendMessage(tabId, {type: 'WinToPod.companion-status'}); }
    catch { details.textContent = '当前页面尚未检测到助手。可重新检测；若仍无响应，请刷新页面。'; return; }
    if (!alive || busy) return;
    if (!state.connected) {
      details.textContent = state.bridgeReachable === false ? '请打开 WinToPod，并启用视频同步。' : state.protocolCompatible === false ? '请重新加载与主程序配套的助手。' : '助手已就绪，连接音箱后自动开始。';
      return;
    }
    details.textContent = reasons[report?.reason] || phases[report?.phase] || '尚未获得有效画面状态。';
    if (['active', 'paused'].includes(report?.phase) && Number.isInteger(report.observedDelayMs) && report.observedDelayMs >= 0 && Number.isInteger(report.framesPresented) && report.framesPresented > 0)
      metrics.textContent += `；已呈现 ${report.framesPresented} 帧，最近画面延后 ${report.observedDelayMs} 毫秒。`;
  } catch { if (alive) { status.textContent = '暂时无法读取助手状态'; details.textContent = '请重新打开扩展弹窗。'; metrics.textContent = ''; } }
  finally { refreshing = false; }
}
(async () => {
  try {
    const [tab] = await chrome.tabs.query({active: true, currentWindow: true});
    const url = new URL(tab?.url);
    if (!['https:', 'http:'].includes(url.protocol) || !Number.isInteger(tab.id)) throw Error();
    origin = url.origin; tabId = tab.id;
    id = 'WinToPod-' + [...new TextEncoder().encode(origin)].map(x => x.toString(16).padStart(2, '0')).join('');
    enable.disabled = false;
    await refresh();
  } catch { status.textContent = '请在视频网页打开助手'; details.textContent = '浏览器内部页面不支持注入。'; }
})();
enable.onclick = async () => {
  if (!origin || busy) return;
  busy = true; enable.disabled = disable.disabled = true;
  try {
    if (!await chrome.permissions.request({origins: [origin + '/*']})) { details.textContent = '未获得网站授权，保留原画面。'; return; }
    const disabled = (await chrome.storage.local.get('disabledOrigins')).disabledOrigins || [];
    await chrome.storage.local.set({disabledOrigins: disabled.filter(x => x !== origin)});
    const host = new URL(origin).hostname;
    if (!(new URL(origin).protocol === 'https:' && (host === 'bilibili.com' || host.endsWith('.bilibili.com')))) {
      await chrome.scripting.unregisterContentScripts({ids: [id]}).catch(() => {});
      await chrome.scripting.registerContentScripts([{id, matches: [origin + '/*'], js: ['content.js'], allFrames: true, persistAcrossSessions: true, runAt: 'document_idle'}]);
    }
    await chrome.scripting.executeScript({target: {tabId, allFrames: true}, files: ['content.js']});
    details.textContent = '已启用，正在等待页面报告。';
  } catch { details.textContent = '此页面无法注入，请检查授权或刷新页面。'; }
  finally { busy = false; enable.disabled = false; }
};
disable.onclick = async () => {
  if (!origin || busy) return;
  busy = true;
  try {
    const values = (await chrome.storage.local.get('disabledOrigins')).disabledOrigins || [];
    await chrome.storage.local.set({disabledOrigins: [...new Set([...values, origin])]});
    await chrome.scripting.unregisterContentScripts({ids: [id]}).catch(() => {});
  } catch { details.textContent = '暂时无法停用此网站。'; }
  finally { busy = false; await refresh(); }
};
const poll = setInterval(refresh, 1000);
addEventListener('pagehide', () => { alive = false; clearInterval(poll); }, {once: true});
