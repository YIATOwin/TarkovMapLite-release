'use strict';

// ============================================================================
// app.js — 手机端：只负责画和触摸。所有坐标都是电脑端算好的图片像素。
//
// 分工：电脑端（LocalServer + MobileState）把"世界坐标 → 图片像素"算完，
// 这里不做任何投影；所以手机上的位置和电脑上永远一致，也不可能对不上。
//
// 文件结构（按顺序看就是数据流）：
//   轮询 poll()            GET /api/state，350ms 一次（断线时退到 1.5s）
//   状态 applyState()      收状态 → 换图就 fit/跟随 → 重画
//   视图 fit/centerOn      纯 2D 仿射：屏幕 = 图片 * S.scale + (S.tx, S.ty)
//   图片 image()           /img/{目录}/{名字}，按需加载，加载完自己重画
//   绘制 draw()            底图 → 楼层 → 区域 → 撤离点 → 玩家（和电脑端同序）
//   顶栏 renderChips()     楼层 chip（点了 POST setFloor）+ 图层开关（本地）
//   抽屉 renderTasks()     任务列表（点了 POST selectTask + 地图框过去）
//   动作 sendAction()      POST /api/action
//   触摸 pointer 事件      单指拖动 / 双指缩放 / 双击复位
//
// 改接口时必须和 LocalServer.cs 头部的路由表、MobileState.cs 头部的字段表对齐。
// 调试：直接改这个文件不生效，它是嵌进 exe 的；要么重新打包，
//       要么把它放到 exe 同目录的 Assets\mobile\ 下（Assets.Open 优先读散文件）。
// ============================================================================

const canvas = document.getElementById('map');
const ctx = canvas.getContext('2d');

const ui = {
  title: document.getElementById('title'),
  net: document.getElementById('net'),
  floors: document.getElementById('floors'),
  layers: document.getElementById('layers'),
  follow: document.getElementById('follow'),
  fit: document.getElementById('fit'),
  drawer: document.getElementById('drawer'),
  handle: document.getElementById('handle'),
  drawerCount: document.getElementById('drawerCount'),
  taskList: document.getElementById('taskList'),
  taskDetail: document.getElementById('taskDetail'),
  toast: document.getElementById('toast'),
};

const S = {
  data: null,
  taskIndex: Object.create(null),
  mapKey: '',
  scale: 1,
  tx: 0,
  ty: 0,
  fitScale: 1,
  follow: true,
  showZones: true,
  showMarks: true,
  onlyActive: true,
  sel: null,
  images: Object.create(null),
  connected: false,
  tasksKey: '',
  detailRev: -1,
  w: 0,
  h: 0,
  dpr: 1,
};

// ---------------------------------------------------------------- 轮询

let pollTimer = 0;

async function poll() {
  pollTimer = 0;
  try {
    const controller = new AbortController();
    const guard = setTimeout(() => controller.abort(), 4000);
    const response = await fetch('/api/state', { cache: 'no-store', signal: controller.signal });
    clearTimeout(guard);
    if (!response.ok) {
      throw new Error('HTTP ' + response.status);
    }
    const data = await response.json();
    setConnected(true);
    applyState(data);
  } catch (error) {
    setConnected(false);
  }
  // 连着的时候 3Hz（走路的位置点够顺），断了就慢一点重试，别把手机电池烧了
  pollTimer = setTimeout(poll, S.connected ? 330 : 1500);
}

function setConnected(ok) {
  if (S.connected === ok) {
    return;
  }
  S.connected = ok;
  ui.net.className = 'badge ' + (ok ? 'ok' : 'bad');
  ui.net.textContent = ok ? '已连接' : '断线';
  if (!ok && S.data) {
    ui.title.textContent = (S.data.map ? S.data.map.name : '地图') + ' · 连接中断';
  }
}

// ---------------------------------------------------------------- 状态

