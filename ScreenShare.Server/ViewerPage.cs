namespace ScreenShare.Server;

/// <summary>
/// Страница просмотра (п. 3.1 № 5, а также п. 3.2: масштабирование на клиенте
/// и тёмная/светлая тема).
///
/// Собранная функция работает и на MJPEG (Variant B): картинка приходит
/// через обычный &lt;img src="/stream"&gt;, то есть трансляция продолжается
/// даже если JavaScript отключён или не загрузился.
/// </summary>
internal static class ViewerPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>ScreenShare — трансляция экрана</title>
<style>
  :root {
    --bg: #101317; --panel: #171b21; --panel-2: #1f242c;
    --line: #2b313a; --fg: #e9ecf1; --muted: #939cab;
    --accent: #3d8bfd; --ok: #3fb950; --err: #f85149;
  }
  html.light {
    --bg: #f2f4f7; --panel: #ffffff; --panel-2: #eef1f5;
    --line: #d6dbe3; --fg: #1b1f27; --muted: #5c6573;
    --accent: #1f6feb;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; background: var(--bg); color: var(--fg);
    font: 14px/1.5 "Segoe UI", system-ui, sans-serif;
    display: flex; flex-direction: column; height: 100vh;
  }
  header {
    display: flex; align-items: center; gap: 14px; flex-wrap: wrap;
    padding: 10px 16px; background: var(--panel); border-bottom: 1px solid var(--line);
  }
  h1 { font-size: 15px; margin: 0; letter-spacing: .2px; }
  .pill { padding: 3px 10px; border-radius: 999px; font-size: 12px; font-weight: 600; }
  .pill.on  { background: rgba(63,185,80,.16); color: var(--ok);  border: 1px solid rgba(63,185,80,.45); }
  .pill.off { background: rgba(248,81,73,.14); color: var(--err); border: 1px solid rgba(248,81,73,.4); }
  .grow { flex: 1; }
  button {
    background: var(--panel-2); color: var(--fg); border: 1px solid var(--line);
    border-radius: 6px; padding: 6px 12px; cursor: pointer; font: inherit; font-size: 13px;
  }
  button:hover:not(:disabled) { border-color: var(--accent); }
  button:disabled { opacity: .45; cursor: not-allowed; }
  button.primary { background: var(--accent); border-color: var(--accent); color: #fff; }
  button.danger  { background: var(--err); border-color: var(--err); color: #fff; }
  select, input[type=number] {
    background: var(--panel-2); color: var(--fg); border: 1px solid var(--line);
    border-radius: 6px; padding: 5px 8px; font: inherit; font-size: 13px;
  }
  label { font-size: 12px; color: var(--muted); display: inline-flex; gap: 5px; align-items: center; }
  .stage {
    flex: 1; min-height: 0; display: flex; align-items: center; justify-content: center;
    background: #000; position: relative; overflow: hidden;
  }
  .stage img {
    max-width: 100%; max-height: 100%;
    width: 100%; height: 100%;
    object-fit: contain; display: block;
    image-rendering: -webkit-optimize-contrast;
  }
  /* Масштабирование под размер окна: картинка вписывается целиком (п. 3.2) */
  .stage.actual img { width: auto; height: auto; max-width: none; max-height: none; object-fit: none; }
  .overlay {
    position: absolute; left: 12px; bottom: 12px;
    background: rgba(0,0,0,.66); color: #fff; padding: 6px 11px; border-radius: 7px;
    font-size: 12px; font-family: Consolas, monospace;
  }
  footer {
    display: flex; gap: 14px; flex-wrap: wrap; align-items: center;
    padding: 9px 16px; background: var(--panel); border-top: 1px solid var(--line);
    font-size: 12px; color: var(--muted);
  }
  #msg { margin-left: auto; }
  .err { color: var(--err); } .ok { color: var(--ok); }

  /* Поле имени: одинаково доступно администратору и наблюдателю. */
  #viewerName {
    background: var(--panel-2); color: var(--fg); border: 1px solid var(--line);
    border-radius: 6px; padding: 5px 9px; font: inherit; font-size: 13px; width: 170px;
  }
  #viewerName:focus { outline: none; border-color: var(--accent); }

  /* Список наблюдателей — только для администратора. */
  #clientPanel {
    background: var(--panel); border-top: 1px solid var(--line);
    max-height: 168px; overflow-y: auto; font-size: 12px;
  }
  #clientPanel h2 {
    margin: 0; padding: 7px 16px 5px; font-size: 11px; font-weight: 600;
    letter-spacing: .6px; text-transform: uppercase; color: var(--muted);
    position: sticky; top: 0; background: var(--panel);
  }
  #clientList { list-style: none; margin: 0; padding: 0 0 8px; }
  #clientList li {
    display: grid; grid-template-columns: 1fr auto auto auto;
    gap: 14px; align-items: baseline; padding: 4px 16px;
  }
  #clientList li + li { border-top: 1px solid var(--line); }
  .c-name { color: var(--fg); font-weight: 600; overflow-wrap: anywhere; }
  .c-addr, .c-rate { color: var(--muted); font-family: Consolas, monospace; }
  .c-seen { color: var(--muted); }
  #clientEmpty { padding: 2px 16px 10px; color: var(--muted); }
