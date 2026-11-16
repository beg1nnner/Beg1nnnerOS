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
  // Дополнительные демо-данные, которые можно задать снаружи (например, сценарием записи ролика):
  // now — свой текущий трек {title, duration, cover, requester, pos0, t0, paused}, log — свои строки журнала [{ago, kind, text}].
  const EXTRA = {now: null, log: []};
  window.demoSet = function (patch) { Object.assign(EXTRA, patch || {}); };

  // текущий трек «играет»: позиция растёт от времени входа в демо
  function current() {
    const x = EXTRA.now;
    if (x) {
      const pos = Math.min(x.duration, x.paused ? x.pos0 : x.pos0 + (now() - x.t0));
      return {id: "dx", vid: "", title: x.title, page_url: "", duration: x.duration, requester: x.requester, login: String(x.requester).toLowerCase(),
        priority: 0, source: "youtube", paid: true, position: Math.round(pos * 10) / 10, paused: !!x.paused, gain_db: 0, in_obs: true,
        loading: false, views: -1, liked: false, artist: x.title.split(" — ")[0], channel: "", cover: x.cover};
    }
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
    if (EXTRA.now) rows[rows.length - 2] = [300, "music", L(`Играет: ${EXTRA.now.title} (заказ ${EXTRA.now.requester})`, `Now playing: ${EXTRA.now.title} (requested by ${EXTRA.now.requester})`)];
    EXTRA.log.forEach(r => rows.push([r.ago, r.kind, r.text]));
    rows.sort((a, b) => b[0] - a[0]);
    return rows.map(([s, kind, text], i) => ({id: i + 1, ts: ago(s), kind, text}));
  }
  // модели распознавания: в примере Whisper Base и Vosk уже скачаны (настоящее состояние программы не трогаем)
  function speech() {
    const m = [["whisper", "tiny", "Whisper Tiny", "для слабых ПК", 32, 32], ["whisper", "base", "Whisper Base", "рекомендуем для субтитров", 60, 60],
      ["whisper", "small", "Whisper Small", "точнее, нужен мощный ПК", 190, 190], ["vosk", "ru", "Vosk · русский", "быстрая, для голосовых команд", 45, 90],
      ["vosk", "en", "Vosk · English", "быстрая, для голосовых команд", 40, 70]];
    const ready = new Set(["whisper:base", "vosk:" + (EN() ? "en" : "ru")]);
    return {models: m.map(([engine, key, name, hint, size_mb, disk_mb]) => ({id: engine + ":" + key, engine, key, name, hint, size_mb, disk_mb,
        status: ready.has(engine + ":" + key) ? "ready" : "missing", part_mb: 0, in_use: engine + ":" + key === "whisper:base"})),
      free_mb: 120000, recommended: {captions: "whisper:base", voice: "vosk:" + (EN() ? "en" : "ru")}, ready: {captions: true, voice: true},
      captions_model: "whisper:base", voice_model: "vosk:" + (EN() ? "en" : "ru"), checked_ok: true, seq: 0,
      download: {active: false, id: "", name: "", phase: "", attempt: 0, done_mb: 0, total_mb: 0, speed_kb: 0, queue: [], error: "", error_ids: []}};
  }
  // субтитры и голосовые команды: распознавание «работает», но в демо ничего не распознано — последняя фраза пустая
  async function captionsState(real) {
    const r = await real().catch(() => ({}));
    return Object.assign({}, r, {enabled: true, running: true, status: L("слушаю микрофон (Whisper)", "listening to the microphone (Whisper)"),
      lines: [], partial: "", speaking: false, level: 0.12 + 0.1 * Math.abs(Math.sin(now() * 3)), now: now(),
      engine: "whisper", model: "base", has_model: true, accel: L("видеокарта (Vulkan)", "GPU (Vulkan)"), speed: 6.4, restart_needed: false,
      speech: speech(),
      voice: {enabled: true, wake: L("бот", "bot"), hide: true, chime: true, testing: false, test_left: 0, last: null,
        commands: [
          {key: "skip", title: "Следующий трек", phrases: ["следующий трек", "скип", "next track"], on: true, custom: []},
          {key: "pause", title: "Пауза", phrases: ["пауза", "поставь на паузу", "pause"], on: true, custom: []},
          {key: "resume", title: "Продолжить", phrases: ["продолжай", "включи музыку", "resume"], on: true, custom: []},
          {key: "clip", title: "Сделать клип", phrases: ["сделай клип", "клип", "clip that"], on: true, custom: []}]}});
  }
  function wheelState() {
    return {current: null, current_result: "",
      queue: [{id: 7, wheel: L("Колесо фортуны", "Wheel of fortune"), user: VIEWERS[1], source: "reward", test: false, elapsed_ms: 0, duration_ms: 0}],
      history: [
        {ts: ago(600), wheel: L("Колесо фортуны", "Wheel of fortune"), user: VIEWERS[3], source: "reward", result: L("Приветствие в чат", "Chat greeting"), action: L("сообщение в чат", "chat message"), test: false, giveaway: false},
        {ts: ago(1500), wheel: L("Колесо фортуны", "Wheel of fortune"), user: VIEWERS[5], source: "chat", result: L("Ничего", "Nothing"), action: "", test: false, giveaway: false}],
      alerts: [{id: "da1", name: L("Аплодисменты", "Applause")}, {id: "da2", name: L("Звезда вечера", "Star of the night")}]};
  }
  // колесо зрителей: вымышленные варианты, покупки «приходят» сами — раз в несколько секунд, без запросов к Twitch
  const VW_OPTS = [["Hollow Knight", 4], ["Stardew Valley", 3], ["Celeste", 2], ["Outer Wilds", 3], ["Hades", 2], ["Terraria", 1],
    ["Portal 2", 2], ["Dead Cells", 1], ["Disco Elysium", 1], ["Subnautica", 1], ["Inside", 1], ["Into the Breach", 1]];
  const VW_COSTS = [500, 500, 500, 2000, 500, 10000, 500, 2000];
  function vwDemo() {
    const t = now() - D.t0, n = Math.min(40, 18 + Math.floor(t / 4));   // новая покупка примерно раз в 4 секунды
    const sec = new Map(), contribs = [];
    for (let i = 0; i < n; i++) {
      // детерминированно: вариант и цена зависят только от номера покупки
      let k = (i * 7 + (i >> 2)) % VW_OPTS.reduce((a, x) => a + x[1], 0), oi = 0;
      while (k >= VW_OPTS[oi][1]) { k -= VW_OPTS[oi][1]; oi++; }
      const [name] = VW_OPTS[oi], pts = VW_COSTS[(i * 5 + 3) % VW_COSTS.length], user = VIEWERS[(i * 3) % VIEWERS.length];
      if (!sec.has(name)) sec.set(name, {id: "vs" + oi, name, points: 0, contribs: [], users: new Set(), order: i});
      const x = sec.get(name); x.points += pts; x.users.add(user);
      x.contribs.unshift({user, points: pts, ts: D.t0 - 600 + i * 15 + Math.max(0, i - 17) * 4, source: "reward", confirmed: true});
      contribs.push({user, points: pts});
    }
    const list = [...sec.values()].sort((a, b) => a.order - b.order), total = list.reduce((a, x) => a + x.points, 0);
    const pct = p => { const v = p * 100 / total; return v >= 10 ? String(Math.round(v)) : String(Math.round(v * 10) / 10); };
    const last = contribs[contribs.length - 1];
    const sectors = list.map(x => ({id: x.id, name: x.name, points: x.points, chance: pct(x.points), contributors: x.users.size, similar: "", similar_name: "",
      refundable: 0, not_refundable: x.contribs.length, no_answer: false, contribs: x.contribs}));
    // один вариант с опечаткой — пример пометки «Похоже на…»
    sectors.push({id: "vs-typo", name: "Hollow Knigth", points: 500, chance: pct(500), contributors: 1, similar: "vs0", similar_name: "Hollow Knight",
      refundable: 0, not_refundable: 1, no_answer: false, contribs: [{user: VIEWERS[6], points: 500, ts: now() - 40, source: "reward", confirmed: true}]});
    const total2 = total + 500, ends = Math.max(0, 300 - Math.floor(t));
    return {id: "demo-vw", exists: true, status: "open", participants: false, ends_in: ends, next_in: 0, total: total2, round: 0, sectors,
      out_sectors: [], pending: [{id: "p1", text: L("Пройти игру с закрытыми глазами", "Beat the game blindfolded"), user: VIEWERS[4], points: 2000, ts: now() - 25}],
      last: {user: last.user, points: last.points, ts: now() - (t % 4)}, winner: null,
      history: [{ts: ago(86400), wheel: L("Во что играем дальше", "What do we play next"), winner: "Outer Wilds", chance: "23", points: 9500, sectors: 11, contributors: 17, elimination: false}],
      rewards: {status: L("синхронизировано", "synced"), no_points: false, connected: true, scope_missing: false, foreign: 0},
      overlay: {id: "demo-vw", name: L("Во что играем дальше", "What do we play next"), status: "open", label: "", show: true, ends_in: ends, elimination: false,
        left: sectors.length, sectors: sectors.map(x => ({id: x.id, name: x.name, weight: x.points})),
        top: [...sectors].sort((a, b) => b.points - a.points).slice(0, 8).map(x => ({name: x.name, pct: x.chance})),
        last: {user: last.user, points: last.points}, winner: ""}};
  }
  // колесо зрителей в настройках примера (только в памяти панели; после выхода из примера настройки загружаются заново)
  window.demoViewerWheel = function () {
    if (!S || !S.wheel || !S.wheel.wheels || S.wheel.wheels.some(w => w.id === "demo-vw")) return;
    S.wheel.wheels.push({id: "demo-vw", name: L("Во что играем дальше", "What do we play next"), type: "viewers", reward: {enabled: false, title: "", cost: 500}, sectors: [],
      viewer: {mode: "options", rewards: [{title: L("Предложить игру", "Suggest a game"), cost: 500, prompt: "", per_user: 0, total: 0, cooldown: 0, color: "#8B5CF6"},
        {title: L("Предложить игру ×4", "Suggest a game ×4"), cost: 2000, prompt: "", per_user: 3, total: 0, cooldown: 0, color: "#EC4899"},
        {title: L("Предложить игру ×20", "Suggest a game ×20"), cost: 10000, prompt: "", per_user: 1, total: 5, cooldown: 60, color: "#F59E0B"}],
        approval: true, chat_add: true, add_template: L("{user} добавил «{option}» — теперь {chance}%", "{user} added “{option}” — now {chance}%"), chat_result: true,
        result_template: L("Победил «{option}» — {chance}%, вложено {points}", "“{option}” wins — {chance}%, {points} points in"), timer_min: 5, extend_sec: 10, extend_max: 120,
        show: "collect", spin_mode: "normal", auto_next: false, next_sec: 5, after_spin: "keep", spin_sec: 12, confirm_on_spin: false, after_finish: "hide",
        follower_days: 0, use_blocklist: true, donation: {enabled: true, rate: 10}, giveaway: false}});
  };
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
      {id: "da1", name: L("Аплодисменты", "Applause"), media: "", media_kind: null, sound: "applause.mp3", text: "{user}!", cost: 300, reward_title: L("Аплодисменты", "Applause"), enabled: true, volume: 80, duration: 5, duration_mode: "manual", scale: 1, x: 760, y: 340, random_pos: false, cooldown_global: 30, cooldown_user: 120},
      {id: "da2", name: L("Звезда вечера", "Star of the night"), media: "", media_kind: null, sound: "", text: L("{user} — звезда вечера!", "{user} is the star of the night!"), cost: 1000, reward_title: L("Звезда вечера", "Star of the night"), enabled: true, volume: 70, duration: 7, duration_mode: "manual", scale: 1, x: 760, y: 340, random_pos: false, cooldown_global: 30, cooldown_user: 120},
      {id: "da3", name: L("Барабанная дробь", "Drum roll"), media: "", media_kind: null, sound: "drums.mp3", text: "", cost: 500, reward_title: L("Барабанная дробь", "Drum roll"), enabled: false, volume: 60, duration: 4, duration_mode: "manual", scale: 1, x: 760, y: 340, random_pos: false, cooldown_global: 30, cooldown_user: 120},
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
    "/backups": () => [0, 1, 3].map((d, i) => ({name: `backup-${new Date((D.t0 - d * 86400) * 1000).toISOString().slice(0, 10)}-2130.zip`, size: 412000 + i * 5300})),
    "/mod/unban-requests": () => ({ready: true, scope: true, error: "", items: [
      {id: "ur1", name: "Quiet_Lantern", created_at: new Date((D.t0 - 3 * 3600) * 1000).toISOString(),
        text: L("Простите за капс вчера, больше не повторится", "Sorry for the caps yesterday, won't happen again")},
      {id: "ur2", name: "MossyPebble", created_at: new Date((D.t0 - 26 * 3600) * 1000).toISOString(),
        text: L("Это был мой младший брат, можно вернуть доступ?", "That was my little brother, can I get access back?")}]}),
    "/music/history": () => history(),
    "/music/liked": () => ({items: [], total: 0}),
    "/music/fallback/next": () => ({status: L("резервный плейлист: 48 треков", "fallback playlist: 48 tracks"), items: TRACKS.slice(3, 6).map(t => ({title: t.title, duration: t.dur, liked: false}))}),
    "/pro/live": () => ({giveaway: giveaway()}),
    "/pro": async real => Object.assign({}, await real(), {giveaway: giveaway()}),
    "/pro/streams": () => streams(),
    "/precheck": () => ({disk_free_gb: 214, audio: {custom: false, found: true, output: "app"}, yandex: {enabled: false, token: false}, timers_on: 2, mod_on: true, alerts_on: true, captions_on: false, music_points: true}),
    "/remote": () => ({enabled: false, running: false, error: "", port: 8081, addresses: []}),
    "/captions": (real, q) => captionsState(real),
    "/models": () => speech(),
    "/captions/devices": () => ({items: [{id: L("Микрофон (USB Audio)", "Microphone (USB Audio)")}]}),
    "/wheel": () => wheelState(),
    "/vwheel": () => vwDemo(),
  };
  // итоги стрима — в том же виде, что собирает программа (Announce.Summary), из вымышленных цифр примера
  function streamSummaryText(id) {
    const d = streamDetail(id), s = d.summary, h = Math.floor(s.duration / 3600), m = Math.round(s.duration % 3600 / 60);
    const n = v => v.toLocaleString(EN() ? "en-US" : "ru-RU");
    const text = EN()
      ? [`📊 Stream summary «${s.title}»`, "", `⏱ ${h} h ${m} min · 🎮 ${s.category}`, `👀 Peak viewers: ${n(s.peak)} · average ${n(s.avg)}`,
         `💬 Messages: ${n(s.messages)} from ${n(s.chatters)} viewers`, `💜 New followers: ${s.follows} · subs: ${s.subs} · bits: ${n(s.bits)} · raids: ${s.raids}`,
         `💸 Donations: ${n(s.donations.RUB)} ₽`, `🎵 Tracks requested: ${s.orders}`, `🏆 Most active: ${d.top_chat.map(x => `${x[0]} (${x[1]})`).join(", ")}`,
         "🎶 Stream tracks:", ...TRACKS.slice(0, 3).map((t, i) => `  ${i + 1}. ${t.title}`), "", "Thanks to everyone who was on the stream! ❤️"]
      : [`📊 Итоги стрима «${s.title}»`, "", `⏱ ${h} ч ${m} мин · 🎮 ${s.category}`, `👀 Пик зрителей: ${n(s.peak)} · в среднем ${n(s.avg)}`,
         `💬 Сообщений: ${n(s.messages)} от ${n(s.chatters)} зрителей`, `💜 Новых фолловеров: ${s.follows} · сабов: ${s.subs} · битсов: ${n(s.bits)} · рейдов: ${s.raids}`,
         `💸 Донаты: ${n(s.donations.RUB)} ₽`, `🎵 Заказано треков: ${s.orders}`, `🏆 Самые активные: ${d.top_chat.map(x => `${x[0]} (${x[1]})`).join(", ")}`,
         "🎶 Треки стрима:", ...TRACKS.slice(0, 3).map((t, i) => `  ${i + 1}. ${t.title}`), "", "Спасибо всем, кто был на стриме! ❤️"];
    return {text: text.join("\n"), thanks: null};
  }
  const PREFIX = [["/pro/streams/", p => streamDetail(decodeURIComponent(p.slice("/pro/streams/".length)))],
    ["/stream-summary/", p => streamSummaryText(decodeURIComponent(p.slice("/stream-summary/".length)))]];
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
    try { window.demoViewerWheel(); } catch (e) {}
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