function applyState(data) {
  const previousMap = S.data && S.data.map ? S.data.map.key : '';
  const mapChanged = !S.data || !data.map || data.map.key !== previousMap;

  S.data = data;
  S.taskIndex = Object.create(null);
  for (const task of data.tasks) {
    S.taskIndex[task.id] = task.s;
  }

  if (data.map) {
    ui.title.textContent = data.map.name + (floorLabel(data.map) ? ' · ' + floorLabel(data.map) : '');
  } else {
    ui.title.textContent = '等待电脑端…';
  }

  if (mapChanged && data.map) {
    S.mapKey = data.map.key;
    S.sel = null;
    S.tasksKey = '';
    fit();
    // 换图通常意味着开了新局：直接回到"跟着我"的状态，放大看清楚
    if (data.player) {
      setFollow(true);
      S.scale = clampScale(Math.max(S.scale, S.fitScale * 2.0));
      centerOn(data.player.x, data.player.y);
    }
  }

  if (S.follow && data.player) {
    centerOn(data.player.x, data.player.y);
  }

  renderChips();
  renderTasks();
  renderDetail();
  draw();
}

function floorLabel(map) {
  const floor = map.floors.find(f => f.l === map.layer);
  return floor && map.layer ? floor.n : '';
}

// ---------------------------------------------------------------- 视图

function fit() {
  const map = S.data && S.data.map;
  if (!map || !S.w) {
    return;
  }
  S.fitScale = Math.min(S.w / map.w, S.h / map.h);
  S.scale = S.fitScale;
  S.tx = (S.w - map.w * S.scale) / 2;
  S.ty = (S.h - map.h * S.scale) / 2;
}

function centerOn(x, y) {
  S.tx = S.w / 2 - x * S.scale;
  S.ty = S.h / 2 - y * S.scale;
}

function clampScale(scale) {
  const min = Math.max(S.fitScale * 0.6, 0.02);
  const max = Math.max(S.fitScale * 9, 1.2);
  return Math.min(Math.max(scale, min), max);
}

/// 把某个任务的所有区域框进可视区（顶栏和底部抽屉要留出来），点任务后地图直接带过去
function focusTask(taskId) {
  const data = S.data;
  if (!data) {
    return;
  }
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const zone of data.zones) {
    if (zone.i !== taskId) {
      continue;
    }
    for (let i = 0; i < zone.p.length; i += 2) {
      const x = zone.p[i];
      const y = zone.p[i + 1];
      if (x < minX) minX = x;
      if (y < minY) minY = y;
      if (x > maxX) maxX = x;
      if (y > maxY) maxY = y;
    }
  }
  if (!isFinite(minX) || !isFinite(maxX)) {
    return;
  }
  // 顶栏两层 chip 大概 150px，底部抽屉收起时 50px
  const left = 24;
  const right = S.w - 24;
  const top = 150;
  const bottom = Math.max(S.h - 70, top + 120);
  const scale = clampScale(Math.min(
    (right - left) / Math.max(maxX - minX, 1),
    (bottom - top) / Math.max(maxY - minY, 1)));
  S.scale = scale;
  S.tx = (left + right) / 2 - (minX + maxX) / 2 * scale;
  S.ty = (top + bottom) / 2 - (minY + maxY) / 2 * scale;
  setFollow(false);
}

function toX(x) {
  return x * S.scale + S.tx;
}

function toY(y) {
  return y * S.scale + S.ty;
}

// ---------------------------------------------------------------- 图片

function image(folder, stem) {
  const key = folder + '/' + stem;
  let entry = S.images[key];
  if (!entry) {
    entry = { img: new Image(), ok: false };
    entry.img.onload = () => {
      entry.ok = true;
      draw();
    };
    // 这一层没有单独的图（不少图只有地面）：底图已经画上了，什么都不用做
    entry.img.onerror = () => { entry.failed = true; };
    entry.img.src = '/img/' + encodeURIComponent(folder) + '/' + encodeURIComponent(stem);
    S.images[key] = entry;
  }
  return entry.ok ? entry.img : null;
}

// ---------------------------------------------------------------- 绘制