</style>
</head>
<body>

<header>
  <h1>ScreenShare</h1>
  <span id="state" class="pill off">соединение…</span>
  <input id="viewerName" maxlength="32" placeholder="Как вас зовут?"
         title="Под этим именем вас увидит тот, кто ведёт трансляцию">
  <span class="grow"></span>
  <button id="btnStart" class="primary" data-admin>Запустить</button>
  <button id="btnStop" class="danger" data-admin>Остановить</button>
  <button id="btnFit">Вписать</button>
  <button id="btnActual">1:1</button>
  <button id="btnTheme">Тема</button>
</header>

<div class="stage" id="stage">
  <!-- Трансляция идёт обычным MJPEG-потоком: скрипт для картинки не нужен -->
  <img id="view" src="/stream" alt="Трансляция экрана">
  <div class="overlay" id="overlay">—</div>
</div>

<!-- Список наблюдателей: элемент целиком удаляется, если права недостаточны. -->
<div id="clientPanel" data-admin>
  <h2>Кто смотрит</h2>
  <ul id="clientList"></ul>
  <div id="clientEmpty">пока никто не подключился</div>
</div>

<footer>
  <span id="adminSettings" data-admin style="display:contents">
    <label>FPS <input id="fps" type="number" min="1" max="60" value="10" style="width:62px"></label>
    <label>Качество <input id="quality" type="number" min="10" max="100" value="70" style="width:62px"></label>
    <label>Ширина <input id="mw" type="number" min="0" max="7680" value="0" style="width:72px"></label>
    <select id="monitor"></select>
    <label><input type="checkbox" id="cursor" checked> курсор</label>
    <button id="btnApply">Применить</button>
  </span>
  <span id="msg"></span>
</footer>

<script>
const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"]/g, c =>
  ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const bytes = n => {
  const u = ['Б','КБ','МБ','ГБ']; let i = 0; n = n || 0;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return n.toFixed(i ? 1 : 0) + ' ' + u[i];
};

const stage = $('stage');
let busy = false;

function note(text, ok) {
  $('msg').textContent = text;
  $('msg').className = ok ? 'ok' : 'err';
}

async function api(path, opts) {
  const r = await fetch(path, Object.assign({ cache: 'no-store' }, opts));
  const body = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(body.error || ('HTTP ' + r.status));
  return body;
}

// Роль запрашивается один раз: сервер всё равно не даст наблюдателю
// выполнить запрещённые действия, но прятать бесполезные кнопки
// честнее, чем оставлять их и получать 403 по нажатию.
(async () => {
  try {
    const me = await api('/api/me');

    // Логин из окна входа — главный источник имени. Он берётся только
    // если введён НЕ тот же логин, что и в прошлый раз: иначе человек,
    // вписавший своё имя в поле страницы, увидел бы его затёртым
    // при каждой загрузке. Запоминаем логин именно ради такого сравнения.
    const lastUser = localStorage.getItem('ss-user') || '';
    const typedNew = !!(me.user && me.user !== lastUser);
    if (typedNew) localStorage.setItem('ss-user', me.user);

    if (!$('viewerName').value || typedNew) {
      const initial = me.user || me.viewerName;
      if (initial && initial !== $('viewerName').value) {
        $('viewerName').value = initial;
        await saveName();
      }
    }

    if (me.isAdmin) return;

    document.querySelectorAll('[data-admin]').forEach(el => el.remove());
    $('state').title = 'режим наблюдателя: только просмотр';
  } catch {
    // Если роль определить не удалось, элементы остаются на месте,
    // а сервер всё равно применит ограничения сам.
  }
})();

// Имя сохраняется на сервере в cookie: cookie уходит и с запросом картинки
// /stream, где клиент регистрируется. Поэтому после смены имени поток
// приходится переоткрыть — иначе в списке осталось бы прежнее.
async function saveName() {
  const value = $('viewerName').value.trim();
  if (value === $('viewerName').dataset.saved) return;

  try {
    const r = await api('/api/viewer/name', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: value })
    });

    $('viewerName').value = r.name;
    $('viewerName').dataset.saved = r.name;
    localStorage.setItem('ss-name', r.name);

    $('view').src = '/stream?t=' + Date.now();
  } catch {
    // Сервер недоступен: имя применится при следующей попытке.
  }
}

$('viewerName').addEventListener('change', saveName);
$('viewerName').addEventListener('keydown', e => { if (e.key === 'Enter') $('viewerName').blur(); });
$('viewerName').value = localStorage.getItem('ss-name') || '';
$('viewerName').dataset.saved = $('viewerName').value;

