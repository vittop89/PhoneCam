'use strict';

const host = window.chrome.webview;
const $ = id => document.getElementById(id);
const canvas = $('video');
const ctx = canvas.getContext('2d', { alpha: false, desynchronized: true });

const MIN_PANEL_WIDTH = 340;
const MAX_PANEL_WIDTH = 600;
const MIN_STAGE_WIDTH = 320;
const settings = { cameraId: null, size: null, fps: 30, bitRate: 8000000, rotation: 0, mirror: false, fit: 'contain', panelCollapsed: false, panelWidth: MIN_PANEL_WIDTH };
let cameras = [];
let zoom = 1;
let pan = { x: 0, y: 0 };
let panStart = null;
let lastPanEnd = 0;
let maxZoom = 1;
let fullscreen = false;
let streaming = false;
let sessionSize = '';
// In full screen the panel starts hidden and floats over the image; the saved choice is for the window.
let panelOpenInFullscreen = false;
let noticeTimer = 0;
const supportedLanguages = ['en', 'it', 'fa'];
let language = 'en';
let translations = null;
let localeRequest = 0;
let statusMessage = { key: 'status.initializing' };
let noticeMessage = null;
let deviceModel = null;
let deviceKey = null;
let hiddenCameraCount = 0;

// ---------------------------------------------------------------- host messages

host.addEventListener('message', event => {
  const data = event.data;
  if (typeof data === 'string') {
    if (data.charCodeAt(0) === 86) onVideoPacket(data); // "V": video packet
    return;
  }
  handlers[data.type]?.(data);
});

function translation(key) {
  let value = translations;
  for (const part of key.split('.'))
    value = value?.[part];
  return typeof value === 'string' ? value : null;
}

function t(key, args = {}) {
  const template = translation(key);
  if (template === null) {
    console.error(`Missing translation: ${key}`);
    return key;
  }
  return template.replace(/\{(\w+)\}/g, (match, name) =>
    Object.hasOwn(args, name) ? String(args[name]) : match);
}

function localizedMessage(message) {
  const args = { ...message.args };
  if (typeof args.seconds === 'number')
    args.seconds = formatNumber(args.seconds);
  if (args.error && typeof args.error === 'object')
    args.error = args.error.key
      ? t(args.error.key, { detail: args.error.detail ?? '' })
      : args.error.detail ?? '';
  return t(message.key, args);
}

function updateStaticText() {
  for (const element of document.querySelectorAll('[data-i18n]'))
    element.textContent = t(element.getAttribute('data-i18n'));
  for (const element of document.querySelectorAll('[data-i18n-title]'))
    element.title = t(element.getAttribute('data-i18n-title'));
  for (const element of document.querySelectorAll('[data-i18n-aria-label]'))
    element.setAttribute('aria-label', t(element.getAttribute('data-i18n-aria-label')));
}

function updateStatus() {
  const text = localizedMessage(statusMessage);
  $('status').textContent = text;
  $('placeholderText').textContent = text;
}

function updateDevice() {
  $('device').textContent = deviceKey ? t(deviceKey) : deviceModel || t('device.none');
}

function updateRestoreCameras() {
  const button = $('restoreCameras');
  button.hidden = hiddenCameraCount === 0;
  button.textContent = hiddenCameraCount === 1
    ? t('camera.restoreOne')
    : t('camera.restoreMany', { count: formatNumber(hiddenCameraCount) });
}

async function applyLanguage(code, persist = false) {
  if (!supportedLanguages.includes(code))
    throw new Error(`Unsupported language: ${code}`);
  const request = ++localeRequest;
  const response = await fetch(`locales/${code}.json`);
  if (!response.ok)
    throw new Error(`Could not load locale ${code}: HTTP ${response.status}`);
  const dictionary = await response.json();
  if (request !== localeRequest)
    return;

  language = code;
  translations = dictionary;
  document.documentElement.lang = code;
  document.documentElement.dir = code === 'fa' ? 'rtl' : 'ltr';
  $('language').value = code;
  updateStaticText();
  updateStatus();
  updateDevice();
  updateRestoreCameras();
  if (noticeMessage)
    $('notice').textContent = localizedMessage(noticeMessage);
  if (cameras.length)
    fillCameraControls();
  syncViewControls();
  syncFullscreenButton();
  updateStats();
  if (persist)
    send({ type: 'language', language: code });
}