function draw() {
  const dpr = Math.min(window.devicePixelRatio || 1, 2.5);
  S.dpr = dpr;
  const w = canvas.clientWidth || window.innerWidth;
  const h = canvas.clientHeight || window.innerHeight;
  if (S.w !== w || S.h !== h) {
    const zoomed = S.fitScale > 0 && Math.abs(S.scale - S.fitScale) > 1e-6;
    S.w = w;
    S.h = h;
    if (!zoomed) {
      fit();
    }
  }
  const pixelW = Math.round(S.w * dpr);
  const pixelH = Math.round(S.h * dpr);
  if (canvas.width !== pixelW || canvas.height !== pixelH) {
    canvas.width = pixelW;
    canvas.height = pixelH;
  }
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.fillStyle = '#0d0f11';
  ctx.fillRect(0, 0, S.w, S.h);

  const data = S.data;
  if (!data || !data.map) {
    ctx.fillStyle = '#6d7783';
    ctx.font = '14px system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.fillText(S.connected ? '等待电脑端…' : '连接不上电脑端', S.w / 2, S.h / 2);
    return;
  }

  const map = data.map;
  const base = image(map.img, 'base');
  if (base) {
    ctx.drawImage(base, S.tx, S.ty, map.w * S.scale, map.h * S.scale);
  }
  if (map.layer) {
    const floor = image(map.img, map.layer);
    if (floor) {
      ctx.drawImage(floor, S.tx, S.ty, map.w * S.scale, map.h * S.scale);
    }
  }

  if (S.showZones) {
    drawZones(data);
  }
  if (S.showMarks) {
    drawMarks(data);
  }
  drawPlayer(data.player);
}

function drawZones(data) {
  const selected = S.sel || data.sel || null;
  const scale = S.scale;
  for (const zone of data.zones) {
    if (selected && zone.i !== selected) {
      continue;
    }
    const state = S.taskIndex[zone.i] || 0;
    if (S.onlyActive && state !== 1) {
      continue;
    }
    const points = zone.p;
    if (points.length < 6) {
      continue;
    }
    ctx.beginPath();
    let visible = false;
    for (let i = 0; i < points.length; i += 2) {
      const x = toX(points[i]);
      const y = toY(points[i + 1]);
      if (i === 0) {
        ctx.moveTo(x, y);
      } else {
        ctx.lineTo(x, y);
      }
      if (x > -40 && y > -40 && x < S.w + 40 && y < S.h + 40) {
        visible = true;
      }
    }
    if (!visible) {
      continue;
    }
    ctx.closePath();
    const dim = state === 2 || state === 3;
    ctx.fillStyle = stateColor(state, dim ? 0.1 : 0.22);
    ctx.fill();
    // 线宽按屏幕像素算，缩小时不会糊成一条边
    ctx.lineWidth = Math.max(1.2 / scale, 0.6);
    ctx.strokeStyle = dim ? 'rgba(255,255,255,.55)' : 'rgba(255,255,255,.92)';
    ctx.stroke();
  }
}

function stateColor(state, alpha) {
  const rgb = state === 1 ? [76, 154, 255]
    : state === 3 ? [120, 200, 140]
      : state === 2 ? [220, 110, 110]
        : [150, 150, 160];
  return 'rgba(' + rgb[0] + ',' + rgb[1] + ',' + rgb[2] + ',' + alpha + ')';
}

function drawMarks(data) {
  const marks = data.marks;
  if (!marks.length) {
    return;
  }
  ctx.font = '11px system-ui, "PingFang SC", sans-serif';
  ctx.textBaseline = 'middle';
  ctx.textAlign = 'left';
  const placed = [];
  for (const mark of marks) {
    const x = toX(mark.x);
    const y = toY(mark.y);
    if (x < -30 || y < -30 || x > S.w + 30 || y > S.h + 30) {
      continue;
    }
    const extract = mark.k === 'e';
    ctx.beginPath();
    if (extract) {
      ctx.rect(x - 4.5, y - 4.5, 9, 9);
    } else {
      ctx.moveTo(x, y - 6);
      ctx.lineTo(x + 6, y);
      ctx.lineTo(x, y + 6);
      ctx.lineTo(x - 6, y);
      ctx.closePath();
    }
    ctx.fillStyle = extract ? '#78dc96' : '#be96f5';
    ctx.fill();
    ctx.lineWidth = 1.2;
    ctx.strokeStyle = 'rgba(20,24,20,.85)';
    ctx.stroke();

    // 名字：挤不下就跳过，而不是叠成一团
    const text = mark.n;
    if (!text) {
      continue;
    }
    const width = ctx.measureText(text).width;
    let tx = x + 8;
    let ty = y;
    if (tx + width > S.w - 4) {
      tx = x - width - 8;
    }
    const box = { x: tx - 2, y: ty - 7, w: width + 4, h: 14 };
    if (placed.some(other => box.x < other.x + other.w && box.x + box.w > other.x
      && box.y < other.y + other.h && box.y + box.h > other.y)) {
      continue;
    }
    placed.push(box);
    ctx.lineWidth = 3;
    ctx.strokeStyle = 'rgba(0,0,0,.75)';
    ctx.strokeText(text, tx, ty);
    ctx.fillStyle = 'rgba(238,242,248,.95)';
    ctx.fillText(text, tx, ty);
  }
}