// Список наблюдателей рисуется только у администратора: у наблюдателя
// сам элемент удалён, а сервер всё равно не отдаёт ему эти данные.
function renderClients(clients) {
  const list = $('clientList');
  if (!list) return;

  $('clientEmpty').style.display = clients.length ? 'none' : '';

  list.textContent = '';
  for (const c of clients) {
    const li = document.createElement('li');

    const name = document.createElement('span');
    name.className = 'c-name';
    name.textContent = c.name;            // textContent, не innerHTML:
    list.appendChild(li);                  // имя прислал клиент

    const addr = document.createElement('span');
    addr.className = 'c-addr';
    addr.textContent = c.remoteAddress;
    li.appendChild(addr);

    const rate = document.createElement('span');
    rate.className = 'c-rate';
    rate.textContent = bytes(c.bytesSent);
    li.appendChild(rate);

    const seen = document.createElement('span');
    seen.className = 'c-seen';
    const mins = Math.floor(c.connectedAt && (Date.now() - Date.parse(c.connectedAt)) / 60000);
    seen.textContent = mins >= 1 ? mins + ' мин' : 'только что';
    li.appendChild(seen);

    li.insertBefore(name, li.firstChild);
  }
}

// Событие ошибки <img> срабатывает и при обрыве потока — по нему перезапускаем.
$('view').addEventListener('error', () => {
  $('state').textContent = 'поток прерван';
  $('state').className = 'pill off';
  // Браузер сам повторит запрос к /stream; даём ему 1 секунду.
  setTimeout(() => { $('view').src = '/stream?t=' + Date.now(); }, 1000);
});

async function refresh() {
  let s;
  try {
    s = await api('/api/status');
  } catch (e) {
    $('state').textContent = 'нет связи с сервером';
    $('state').className = 'pill off';
    return;
  }

  $('state').textContent = s.running ? 'трансляция идёт' : 'остановлена';
  $('state').className = 'pill ' + (s.running ? 'on' : 'off');
  renderClients(s.clients || []);
  // У наблюдателя администраторские элементы удалены из DOM,
  // поэтому обращения к ним должны быть безопасными.
  if ($('btnStart')) $('btnStart').disabled = s.running;
  if ($('btnStop'))  $('btnStop').disabled  = !s.running;

  $('overlay').textContent =
    `${s.capture.width}×${s.capture.height}  ·  ${(s.capture.recentFps || s.capture.averageFps).toFixed(1)} fps  ·  ` +
    `${bytes(s.capture.recentFrameBytes || s.capture.averageFrameBytes)}/кадр  ·  клиентов ${s.clientCount}/${s.maxClients}  ·  ` +
    `отдано ${bytes(s.totalBytesSent)}`;

  // Поля формы заполняем только при первом получении, иначе
  // пользователь не сможет печатать значения во время автообновления.
  const fpsInput = $('fps');
  if (fpsInput && !fpsInput.dataset.touched) {
    $('fps').value = s.capture.targetFps;
    $('quality').value = s.capture.quality;
    $('mw').value = s.capture.width > 1600 ? 1600 : 0;
    $('cursor').checked = true;

    $('monitor').innerHTML = s.monitors
      .map(m => `<option value="${esc(m.deviceName)}">${esc(m.deviceName)} (${m.width}×${m.height})</option>`)
      .join('');
    $('monitor').value = s.capture.monitor || (s.monitors[0] && s.monitors[0].deviceName) || '';
  }
}

for (const id of ['fps','quality','mw']) {
  const el = $(id);
  if (el) el.addEventListener('input', () => { el.dataset.touched = '1'; });
}

async function act(path, label) {
  if (busy) return; busy = true;
  try { await api(path, { method: 'POST' }); note(label + ' — готово', true); }
  catch (e) { note(label + ' — ошибка: ' + e.message, false); }
  finally { busy = false; refresh(); }
}

$('btnStart').onclick = () => act('/api/start', 'Запуск');
$('btnStop').onclick  = () => act('/api/stop', 'Остановка');

$('btnApply').onclick = async () => {
  if (busy) return; busy = true;
  try {
    await api('/api/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        fps: +$('fps').value,
        quality: +$('quality').value,
        maxWidth: +$('mw').value,
        monitor: $('monitor').value,
        showCursor: $('cursor').checked
      })
    });
    note('Параметры применены без остановки трансляции', true);
  } catch (e) { note('Ошибка: ' + e.message, false); }
  finally { busy = false; refresh(); }
};

// Масштабирование на клиенте: «вписать» против оригинального размера (п. 3.2).
$('btnFit').onclick    = () => stage.classList.remove('actual');
$('btnActual').onclick = () => stage.classList.add('actual');

// Тёмная/светлая тема (п. 3.2).
$('btnTheme').onclick = () => document.documentElement.classList.toggle('light');
if (localStorage.getItem('ss-theme') === 'light') document.documentElement.classList.add('light');
document.addEventListener('click', () =>
  localStorage.setItem('ss-theme', document.documentElement.classList.contains('light') ? 'light' : 'dark'));

refresh();
setInterval(refresh, 1000);
</script>
</body>
</html>
""";
}