const handlers = {
  settings(m) {
    const previousCameraId = settings.cameraId;
    Object.assign(settings, m.settings);
    if (settings.cameraId !== previousCameraId)
      resetZoom();
    const savedPanelWidth = settings.panelWidth;
    syncPanelWidth();
    fillCameraControls();
    syncViewControls();
    syncPanel();
    if (settings.panelWidth !== savedPanelWidth)
      sendView();
  },
  notice(m) {
    noticeMessage = m;
    showNotice(localizedMessage(m));
  },
  status(m) {
    $('dot').dataset.state = m.state;
    statusMessage = m;
    updateStatus();
  },
  device(m) {
    deviceModel = m.model;
    deviceKey = m.key;
    updateDevice();
    if (!m.model) {
      cameras = [];
      hiddenCameraCount = 0;
      fillCameraControls();
      updateRestoreCameras();
    }
  },
  cameras(m) {
    cameras = m.items;
    fillCameraControls();
    syncZoomControls();
    hiddenCameraCount = m.hidden ?? 0;
    updateRestoreCameras();
  },
  session(m) {
    resetDecoder();
    streaming = true;
    sessionSize = `${formatNumber(m.width)}×${formatNumber(m.height)}`;
  },
  streamEnded() {
    streaming = false;
    resetDecoder();
    showPlaceholder(true);
  },
  fullscreen(m) {
    fullscreen = m.on;
    panelOpenInFullscreen = false;
    document.body.classList.toggle('fullscreen', fullscreen);
    syncFullscreenButton();
    syncPanel();
  },
};

function send(message) {
  host.postMessage(message);
}

// ---------------------------------------------------------------- camera controls

function cameraLabel(camera) {
  const megapixels = Math.round(camera.sensorWidth * camera.sensorHeight / 1e6);
  const zoom = camera.zoomMax > camera.zoomMin
    ? t('camera.zoomRange', { min: formatNumber(camera.zoomMin), max: formatNumber(camera.zoomMax) })
    : '';
  return t('camera.cameraLabel', {
    facing: ({ back: t('camera.facingBack'), front: t('camera.facingFront'), external: t('camera.facingExternal') })[camera.facing] ?? camera.facing,
    megapixels: formatNumber(megapixels),
    zoom,
    id: camera.id,
  });
}

function sizeLabel(width, height) {
  const ratio = width / height;
  const near = target => Math.abs(ratio - target) < 0.02;
  const aspect = near(16 / 9) ? '16:9' : near(4 / 3) ? '4:3' : near(1) ? '1:1' : `${formatNumber(ratio)}:1`;
  const names = { 2160: t('camera.format4k'), 1440: t('camera.format2k'), 1080: t('camera.formatFullHd'), 720: t('camera.formatHd') };
  const name = aspect === '16:9' && names[height] ? ` · ${names[height]}` : '';
  return t('camera.sizeLabel', { width: formatNumber(width), height: formatNumber(height), aspect, name });
}