function drawPlayer(player) {
  if (!player) {
    return;
  }
  const x = toX(player.x);
  const y = toY(player.y);
  const onScreen = x > 0 && y > 0 && x < S.w && y < S.h;
  if (!onScreen) {
    // 不在视野里：在边缘画个箭头指过去
    const margin = 24;
    const ex = Math.min(Math.max(x, margin), S.w - margin);
    const ey = Math.min(Math.max(y, margin), S.h - margin);
    const angle = Math.atan2(y - ey, x - ex);
    ctx.save();
    ctx.translate(ex, ey);
    ctx.rotate(angle);
    ctx.beginPath();
    ctx.moveTo(11, 0);
    ctx.lineTo(-6, -7);
    ctx.lineTo(-6, 7);
    ctx.closePath();
    ctx.fillStyle = '#4de0e0';
    ctx.fill();
    ctx.strokeStyle = 'rgba(0,0,0,.6)';
    ctx.lineWidth = 1.2;
    ctx.stroke();
    ctx.restore();
    return;
  }

  ctx.beginPath();
  ctx.arc(x, y, 10, 0, Math.PI * 2);
  ctx.fillStyle = 'rgba(77,224,224,.16)';
  ctx.fill();

  ctx.beginPath();
  ctx.arc(x, y, 4.6, 0, Math.PI * 2);
  ctx.fillStyle = '#4de0e0';
  ctx.fill();
  ctx.lineWidth = 1.4;
  ctx.strokeStyle = 'rgba(255,255,255,.9)';
  ctx.stroke();

  // 朝向：电脑端给的就是图片像素方向，直接 atan2，不用管地图旋转了几度
  ctx.save();
  ctx.translate(x, y);
  ctx.rotate(Math.atan2(player.dy, player.dx));
  ctx.beginPath();
  ctx.moveTo(14, 0);
  ctx.lineTo(4, -6.5);
  ctx.lineTo(4, 6.5);
  ctx.closePath();
  ctx.fillStyle = '#4de0e0';
  ctx.fill();
  ctx.strokeStyle = 'rgba(255,255,255,.85)';
  ctx.lineWidth = 1.1;
  ctx.stroke();
  ctx.restore();
}

// ---------------------------------------------------------------- 顶栏 chip

function renderChips() {
  const data = S.data;
  const map = data && data.map;
  ui.floors.textContent = '';
  if (map && map.floors.length > 1) {
    for (const floor of map.floors) {
      const chip = document.createElement('button');
      chip.type = 'button';
      chip.className = 'chip' + (floor.l === map.layer ? ' on' : '');
      chip.textContent = floor.n;
      chip.addEventListener('click', () => {
        sendAction('setFloor', floor.l);
        toast('切换楼层：' + floor.n);
      });
      ui.floors.appendChild(chip);
    }
  }

  ui.layers.textContent = '';
  addLayerChip('区域', S.showZones, on => {
    S.showZones = on;
    draw();
  });
  addLayerChip('撤离点', S.showMarks, on => {
    S.showMarks = on;
    draw();
  });
  addLayerChip('仅进行中', S.onlyActive, on => {
    S.onlyActive = on;
    draw();
  });
}

function addLayerChip(text, active, onChange) {
  const chip = document.createElement('button');
  chip.type = 'button';
  chip.className = 'chip' + (active ? ' on' : '');
  chip.textContent = text;
  chip.addEventListener('click', () => {
    const next = !chip.classList.contains('on');
    chip.classList.toggle('on', next);
    onChange(next);
  });
  ui.layers.appendChild(chip);
}

