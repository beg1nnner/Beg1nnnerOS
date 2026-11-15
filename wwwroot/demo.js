// Демо-режим панели: «Показать на примере».
// Подменяет ответы api() вымышленными данными только внутри панели. Ничего не пишется ни в настройки,
// ни в базу, ни в статистику и не уходит в Twitch, чат, Telegram или Discord: любой запрос, который меняет данные,
// в демо-режиме не отправляется вовсе — вместо него тост «Это пример — действие не выполнено».
// Состояние не сохраняется между запусками: после перезапуска программы демо-режим выключен.
(function () {
  const EN = () => window.I18N_LANG === "en";
  const L = (ru, en) => EN() ? en : ru;
  const now = () => Date.now() / 1000;

  // ---------- обложки: градиенты без внешних картинок ----------
  function cover(h1, h2, shape) {
    const shapes = {
      sun: `<circle cx="100" cy="112" r="46" fill="#fff" fill-opacity=".85"/>`,
      wave: `<path d="M0 130 Q50 95 100 130 T200 130 V200 H0Z" fill="#fff" fill-opacity=".35"/><path d="M0 150 Q50 120 100 150 T200 150 V200 H0Z" fill="#fff" fill-opacity=".45"/>`,
      rings: `<circle cx="100" cy="100" r="62" fill="none" stroke="#fff" stroke-opacity=".5" stroke-width="10"/><circle cx="100" cy="100" r="28" fill="#fff" fill-opacity=".75"/>`,
      peaks: `<path d="M0 170 L60 90 L100 140 L140 70 L200 170Z" fill="#fff" fill-opacity=".55"/>`,
      grid: `<path d="M20 160 H180 M40 130 H160 M60 105 H140" stroke="#fff" stroke-opacity=".6" stroke-width="6" stroke-linecap="round"/><circle cx="100" cy="70" r="26" fill="#fff" fill-opacity=".8"/>`,
      drop: `<path d="M100 40 C130 90 150 110 150 135 a50 50 0 0 1 -100 0 C50 110 70 90 100 40Z" fill="#fff" fill-opacity=".7"/>`,
    };
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">`
      + `<stop offset="0" stop-color="hsl(${h1} 70% 52%)"/><stop offset="1" stop-color="hsl(${h2} 65% 32%)"/></linearGradient></defs>`
      + `<rect width="200" height="200" fill="url(#g)"/>${shapes[shape] || ""}</svg>`;
    return "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svg);
  }

  // вымышленные исполнители, треки и зрители
  const TRACKS = [
    {title: "Velvet Harbor — Lanterns at Dusk", dur: 214, cv: cover(28, 330, "sun")},
    {title: "Copper Tide — Northbound", dur: 241, cv: cover(200, 245, "wave")},
    {title: "Mira Solenne — Paper Satellites", dur: 198, cv: cover(265, 300, "rings")},
    {title: "The Quiet Engines — Glasshouse", dur: 225, cv: cover(150, 190, "peaks")},
    {title: "Lumo Park — Slow Static", dur: 263, cv: cover(320, 20, "grid")},
    {title: "Orchid Avenue — Weekend Radio", dur: 207, cv: cover(45, 80, "drop")},
    {title: "Hollow Pines — Ember Lines", dur: 189, cv: cover(10, 260, "wave")},
  ];
  const VIEWERS = ["Nightjar_77", "PixelMoth", "kettle_crow", "SableFinch", "Moonlit_Otter", "quietfox_tv", "BrambleBee", "Tundra_Lynx"];
  const STREAMER = "demo_streamer";

  const D = {on: false, t0: 0, notifs: 0, sec: "live"};

  // текущий трек «играет»: позиция растёт от времени входа в демо
  function current() {
    const t = TRACKS[0], pos = ((now() - D.t0) + 83) % t.dur;
    return {id: "d0", vid: "", title: t.title, page_url: "", duration: t.dur, requester: VIEWERS[0], login: VIEWERS[0].toLowerCase(),
      priority: 0, source: "youtube", paid: true, position: Math.round(pos * 10) / 10, paused: false, gain_db: 0, in_obs: true,
      loading: false, views: 184000, liked: false, artist: t.title.split(" — ")[0], channel: "", cover: t.cv};
  }
  function queue() {
    return TRACKS.slice(1, 7).map((t, i) => ({id: "dq" + i, vid: "", title: t.title, page_url: "", duration: t.dur,
      requester: VIEWERS[i + 1], login: VIEWERS[i + 1].toLowerCase(), priority: i === 0 ? 3 : i === 1 ? 1 : 0,
      source: i === 3 ? "yandex" : "youtube", paid: true, views: -1, fallback: false, artist: t.title.split(" — ")[0], channel: "", cover: t.cv}));
  }
  function music() {
    return {current: current(), queue: queue(), volume: 62, votes: 1, votes_needed: 3, toast: {id: 0, text: ""}, output: "app",
      fallback_status: L("резервный плейлист: 48 треков", "fallback playlist: 48 tracks"), liked_count: 12, restored: "", obs_connected: true};
  }
  function ready() {
    return {keys: true, quick_login: false, wizard_done: true,
      streamer: {ok: true, login: STREAMER, problem: "", missing_scopes: []}, bot: {ok: true, login: "demo_helper_bot"},
      sender: {mode: "bot", ok: true, login: "demo_helper_bot", error: "", allow: true}, eventsub: {ok: true, failed: {}},
      alerts_source: 1, obs: {player: true, chat: 1, alerts: 1, music: false},
      donationalerts: {ok: true, status: L("подключено", "connected"), configured: true}, rewards: L("синхронизировано", "synced"),
      stream: {live: true, title: L("Вечер музыки от зрителей", "Viewer music night"), category: "Just Chatting", uptime: uptime()}};
  }
  function uptime() {
    const m = 102 + Math.floor((now() - D.t0) / 60);
    return EN() ? `${Math.floor(m / 60)} h ${m % 60} min` : `${Math.floor(m / 60)} ч ${m % 60} мин`;
  }
  const ago = s => now() - s;
  function log() {
    const rows = [
      [3400, "system", L("Beg1nnnerOS запущен", "Beg1nnnerOS started")],
      [3380, "system", L("EventSub подключён", "EventSub connected")],
      [2900, "event", L(`Новый фолловер: ${VIEWERS[4]}`, `New follower: ${VIEWERS[4]}`)],
      [2400, "music", L(`Заказ от ${VIEWERS[1]}: «${TRACKS[1].title}» — в очереди (позиция 1)`, `Request from ${VIEWERS[1]}: "${TRACKS[1].title}" — queued (position 1)`)],
      [2100, "command", L(`!uptime от ${VIEWERS[2]}`, `!uptime from ${VIEWERS[2]}`)],
      [1800, "event", L(`Подписка: ${VIEWERS[3]} (уровень 1)`, `Subscription: ${VIEWERS[3]} (tier 1)`)],
      [1500, "alert", L(`Показ «Аплодисменты» для ${VIEWERS[5]}`, `Showing "Applause" for ${VIEWERS[5]}`)],
      [1100, "event", L(`Рейд от ${VIEWERS[6]}: 34 зрителя`, `Raid from ${VIEWERS[6]}: 34 viewers`)],
      [700, "event", L(`Донат: ${VIEWERS[7]} — 300 ₽ «Отличный трек!»`, `Donation: ${VIEWERS[7]} — 300 ₽ "Great track!"`)],
      [300, "music", L(`Играет: ${TRACKS[0].title} (заказ ${VIEWERS[0]})`, `Now playing: ${TRACKS[0].title} (requested by ${VIEWERS[0]})`)],
      [90, "command", L(`!song от ${VIEWERS[1]}`, `!song from ${VIEWERS[1]}`)],
    ];
    return rows.map(([s, kind, text], i) => ({id: i + 1, ts: ago(s), kind, text}));
  }
  function commands(real) {
    const custom = [
      ["!привет", "!hello", L("Привет, {user}! Устраивайся поудобнее", "Hi {user}! Make yourself comfortable")],
      ["!правила", "!rules", L("Будьте вежливы, без спама и спойлеров", "Be kind, no spam and no spoilers")],
      ["!песня", "!song", L("Сейчас играет то, что заказал чат — заказывай за баллы!", "Chat is picking the music — request with points!")],
      ["!кубик", "!dice", L("{user} бросает кубик: {random}", "{user} rolls the dice: {random}")],
    ].map(([ru, en, resp], i) => ({id: "dc" + i, name: EN() ? en : ru, aliases: [], response: resp, cooldown: 5, access: "everyone", enabled: true, random_max: 6}));
    return Object.assign({}, real || {}, {custom});
  }
  function timers() {
    return {repeating: [
      {id: "dt1", name: L("Правила чата", "Chat rules"), text: L("Напоминаем: уважаем друг друга, без спама", "Friendly reminder: be kind, no spam"), interval: 15, min_messages: 10, enabled: true},
      {id: "dt2", name: L("Заказ музыки", "Music requests"), text: L("Закажи трек за баллы канала — награда «Заказать музыку»", "Request a song with channel points — reward \"Request music\""), interval: 20, min_messages: 5, enabled: true},
      {id: "dt3", name: L("Соцсети", "Socials"), text: L("Анонсы стримов — в нашем канале, ссылка в описании", "Stream announcements — link in the description"), interval: 30, min_messages: 15, enabled: false},
    ], viewer: [], next: {name: L("Заказ музыки", "Music requests"), seconds: 312, messages_left: 0}};
  }
  function alerts() {
    return {items: [
      {id: "da1", name: L("Аплодисменты", "Applause"), media: "", media_kind: null, sound: "applause.mp3", text: "{user}!", cost: 300, reward_title: L("Аплодисменты", "Applause"), enabled: true, volume: 80},
      {id: "da2", name: L("Звезда вечера", "Star of the night"), media: "", media_kind: null, sound: "", text: L("{user} — звезда вечера!", "{user} is the star of the night!"), cost: 1000, reward_title: L("Звезда вечера", "Star of the night"), enabled: true, volume: 70},
      {id: "da3", name: L("Барабанная дробь", "Drum roll"), media: "", media_kind: null, sound: "drums.mp3", text: "", cost: 500, reward_title: L("Барабанная дробь", "Drum roll"), enabled: false, volume: 60},
    ], pending: [], clients: 1};
  }
  function stats(period) {
    const stream = period === "stream";
    const k = stream ? 1 : 6;
    return {
      chat: {messages: 486 * k, users: 57 * k, top: VIEWERS.slice(0, 5).map((v, i) => [v, (90 - i * 14) * k]),
        details: {commands: [["!song", 31 * k], ["!uptime", 18 * k], ["!rules", 9 * k]], hours: [...Array(24)].map((_, h) => h >= 18 && h <= 23 ? 40 + ((h * 37) % 60) : h < 2 ? 20 : 0),
          words: [["музыка", 42], ["привет", 38], ["класс", 27], ["трек", 25], ["вайб", 19], ["GG", 15]].map(([w, n]) => [EN() ? ({"музыка": "music", "привет": "hi", "класс": "nice", "трек": "track", "вайб": "vibe"}[w] || w) : w, n])}},
      music: {orders: 23 * k, rejected: 2 * k, skips: 3 * k, points: 11500 * k, top_tracks: TRACKS.slice(0, 5).map((t, i) => [t.title, 6 - i]),
        top_users: VIEWERS.slice(0, 5).map((v, i) => [v, 7 - i]), by_day: [3, 5, 2, 8, 6, 9, 7]},
    };
  }
  function history() {
    const items = TRACKS.slice(1).concat(TRACKS.slice(2, 5)).map((t, i) => ({ts: ago(600 + i * 420), title: t.title, url: "", vid: "", source: "youtube",
      requester: VIEWERS[(i + 2) % VIEWERS.length], duration: t.dur, fallback: i === 4}));
    return {items, total: items.length};
  }
  function votes() {
    const st = D.t0 - 46, dur = 120;
    const ch = [["Lo-fi", 18], ["Synthwave", 15], [L("Рок", "Rock"), 8]];
    const total = ch.reduce((a, c) => a + c[1], 0);
    return {ready: true, scopes: {polls: true, predictions: true}, poll_error: "", prediction_error: "", now: now(),
      poll: {id: "dp", title: L("Что играем дальше?", "What do we play next?"), status: "ACTIVE", active: true, started: st, ends: st + dur, ended: 0, duration: dur, total,
        points: false, points_per_vote: 0, choices: ch.map(([title, votes], i) => ({id: "c" + i, title, votes, points_votes: 0, pct: Math.round(votes / total * 100)}))},
      prediction: null, templates: {polls: [{title: L("Что играем дальше?", "What do we play next?"), choices: ["Lo-fi", "Synthwave", L("Рок", "Rock")]}], predictions: []},
      overlay: {hide_sec: 20, show_points: true}};
  }
  function giveaway() {
    return {status: "open", prize: L("Игра на выбор", "A game of your choice"), keyword: "!join", reward_title: "", subs_only: false, followers_only: true,
      sub_luck: 1, winners_wanted: 1, ends_in: 245, count: 6, tickets: 6,
      entries: VIEWERS.slice(0, 6).map((v, i) => ({login: v.toLowerCase(), name: v, sub: i % 3 === 0, tickets: 1})), winners: []};
  }
  function streams() {
    const titles = [L("Вечер музыки от зрителей", "Viewer music night"), L("Субботний марафон", "Saturday marathon"), L("Пробуем новинку", "Trying something new")];
    const ss = titles.map((title, i) => {
      const st = now() - 86400 * (i * 2 + 1) - 10800;
      return {id: "ds" + i, started: st, ended: st + 3600 * (3 + i % 2), title, category: ["Just Chatting", "Minecraft", "Indie"][i], duration: 3600 * (3 + i % 2),
        peak: 84 - i * 12, avg: 58 - i * 8, messages: 1650 - i * 280, chatters: 190 - i * 25, follows: 21 - i * 4, subs: 6 - i, bits: 900 - i * 200, raids: 1,
        donations: {RUB: 3800 - i * 700}, orders: 27 - i * 5, points: 13500 - i * 2000};
    });
    const sum = f => ss.reduce((a, s) => a + s[f], 0);
    return {streams: ss, total: {count: ss.length, hours: Math.round(sum("duration") / 360) / 10, peak: Math.max(...ss.map(s => s.peak)), avg: 50,
      messages: sum("messages"), follows: sum("follows"), subs: sum("subs"), bits: sum("bits"), orders: sum("orders"), donations: {RUB: ss.reduce((a, s) => a + s.donations.RUB, 0)}}};
  }
  function streamDetail(id) {
    const s = streams().streams.find(x => x.id === id) || streams().streams[0];
    const vs = [...Array(180)].map((_, k) => [s.started + 60 * k, Math.round(38 + 24 * Math.sin(k / 22) + k * 0.12 + (k % 5))]);
    return {summary: s, viewers: vs, top_chat: VIEWERS.slice(0, 3).map((v, i) => [v, 190 - i * 30]), top_requesters: VIEWERS.slice(0, 2).map((v, i) => [v, 6 - i * 2]),
      events: [{ts: s.started + 5000, type: "donation", name: VIEWERS[7], amount: 300, currency: "RUB"}, {ts: s.started + 3000, type: "follow", name: VIEWERS[4], amount: 1, currency: ""},
        {ts: s.started + 2000, type: "raid", name: VIEWERS[6], amount: 34, currency: ""}]};
  }
  function mod() {
    return {ready: true, scopes: true, count: 2, items: [
      {ts: ago(1300), user: "spam_bot_4821", user_id: "d1", rule: L("ссылки", "links"), detail: "", text: "free followers at …", action: "delete", action_title: L("Удалено", "Deleted"), ok: true, error: ""},
      {ts: ago(500), user: "CAPS_GUY", user_id: "d2", rule: L("капс", "caps"), detail: "92%", text: "WHY IS NOBODY ANSWERING", action: "timeout", action_title: L("Таймаут 60 с", "Timeout 60 s"), ok: true, error: ""},
    ]};
  }

  // ---------- маршруты чтения ----------
  // real — настоящий ответ на тот же GET (только для разделов без активности: версия, обновления, настройки, профили)
  const GET = {
    "/state": async real => Object.assign({}, await real(), {ready: ready(), music: music(), next_timer: {name: L("Заказ музыки", "Music requests"), seconds: 312, messages_left: 0},
      paused_events: false, alerts: {pending: 0, current: null, volume: 80}}),
    "/log": () => log(),
    "/commands": async real => commands(await real().catch(() => null)),
    "/timers": () => timers(),
    "/alerts": () => alerts(),
    "/stats": (_, q) => stats(q.get("period") || "stream"),
    "/votes": () => votes(),
    "/mod": () => mod(),
    "/mod/suspicious": () => ({scope: true, items: [{user_id: "d9", login: "fresh_acc_0001", name: "fresh_acc_0001", source: "age", reason: L("аккаунт создан сегодня", "account created today"),
      text: L("всем привет, загляните ко мне", "hi all, come check my channel"), ts: ago(240), count: 2}]}),
    "/mod/unban-requests": () => ({ready: true, scope: true, error: "", items: []}),
    "/music/history": () => history(),
    "/music/liked": () => ({items: [], total: 0}),
    "/music/fallback/next": () => ({status: L("резервный плейлист: 48 треков", "fallback playlist: 48 tracks"), items: TRACKS.slice(3, 6).map(t => ({title: t.title, duration: t.dur, liked: false}))}),
    "/pro/live": () => ({giveaway: giveaway()}),
    "/pro": async real => Object.assign({}, await real(), {giveaway: giveaway()}),
    "/pro/streams": () => streams(),
    "/precheck": () => ({disk_free_gb: 214, audio: {custom: false, found: true, output: "app"}, yandex: {enabled: false, token: false}, timers_on: 2, mod_on: true, alerts_on: true, captions_on: false, music_points: true}),
    "/remote": () => ({enabled: false, running: false, error: "", port: 8081, addresses: []}),
  };
  const PREFIX = [["/pro/streams/", p => streamDetail(decodeURIComponent(p.slice("/pro/streams/".length)))]];
  // «чтение» через POST: ответить можно, ничего не меняя
  const READ_POST = {
    "/votes/refresh": () => votes(),
    "/tools/youtube-check": () => ({ok: true, summary: L("YouTube доступен (пример)", "YouTube is reachable (example)"), steps: []}),
  };

  async function demoApi(path, body, method, rawApi) {
    const [p, qs] = path.split("?");
    const q = new URLSearchParams(qs || "");
    const isRead = body === undefined && (!method || method === "GET");
    const real = () => rawApi(path);
    if (isRead) {
      if (GET[p]) return GET[p](real, q);
      for (const [pre, fn] of PREFIX) if (p.startsWith(pre)) return fn(p);
      return real();   // справочные данные без активности (настройки, версия, устройства) — настоящие, только чтение
    }
    if (READ_POST[p]) return READ_POST[p](q);
    toast(tr("Это пример — действие не выполнено"));
    throw new Error("demo");
  }

  function banner(show) {
    let b = document.getElementById("demo-bar");
    if (show && !b) {
      b = document.createElement("div"); b.id = "demo-bar"; b.setAttribute("role", "status");
      b.innerHTML = `<svg class="ic" aria-hidden="true" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/></svg>`
        + `<b>${tr("Демо-режим — данные вымышленные")}</b><span>${tr("Действия ничего не меняют и никуда не отправляются.")}</span>`
        + `<button class="sm" onclick="demoStop()">${tr("Выйти из примера")}</button>`;
      (document.querySelector("main") || document.body).prepend(b);
    }
    if (!show && b) b.remove();
    document.documentElement.classList.toggle("demo", !!show);
  }

  window.DEMO = D;
  window.demoApi = demoApi;
  window.demoStart = async function () {
    if (D.on) return;
    D.on = true; D.t0 = now(); D.notifs = NOTIFS.length;
    D.sec = (document.querySelector("main > section:not(.hidden)") || {id: "p-live"}).id.slice(2);
    banner(true);
    try { if (WZ.open) closeWizard(false); } catch (e) {}
    await poll(); loadLiveMetrics(); loadLog();
    go("live");
    toast(tr("Показан пример работающей программы"));
  };
  window.demoStop = async function () {
    if (!D.on) return;
    D.on = false;
    banner(false);
    // центр уведомлений — без сообщений, появившихся в примере
    if (NOTIFS.length > D.notifs) { const n = NOTIFS.length - D.notifs; NOTIFS.splice(0, n); NOTIF_UNREAD = Math.max(0, NOTIF_UNREAD - n); renderNotifs(); }
    document.querySelectorAll("#toasts .toast").forEach(t => t.remove());
    // настоящее состояние — заново с сервера, без перезапуска
    MH_ITEMS = []; CHL_DONE = false;
    try { await loadSettings(); } catch (e) {}
    await poll(); loadLiveMetrics(); loadLog();
    go(D.sec || "live");
  };
})();