function fillCameraControls() {
  const cameraSelect = $('camera');
  cameraSelect.replaceChildren(...cameras.map(c => new Option(cameraLabel(c), c.id)));
  if (cameras.length && !cameras.some(c => c.id === settings.cameraId))
    settings.cameraId = cameras[0].id;
  cameraSelect.value = settings.cameraId ?? '';

  const camera = cameras.find(c => c.id === settings.cameraId);
  const sizes = (camera?.sizes ?? [])
    .map(value => {
      const [w, h] = value.split('x').map(Number);
      return { value, w, h };
    })
    // Phones declare dozens of sizes; the usual 16:9 and 4:3 ones are enough here.
    .filter(s => s.w >= 640 && (Math.abs(s.w / s.h - 16 / 9) < 0.02 || Math.abs(s.w / s.h - 4 / 3) < 0.02))
    .sort((a, b) => b.w * b.h - a.w * a.h || b.w - a.w);
  const sizeSelect = $('size');
  sizeSelect.replaceChildren(...sizes.map(s => new Option(sizeLabel(s.w, s.h), s.value)));
  if (sizes.length && !sizes.some(s => s.value === settings.size))
    settings.size = (sizes.find(s => s.w === 1920 && s.h === 1080) ?? sizes.find(s => s.w <= 1920) ?? sizes[0]).value;
  sizeSelect.value = settings.size ?? '';

  const fpsValues = [...new Set([...(camera?.fps ?? []), 30])].filter(f => f >= 15).sort((a, b) => a - b);
  const fpsSelect = $('fps');
  fpsSelect.replaceChildren(...fpsValues.map(f => new Option(String(f), String(f))));
  if (!fpsValues.includes(settings.fps))
    settings.fps = 30;
  fpsSelect.value = String(settings.fps);

  $('quality').value = String(settings.bitRate);
  for (const id of ['camera', 'size', 'fps'])
    $(id).disabled = cameras.length === 0;
  syncZoomControls();
}

let applyTimer = 0;
function applySoon() {
  clearTimeout(applyTimer);
  applyTimer = setTimeout(() => send({
    type: 'apply',
    settings: { cameraId: settings.cameraId, size: settings.size, fps: settings.fps, bitRate: settings.bitRate },
  }), 300);
}

$('camera').addEventListener('change', () => {
  settings.cameraId = $('camera').value;
  resetZoom();
  fillCameraControls();
  applySoon();
});
$('size').addEventListener('change', () => { settings.size = $('size').value; applySoon(); });
$('fps').addEventListener('change', () => { settings.fps = Number($('fps').value); applySoon(); });
$('quality').addEventListener('change', () => { settings.bitRate = Number($('quality').value); applySoon(); });

$('restoreCameras').addEventListener('click', () => send({ type: 'restoreCameras' }));
$('zoomIn').addEventListener('click', () => changeZoom(1));
$('zoomOut').addEventListener('click', () => changeZoom(-1));
$('zoomReset').addEventListener('click', resetZoom);
$('torch').addEventListener('change', () => send({ type: 'torch', on: $('torch').checked }));
$('stage').addEventListener('wheel', event => {
  if (event.deltaY === 0)
    return;
  event.preventDefault();
  changeZoom(event.deltaY < 0 ? 1 : -1);
}, { passive: false });

function syncZoomControls() {
  const camera = cameras.find(c => c.id === settings.cameraId);
  maxZoom = Math.max(1, camera?.zoomMax ?? 1);
  zoom = Math.min(zoom, maxZoom);
  clampPan();
  $('zoomValue').textContent = `${formatNumber(zoom)}×`;
  $('zoomIn').disabled = zoom >= maxZoom;
  $('zoomOut').disabled = zoom <= 1;
  $('zoomReset').disabled = zoom <= 1 && pan.x === 0 && pan.y === 0;
  document.body.classList.toggle('zoomed', zoom > 1);
  syncPreviewTransform();
}

function changeZoom(direction) {
  const nextZoom = Math.round((zoom * (direction > 0 ? 1.2 : 1 / 1.2)) * 10) / 10;
  zoom = Math.max(1, Math.min(maxZoom, nextZoom));
  clampPan();
  syncZoomControls();
}

function resetZoom() {
  zoom = 1;
  pan = { x: 0, y: 0 };
  syncZoomControls();
}