// ---------------------------------------------------------------- 任务抽屉

function renderTasks() {
  const data = S.data;
  if (!data) {
    return;
  }
  const selected = S.sel || data.sel || '';
  const key = data.tasks.map(task => task.id + ':' + task.s).join('|') + '#' + selected + '#' + data.map.key;
  if (key === S.tasksKey) {
    return;
  }
  S.tasksKey = key;

  ui.drawerCount.textContent = (() => {
    const picked = data.tasks.find(task => task.id === selected);
    if (picked) {
      return picked.n;
    }
    return data.tasks.length ? data.tasks.length + ' 个' : '';
  })();
  ui.taskList.textContent = '';
  if (!data.tasks.length) {
    const empty = document.createElement('div');
    empty.className = 'task';
    empty.innerHTML = '<div class="name">这张图上没有任务</div>';
    ui.taskList.appendChild(empty);
    return;
  }
  for (const task of data.tasks) {
    const row = document.createElement('div');
    row.className = 'task'
      + (task.id === selected ? ' sel' : '')
      + (task.s === 3 ? ' s-done' : '')
      + (task.s === 2 ? ' s-fail' : '');
    const name = document.createElement('div');
    name.className = 'name';
    name.textContent = task.n;
    const sub = document.createElement('div');
    sub.className = 'sub';
    sub.textContent = task.sub;
    row.appendChild(name);
    row.appendChild(sub);
    row.addEventListener('click', () => {
      const next = task.id === selected ? '' : task.id;
      S.sel = next || null;
      S.tasksKey = '';
      // 电脑端跟着一起选，两边看到的是同一件事
      sendAction('selectTask', next);
      if (next) {
        focusTask(next);
        // 选完就把抽屉收起来：手机端最想看到的是"往哪走"，目标文字点一下还有
        ui.drawer.classList.remove('open');
        document.body.classList.remove('drawer-open');
      } else {
        fit();
      }
      renderTasks();
      draw();
    });
    ui.taskList.appendChild(row);
  }
}

function renderDetail() {
  const data = S.data;
  if (!data) {
    return;
  }
  const selected = S.sel || data.sel || '';
  const revKey = data.rev + '#' + selected;
  if (revKey === S.detailRev) {
    return;
  }
  S.detailRev = revKey;
  ui.taskDetail.textContent = '';
  if (!selected) {
    const empty = document.createElement('div');
    empty.className = 'empty';
    empty.textContent = '点上面的任务，这里显示目标和要带的物品。';
    ui.taskDetail.appendChild(empty);
    return;
  }
  const detail = data.detail || { obj: [], items: [] };
  if (!detail.obj.length) {
    const empty = document.createElement('div');
    empty.className = 'empty';
    empty.textContent = '正在从电脑端取目标…';
    ui.taskDetail.appendChild(empty);
    return;
  }
  for (const line of detail.obj) {
    const row = document.createElement('div');
    row.className = 'obj';
    row.textContent = '• ' + line;
    ui.taskDetail.appendChild(row);
  }
  if (detail.items.length) {
    const box = document.createElement('div');
    box.className = 'items';
    const head = document.createElement('div');
    head.className = 'head empty';
    head.textContent = '要带的物品';
    box.appendChild(head);
    for (const item of detail.items) {
      const chip = document.createElement('span');
      chip.className = 'item';
      chip.textContent = item.n;
      box.appendChild(chip);
    }
    ui.taskDetail.appendChild(box);
  }
}

// ---------------------------------------------------------------- 反向动作

async function sendAction(type, value) {
  try {
    await fetch('/api/action', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ type: type, value: value || '' }),
      cache: 'no-store',
    });
  } catch (error) {
    toast('电脑端没有响应');
  }
}

let toastTimer = 0;

function toast(text) {
  ui.toast.textContent = text;
  ui.toast.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => ui.toast.classList.remove('show'), 1400);
}

// ---------------------------------------------------------------- 触摸

const pointers = new Map();
let gesture = null;
let lastTapTime = 0;
let lastTapX = 0;
let lastTapY = 0;

function distance(a, b) {
  return Math.hypot(a.x - b.x, a.y - b.y);
}

function midpoint(a, b) {
  return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
}

canvas.addEventListener('pointerdown', event => {
  canvas.setPointerCapture(event.pointerId);
  pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });

  if (pointers.size === 1) {
    gesture = {
      mode: 'pan',
      startX: event.clientX,
      startY: event.clientY,
      tx: S.tx,
      ty: S.ty,
      moved: 0,
    };
  } else if (pointers.size === 2) {
    const [a, b] = [...pointers.values()];
    gesture = {
      mode: 'pinch',
      d0: Math.max(distance(a, b), 1),
      scale0: S.scale,
      tx0: S.tx,
      ty0: S.ty,
      // 两指中点下方的那个图片坐标，缩放时要钉在原地
      anchorX: (midpoint(a, b).x - S.tx) / S.scale,
      anchorY: (midpoint(a, b).y - S.ty) / S.scale,
    };
    setFollow(false);
  }
});

canvas.addEventListener('pointermove', event => {
  if (!pointers.has(event.pointerId) || !gesture) {
    return;
  }
  pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });

  if (gesture.mode === 'pan' && pointers.size === 1) {
    const dx = event.clientX - gesture.startX;
    const dy = event.clientY - gesture.startY;
    gesture.moved = Math.max(gesture.moved, Math.hypot(dx, dy));
    S.tx = gesture.tx + dx;
    S.ty = gesture.ty + dy;
    if (gesture.moved > 8) {
      setFollow(false);
    }
    draw();
  } else if (gesture.mode === 'pinch' && pointers.size >= 2) {
    const [a, b] = [...pointers.values()];
    const scale = clampScale(gesture.scale0 * (distance(a, b) / gesture.d0));
    const mid = midpoint(a, b);
    S.scale = scale;
    S.tx = mid.x - gesture.anchorX * scale;
    S.ty = mid.y - gesture.anchorY * scale;
    draw();
  }
});

function endPointer(event) {
  if (!pointers.has(event.pointerId)) {
    return;
  }
  pointers.delete(event.pointerId);
  if (pointers.size === 0) {
    const tap = gesture && gesture.mode === 'pan' && gesture.moved < 8;
    gesture = null;
    if (tap) {
      const now = Date.now();
      if (now - lastTapTime < 320
        && Math.hypot(event.clientX - lastTapX, event.clientY - lastTapY) < 34) {
        fit();
        setFollow(false);
        draw();
        lastTapTime = 0;
      } else {
        lastTapTime = now;
        lastTapX = event.clientX;
        lastTapY = event.clientY;
      }
    }
  } else if (pointers.size === 1) {
    // 松开一根手指：剩下的那根继续当拖动，不要突然跳一下
    const [only] = [...pointers.values()];
    gesture = {
      mode: 'pan',
      startX: only.x,
      startY: only.y,
      tx: S.tx,
      ty: S.ty,
      moved: 10,
    };
  }
}

canvas.addEventListener('pointerup', endPointer);
canvas.addEventListener('pointercancel', endPointer);

function setFollow(on) {
  if (S.follow === on) {
    return;
  }
  S.follow = on;
  ui.follow.classList.toggle('on', on);
}

ui.follow.addEventListener('click', () => {
  setFollow(!S.follow);
  if (S.follow) {
    if (S.data && S.data.player) {
      S.scale = clampScale(Math.max(S.scale, S.fitScale * 2));
      centerOn(S.data.player.x, S.data.player.y);
    } else {
      toast('还没定位到玩家');
    }
    draw();
  }
});

ui.fit.addEventListener('click', () => {
  setFollow(false);
  fit();
  draw();
});

// ---------------------------------------------------------------- 抽屉开合

ui.handle.addEventListener('click', () => {
  ui.drawer.classList.toggle('open');
  document.body.classList.toggle('drawer-open', ui.drawer.classList.contains('open'));
});

// ---------------------------------------------------------------- 启动

window.addEventListener('resize', () => draw());
window.addEventListener('orientationchange', () => setTimeout(draw, 220));
window.addEventListener('keydown', event => {
  if (event.key === 'Escape') {
    ui.drawer.classList.remove('open');
    document.body.classList.remove('drawer-open');
  }
});

fit();
draw();
poll();