function clampPan() {
  const width = $('stage').clientWidth;
  const height = $('stage').clientHeight;
  if (!canvas.width || !canvas.height)
    return;
  const scale = settings.fit === 'cover'
    ? Math.max(width / canvas.width, height / canvas.height)
    : Math.min(width / canvas.width, height / canvas.height);
  const contentWidth = canvas.width * scale;
  const contentHeight = canvas.height * scale;
  const maxX = Math.max(0, (contentWidth * zoom - width) / 2);
  const maxY = Math.max(0, (contentHeight * zoom - height) / 2);
  pan.x = Math.max(-maxX, Math.min(maxX, pan.x));
  pan.y = Math.max(-maxY, Math.min(maxY, pan.y));
}

function syncPreviewTransform() {
  canvas.style.transform = `translate(${pan.x}px, ${pan.y}px) scale(${zoom})`;
  $('zoomReset').disabled = zoom <= 1 && pan.x === 0 && pan.y === 0;
}

canvas.addEventListener('pointerdown', event => {
  if (zoom <= 1 || event.button !== 0)
    return;
  panStart = { x: event.clientX, y: event.clientY, panX: pan.x, panY: pan.y, pointerId: event.pointerId };
  canvas.setPointerCapture(event.pointerId);
  event.preventDefault();
});
canvas.addEventListener('pointermove', event => {
  if (!panStart || panStart.pointerId !== event.pointerId)
    return;
  pan.x = panStart.panX + event.clientX - panStart.x;
  pan.y = panStart.panY + event.clientY - panStart.y;
  clampPan();
  syncPreviewTransform();
  if (pan.x !== panStart.panX || pan.y !== panStart.panY) {
    panStart.moved = true;
    document.body.classList.add('panning');
  }
});

function finishPan(event) {
  if (!panStart || panStart.pointerId !== event.pointerId)
    return;
  const moved = panStart.moved;
  panStart = null;
  document.body.classList.remove('panning');
  if (canvas.hasPointerCapture(event.pointerId))
    canvas.releasePointerCapture(event.pointerId);
  if (moved)
    lastPanEnd = Date.now();
}

canvas.addEventListener('pointerup', finishPan);
canvas.addEventListener('pointercancel', finishPan);

// ---------------------------------------------------------------- view controls

function rotate() {
  settings.rotation = (settings.rotation + 90) % 360;
  viewChanged();
}

function toggleMirror() {
  settings.mirror = !settings.mirror;
  viewChanged();
}

function toggleFit() {
  settings.fit = settings.fit === 'contain' ? 'cover' : 'contain';
  viewChanged();
}

function viewChanged() {
  clampPan();
  syncPreviewTransform();
  syncViewControls();
  sendView();
}

function sendView() {
  send({
    type: 'view',
    settings: { rotation: settings.rotation, mirror: settings.mirror, fit: settings.fit, panelCollapsed: settings.panelCollapsed, panelWidth: settings.panelWidth },
  });
}

function syncViewControls() {
  canvas.style.objectFit = settings.fit;
  $('fit').textContent = t(settings.fit === 'contain' ? 'controls.fill' : 'controls.fit');
  $('mirror').setAttribute('aria-pressed', String(settings.mirror));
  $('rotate').textContent = settings.rotation
    ? t('controls.rotateBy', { degrees: formatNumber(settings.rotation) })
    : t('controls.rotate');
}

function syncFullscreenButton() {
  $('fullscreen').textContent = t(fullscreen ? 'controls.exitFullscreen' : 'controls.fullscreen');
}

function togglePanel() {
  if (fullscreen) {
    panelOpenInFullscreen = !panelOpenInFullscreen;
  } else {
    settings.panelCollapsed = !settings.panelCollapsed;
    sendView();
  }
  syncPanel();
}

function syncPanel() {
  const visible = fullscreen ? panelOpenInFullscreen : !settings.panelCollapsed;
  document.body.classList.toggle('panel-hidden', !visible);
  $('showPanel').hidden = visible;
  $('hidePanel').setAttribute('aria-expanded', String(visible));
}

function maxPanelWidth() {
  return Math.max(MIN_PANEL_WIDTH, Math.min(MAX_PANEL_WIDTH, document.documentElement.clientWidth - MIN_STAGE_WIDTH));
}

function syncPanelWidth() {
  settings.panelWidth = Math.round(Math.max(MIN_PANEL_WIDTH, Math.min(maxPanelWidth(), settings.panelWidth)));
  document.documentElement.style.setProperty('--panel-width', `${settings.panelWidth}px`);
  const handle = $('resizePanel');
  handle.setAttribute('aria-valuemin', String(MIN_PANEL_WIDTH));
  handle.setAttribute('aria-valuemax', String(maxPanelWidth()));
  handle.setAttribute('aria-valuenow', String(settings.panelWidth));
  handle.setAttribute('aria-valuetext', `${settings.panelWidth} px`);
}

let panelResizeStart = null;
const resizeHandle = $('resizePanel');
resizeHandle.addEventListener('pointerdown', event => {
  if (event.button !== 0)
    return;
  panelResizeStart = { x: event.clientX, width: settings.panelWidth, pointerId: event.pointerId };
  resizeHandle.setPointerCapture(event.pointerId);
  event.preventDefault();
});
resizeHandle.addEventListener('pointermove', event => {
  if (!panelResizeStart || panelResizeStart.pointerId !== event.pointerId)
    return;
  const direction = document.documentElement.dir === 'rtl' ? -1 : 1;
  settings.panelWidth = panelResizeStart.width + (event.clientX - panelResizeStart.x) * direction;
  syncPanelWidth();
});

function finishPanelResize(event) {
  if (!panelResizeStart || panelResizeStart.pointerId !== event.pointerId)
    return;
  const changed = settings.panelWidth !== panelResizeStart.width;
  panelResizeStart = null;
  if (resizeHandle.hasPointerCapture(event.pointerId))
    resizeHandle.releasePointerCapture(event.pointerId);
  if (changed)
    sendView();
}

resizeHandle.addEventListener('pointerup', finishPanelResize);
resizeHandle.addEventListener('pointercancel', finishPanelResize);
resizeHandle.addEventListener('keydown', event => {
  const step = event.shiftKey ? 40 : 10;
  let width = settings.panelWidth;
  if (event.key === 'ArrowLeft')
    width -= step;
  else if (event.key === 'ArrowRight')
    width += step;
  else if (event.key === 'Home')
    width = MIN_PANEL_WIDTH;
  else if (event.key === 'End')
    width = maxPanelWidth();
  else
    return;
  event.preventDefault();
  settings.panelWidth = width;
  syncPanelWidth();
  sendView();
});

window.addEventListener('resize', () => {
  const previousWidth = settings.panelWidth;
  syncPanelWidth();
  clampPan();
  syncPreviewTransform();
  if (settings.panelWidth !== previousWidth)
    sendView();
});

function showNotice(text) {
  const notice = $('notice');
  notice.textContent = text;
  notice.hidden = false;
  clearTimeout(noticeTimer);
  noticeTimer = setTimeout(() => {
    notice.hidden = true;
    noticeMessage = null;
  }, 8000);
}

function toggleFullscreen() {
  send({ type: 'fullscreen', on: !fullscreen });
}

$('rotate').addEventListener('click', rotate);
$('mirror').addEventListener('click', toggleMirror);
$('fit').addEventListener('click', toggleFit);
$('fullscreen').addEventListener('click', toggleFullscreen);
$('stage').addEventListener('dblclick', () => {
  if (Date.now() - lastPanEnd > 350)
    toggleFullscreen();
});
$('hidePanel').addEventListener('click', togglePanel);
$('showPanel').addEventListener('click', togglePanel);
$('showPanel').addEventListener('dblclick', e => e.stopPropagation());

document.addEventListener('keydown', e => {
  if (e.key === 'F11') {
    toggleFullscreen();
    e.preventDefault();
    return;
  }
  if (e.key === 'Escape' && fullscreen) {
    send({ type: 'fullscreen', on: false });
    e.preventDefault();
    return;
  }
  // Letters and arrows belong to a focused drop-down list.
  if (e.target instanceof HTMLSelectElement)
    return;
  switch (e.key) {
    case '+': case '=': changeZoom(1); break;
    case '-': changeZoom(-1); break;
    case 'r': case 'R': rotate(); break;
    case 'm': case 'M': toggleMirror(); break;
    case 'a': case 'A': toggleFit(); break;
    case 'p': case 'P': togglePanel(); break;
    default: return;
  }
  e.preventDefault();
});

let idleTimer = 0;
document.addEventListener('mousemove', () => {
  document.body.classList.remove('idle');
  clearTimeout(idleTimer);
  idleTimer = setTimeout(() => document.body.classList.add('idle'), 2000);
});

// ---------------------------------------------------------------- decoding

let decoder = null;
let codec = '';
let lastConfig = null; // SPS/PPS, reused if the decoder must be rebuilt without a new config packet
let waitingForKeyFrame = true;
let latestFrame = null;
let drawScheduled = false;
let frameSize = '';
const counters = { decoded: 0, window: 0, dropped: 0, errors: 0, lastError: '' };

// Packet format: "V<flags>,<pts>,<base64>" with flags bit 0 = keyframe, bit 1 = starts with SPS/PPS.
function onVideoPacket(message) {
  const c1 = message.indexOf(',');
  const c2 = message.indexOf(',', c1 + 1);
  const flags = Number(message.slice(1, c1));
  const pts = Number(message.slice(c1 + 1, c2));
  let data = base64ToBytes(message.slice(c2 + 1));
  const keyFrame = (flags & 1) !== 0;

  if (flags & 2) {
    const packetCodec = codecFromAnnexB(data);
    const sliceStart = firstSliceOffset(data);
    if (sliceStart > 0)
      lastConfig = data.slice(0, sliceStart);
    if (packetCodec && (packetCodec !== codec || decoder?.state !== 'configured'))
      configureDecoder(packetCodec);
  } else if (decoder?.state !== 'configured' && keyFrame && lastConfig && codec) {
    configureDecoder(codec);
    const merged = new Uint8Array(lastConfig.length + data.length);
    merged.set(lastConfig);
    merged.set(data, lastConfig.length);
    data = merged;
  }

  if (decoder?.state !== 'configured')
    return;
  if (waitingForKeyFrame) {
    if (!keyFrame) {
      counters.dropped++;
      return;
    }
    waitingForKeyFrame = false;
  }
  if (decoder.decodeQueueSize > 10) {
    // Too far behind: drop to the next keyframe rather than showing old images.
    counters.dropped++;
    waitingForKeyFrame = true;
    send({ type: 'keyframe' });
    return;
  }
  decoder.decode(new EncodedVideoChunk({ type: keyFrame ? 'key' : 'delta', timestamp: pts, data }));
}

function configureDecoder(newCodec) {
  if (decoder && decoder.state !== 'closed')
    decoder.close();
  decoder = new VideoDecoder({ output: onFrame, error: onDecoderError });
  decoder.configure({ codec: newCodec, optimizeForLatency: true, hardwareAcceleration: 'prefer-hardware' });
  codec = newCodec;
  waitingForKeyFrame = true;
}

function onDecoderError(error) {
  counters.errors++;
  counters.lastError = error.message;
  send({ type: 'log', text: `decoder: ${error.message}` });
  decoder = null;
  waitingForKeyFrame = true;
  send({ type: 'keyframe' });
}

function resetDecoder() {
  if (decoder && decoder.state !== 'closed')
    decoder.close();
  decoder = null;
  codec = '';
  lastConfig = null;
  waitingForKeyFrame = true;
  if (latestFrame) {
    latestFrame.close();
    latestFrame = null;
  }
}

function onFrame(frame) {
  counters.decoded++;
  counters.window++;
  if (latestFrame)
    latestFrame.close();
  latestFrame = frame;
  if (!drawScheduled) {
    drawScheduled = true;
    requestAnimationFrame(draw);
  }
}

function draw() {
  drawScheduled = false;
  const frame = latestFrame;
  latestFrame = null;
  if (!frame)
    return;
  const w = frame.displayWidth;
  const h = frame.displayHeight;
  const sideways = settings.rotation % 180 !== 0;
  const cw = sideways ? h : w;
  const ch = sideways ? w : h;
  if (canvas.width !== cw || canvas.height !== ch) {
    canvas.width = cw;
    canvas.height = ch;
    clampPan();
    syncPreviewTransform();
  }
  // Mirror in screen space, after the rotation.
  ctx.setTransform(1, 0, 0, 1, cw / 2, ch / 2);
  if (settings.mirror)
    ctx.scale(-1, 1);
  ctx.rotate(settings.rotation * Math.PI / 180);
  ctx.drawImage(frame, -w / 2, -h / 2, w, h);
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  frameSize = `${formatNumber(w)}×${formatNumber(h)}`;
  frame.close();
  showPlaceholder(false);
}

function showPlaceholder(show) {
  const placeholder = $('placeholder');
  if (placeholder.hidden === !show)
    return;
  placeholder.hidden = !show;
  if (show)
    ctx.clearRect(0, 0, canvas.width, canvas.height);
}

function base64ToBytes(text) {
  if (Uint8Array.fromBase64)
    return Uint8Array.fromBase64(text);
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++)
    bytes[i] = binary.charCodeAt(i);
  return bytes;
}

function codecFromAnnexB(data) {
  for (let i = 0; i + 6 < data.length; i++) {
    if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 1) {
      if ((data[i + 3] & 0x1f) === 7)
        return `avc1.${hex(data[i + 4])}${hex(data[i + 5])}${hex(data[i + 6])}`;
      i += 2;
    }
  }
  return null;
}

function firstSliceOffset(data) {
  for (let i = 0; i + 3 < data.length; i++) {
    if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 1) {
      const type = data[i + 3] & 0x1f;
      if (type === 1 || type === 5)
        return i > 0 && data[i - 1] === 0 ? i - 1 : i;
      i += 2;
    }
  }
  return -1;
}

const hex = value => value.toString(16).padStart(2, '0');
const formatNumber = value => Number(value).toLocaleString(language, { maximumFractionDigits: 1 });

// ---------------------------------------------------------------- stats

function updateStats() {
  const fps = counters.window;
  const size = frameSize || sessionSize;
  $('stats').textContent = streaming && size
    ? `${size} · ${t('stats.fps', { count: formatNumber(fps) })}${counters.dropped ? t('stats.dropped', { count: formatNumber(counters.dropped) }) : ''}`
    : '';
}

setInterval(() => {
  const fps = counters.window;
  counters.window = 0;
  updateStats();
  send({ type: 'stats', decoded: counters.decoded, fps, dropped: counters.dropped, errors: counters.errors, lastError: counters.lastError, codec, frameSize });
}, 1000);

if (!('VideoDecoder' in window)) {
  handlers.status({ state: 'error', key: 'messages.noWebCodecs' });
  send({ type: 'log', text: 'WebCodecs is unavailable' });
}

async function initialize() {
  const requested = new URLSearchParams(location.search).get('lang');
  const initialLanguage = supportedLanguages.includes(requested) ? requested : 'en';
  try {
    await applyLanguage(initialLanguage);
  } catch (error) {
    console.error('Could not initialize the selected language.', error);
    if (initialLanguage !== 'en') {
      try {
        await applyLanguage('en');
      } catch (fallbackError) {
        console.error('Could not load the English fallback language.', fallbackError);
      }
    }
  }

  $('language').addEventListener('change', async () => {
    const selected = $('language').value;
    try {
      await applyLanguage(selected, true);
    } catch (error) {
      console.error(`Could not apply language ${selected}.`, error);
      $('language').value = language;
      if (translations)
        showNotice(t('messages.localeLoadFailed', { detail: error.message }));
    }
  });
  send({ type: 'ready' });
}

void initialize();
