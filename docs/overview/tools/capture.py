"""Скриншоты настоящего интерфейса Beg1nnnerOS для документа-обзора.

Панель и оверлеи — настоящие страницы из wwwroot/, их отдаёт заглушка сервера (tools/stub/server.py) с вымышленными
данными. Панель снимается в демо-режиме «Показать на примере» и в режиме «Безопасно для стрима».
Окно 1440×900, плотность пикселей 2, тема «Студия».

Кроме картинок пишет screenshots/shots.json: размеры и положения выносок (их координаты берутся у настоящих
элементов страницы, поэтому номер на картинке всегда стоит на нужном месте).

Запуск: python3 docs/overview/tools/capture.py [имя-снимка …]
"""
import asyncio, json, os, re, subprocess, sys, time, urllib.request
from playwright.async_api import async_playwright

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
OUT = os.path.join(HERE, "..", "screenshots")
PORT = 8099
BASE = f"http://127.0.0.1:{PORT}"
ONLY = sys.argv[1:]
META = {}

CFG = open(os.path.join(ROOT, "Core", "Config.cs"), encoding="utf-8").read()
TEMPLATES = {}
for k, txt, en in re.findall(r'new\("(\w+)", "[^"]+", new\[\][^)]*\},\s*"([^"]*)", (true|false)\)', CFG):
    TEMPLATES[k] = {"enabled": en == "true", "screen": k in ("donation", "follow", "sub", "cheer", "raid", "hype"), "text": txt}

# ---------- вспомогательный код в странице ----------
HELP = r"""
window.__el = s => {
  if (s.startsWith("css:")) return document.querySelector(s.slice(4));
  const hs = [...document.querySelectorAll("main h2, main h3, .modal h2, .modal h3, #wizard h2, #wizard h3")]
    .filter(h => h.offsetParent && h.textContent.trim().startsWith(s));
  return hs.length ? hs[0].closest(".card, .al-card, .modal, .wz, .sp") : null;
};
window.__rect = (specs, pad) => {
  const els = specs.map(__el);
  if (els.some(e => !e)) return {missing: specs.filter((s, i) => !els[i])};
  const r = els.map(e => e.getBoundingClientRect());
  const x = Math.max(0, Math.min(...r.map(a => a.left)) - pad), y = Math.max(0, Math.min(...r.map(a => a.top)) - pad);
  return {x, y, width: Math.max(...r.map(a => a.right)) + pad - x, height: Math.max(...r.map(a => a.bottom)) + pad - y};
};
window.__pt = (s, ax, ay) => { const e = __el(s); if (!e) return null; const r = e.getBoundingClientRect();
  return {x: r.left + r.width * ax, y: r.top + r.height * ay}; };
"""
CLEAN = """
document.querySelectorAll('.toast').forEach(t => t.remove());
const bar = document.getElementById('demo-bar'); if (bar) bar.style.display = 'none';   // баннер примера не нужен на картинках
"""


def scene(d):
    urllib.request.urlopen(urllib.request.Request(BASE + "/api/scene", data=json.dumps(d).encode(), headers={"Content-Type": "application/json"}))


def want(name):
    return not ONLY or any(name.startswith(o) for o in ONLY)


async def patch_routes(pg):
    async def sett(route):
        if route.request.method != "GET":
            return await route.continue_()
        r = await route.fetch(); j = await r.json(); d = j["data"]
        d["events"]["templates"] = TEMPLATES
        d["tools"]["translate"].update(enabled=True, auto=True); d["tools"]["layout"]["enabled"] = True
        d["mod"]["enabled"] = True
        d["announce"].update(tg_enabled=True, dc_enabled=True, tg_chat="@my_stream_news")
        d["music"].update(blocked_users=["spam_account_01"], blocked_tracks=["Loud Noise — Test Tone"],
                          blocked_artists=["Static Choir"], blocked_channels=["@RandomLoopsChannel"])
        d["hotkeys"]["enabled"] = True
        await route.fulfill(status=200, content_type="application/json", body=json.dumps(j, ensure_ascii=False))
    await pg.route("**/api/settings", sett)


async def go(pg, sec, tab=None, wait=1400):
    await pg.evaluate(f"go('{sec}'{', ' + repr(tab) if tab else ''})")
    await pg.evaluate("document.querySelector('main').scrollTop = 0; window.scrollTo(0, 0)")
    await pg.wait_for_timeout(wait)
    await pg.evaluate(CLEAN)
    await pg.evaluate("document.fonts.ready")


async def callouts(pg, items, ox=0, oy=0, w=1440, h=900):
    out = []
    for i, (spec, label, ax, ay) in enumerate(items, 1):
        p = await pg.evaluate(f"__pt({json.dumps(spec)}, {ax}, {ay})")
        if not p:
            print("  выноска не найдена:", spec); continue
        out.append({"n": i, "x": round((p["x"] - ox) / w * 100, 2), "y": round((p["y"] - oy) / h * 100, 2), "label": label})
    return out


async def screen(pg, name, items=()):
    """Экран целиком: окно 1440×900."""
    if not want(name): return
    await pg.set_viewport_size({"width": 1440, "height": 900}); await pg.wait_for_timeout(400)
    await pg.evaluate(CLEAN); await pg.wait_for_timeout(250)
    await pg.screenshot(path=f"{OUT}/{name}.png")
    META[name] = {"kind": "screen", "w": 1440, "h": 900, "callouts": await callouts(pg, items)}
    await pg.set_viewport_size({"width": 1440, "height": 2200}); await pg.wait_for_timeout(300)
    print("ok", name)


async def frag(pg, name, specs, pad=16, items=(), scroll=True):
    """Фрагмент: карточка или блок крупно, по элементу, с ровными отступами."""
    if not want(name): return
    await pg.evaluate(CLEAN)
    r = await pg.evaluate(f"__rect({json.dumps(specs)}, {pad})")
    if "missing" in r:
        print("НЕ НАЙДЕНО", name, r["missing"]); return
    if scroll and (r["y"] + r["height"] > 2150 or r["y"] < 0):
        await pg.evaluate(f"document.querySelector('main').scrollBy(0, {r['y'] - 90})"); await pg.wait_for_timeout(350)
        r = await pg.evaluate(f"__rect({json.dumps(specs)}, {pad})")
    await pg.wait_for_timeout(200)
    await pg.screenshot(path=f"{OUT}/{name}.png", clip=r)
    META[name] = {"kind": "frag", "w": round(r["width"]), "h": round(r["height"]),
                  "callouts": await callouts(pg, items, r["x"], r["y"], r["width"], r["height"])}
    print("ok", name, round(r["width"]), round(r["height"]))


async def overlay(browser, path, name, w, h, prep=None, wait=2500, routes=None, transparent=True):
    """Оверлей: страница для OBS на прозрачном фоне (в вёрстке кладётся на нейтральный тёмный фон)."""
    if not want(name): return
    pg = await browser.new_page(viewport={"width": w, "height": h}, device_scale_factor=2)
    for pat, body in (routes or {}).items():
        await pg.route(pat, lambda r, _q=None, body=body: r.fulfill(status=200, content_type="application/json", body=json.dumps(body, ensure_ascii=False)))
    if transparent:
        await pg.add_init_script("""document.addEventListener('DOMContentLoaded',()=>{const st=document.createElement('style');
          st.textContent='html,body{background:transparent!important}' + (location.pathname==='/chat' ? ' img{display:none!important}' : '');
          document.head.appendChild(st)})""")
    await pg.goto(BASE + path); await pg.wait_for_timeout(700)
    if prep: await prep(pg)
    await pg.wait_for_timeout(wait)
    await pg.screenshot(path=f"{OUT}/{name}.png", omit_background=transparent)
    META[name] = {"kind": "overlay", "w": w, "h": h, "callouts": []}
    print("ok", name); await pg.close()


async def run(browser, pg):
    # ---------- Эфир ----------
    await go(pg, "live", wait=2200)
    await screen(pg, "s-live", [("css:#ready", "Состояние подключений", .02, .5), ("css:#live-metrics", "Показатели стрима", .02, .2),
                                ("css:[data-card=now]", "«Сейчас играет» и очередь", .03, .04), ("css:[data-card=quick]", "Быстрые действия", .06, .05)])
    await frag(pg, "f-live-now", ["css:[data-card=now]"], items=[("css:#np-art", "Обложка и цвет карточки", .5, .5), ("css:.np-ctl", "Пауза, пропуск, лайк и блокировка", .1, .5)])
    await frag(pg, "f-live-quick", ["css:[data-card=quick]"])
    if want("f-live-layout"):
        await pg.evaluate("layEdit(true)"); await pg.wait_for_timeout(500)
        await frag(pg, "f-live-layout", ["css:.live-tools", "css:#live-grid"], pad=12,
                   items=[("css:[data-card=quick] .lay-handle", "Ручка: перетащить карточку", .5, .5), ("css:[data-card=quick] .lay-pin", "Закрепить вверху колонки", .5, .5),
                          ("css:[data-card=quick] .lay-hide", "Скрыть карточку", .5, .5), ("css:#lay-reset", "Сбросить расположение", .5, .5)])
        await pg.evaluate("layEdit(false)")
    if want("f-precheck"):
        await pg.evaluate("openPrecheck()"); await pg.wait_for_timeout(2200)
        await frag(pg, "f-precheck", ["css:.modal"], pad=0); await pg.evaluate("closeModal()"); await pg.wait_for_timeout(300)
    if want("f-remote"):
        await pg.evaluate("openRemote()"); await pg.wait_for_timeout(1000)
        await pg.evaluate("REMOTE={enabled:true,running:true,error:'',port:8081,addresses:['http://192.168.1.20:8081/?k=demo-example']}; renderRemote()")
        await pg.wait_for_timeout(400)
        # QR в примере ведёт на вымышленный адрес; размытие «Безопасно для стрима» снимаем только для этого снимка
        await pg.evaluate("document.documentElement.classList.remove('safe')"); await pg.wait_for_timeout(200)
        await frag(pg, "f-remote", ["css:.modal"], pad=0); await pg.evaluate("closeModal(); document.documentElement.classList.add('safe')"); await pg.wait_for_timeout(300)

    # ---------- Музыка ----------
    await go(pg, "music", "player")
    await screen(pg, "s-music", [("Ручной заказ", "Ручной заказ без баллов", .02, .2), ("Очередь", "Очередь: порядок, приоритет, кто заказал", .02, .1)])
    await go(pg, "music", "orders")
    await frag(pg, "f-music-rewards", ["Награды за баллы канала"])
    await frag(pg, "f-music-sources", ["Источники и ограничения"])
    await frag(pg, "f-music-volume", ["Громкость и вывод звука"])
    await frag(pg, "f-music-xfade", ["Плавные переходы"])
    await go(pg, "music", "fallback")
    await frag(pg, "f-music-fallback", ["Резервный плейлист", "Дальше в резерве"])
    await go(pg, "music", "blocks")
    await frag(pg, "f-music-blocks", ["Блокировки", "Чёрный список исполнителей и каналов"])
    await go(pg, "music", "mhist")
    await frag(pg, "f-music-hist", ["css:#p-music [data-tab=mhist] .card"])
    await go(pg, "music", "mstats")
    await frag(pg, "f-music-stats", ["Статистика музыки"])
    await go(pg, "music", "overlay")
    await frag(pg, "f-ov-player-themes", ["Тема плеера в OBS"])

    # ---------- Чат ----------
    await go(pg, "chat", "cmds")
    await screen(pg, "s-chat", [("Свои команды", "Свои команды", .02, .1), ("Таймеры", "Таймеры по расписанию", .05, .1)])
    await frag(pg, "f-chat-cmds", ["Свои команды"])
    await frag(pg, "f-chat-std", ["Стандартные команды"], scroll=True)
    await frag(pg, "f-chat-autopost", ["Автосообщение в начале стрима"])
    if want("f-chat-import"):
        await pg.evaluate("openImport()"); await pg.wait_for_timeout(1000)
        await frag(pg, "f-chat-import", ["css:.modal"], pad=0); await pg.evaluate("closeModal()"); await pg.wait_for_timeout(300)
    await go(pg, "chat", "tools")
    await frag(pg, "f-chat-tools", ["Локальный перевод", "Автоматическое исправление раскладки"])
    await go(pg, "chat", "obschat")
    await frag(pg, "f-ov-chat-themes", ["Тема чата в OBS"])
    await go(pg, "chat", "cstats")
    await frag(pg, "f-chat-activity", ["Активность чата"])

    # ---------- МедиаАлерты ----------
    await go(pg, "alerts")
    await screen(pg, "s-alerts", [("css:#al-list .al-card", "Алерт: медиа, цена, громкость", .1, .1), ("css:.al-new", "Новый МедиаАлерт", .5, .3)])
    if want("f-alert-edit"):
        await pg.evaluate("editAlert('da2')"); await pg.wait_for_timeout(1300)
        await frag(pg, "f-alert-edit", ["css:.modal"], pad=0); await pg.evaluate("closeModal()"); await pg.wait_for_timeout(300)

    # ---------- События ----------
    await go(pg, "events")
    await screen(pg, "s-events", [("События канала", "Событие: текст в чат и показ на экране", .05, .08), ("Очередь уведомлений", "Очередь и пауза уведомлений", .05, .1)])
    await frag(pg, "f-events-screen", ["Оверлей в OBS"])
    await frag(pg, "f-events-da", ["DonationAlerts"])
    await frag(pg, "f-announce", ["css:#p-events .card:has(h3.u-m-3-0-1)"])

    # ---------- Опросы ----------
    await go(pg, "votes", wait=2200)
    await screen(pg, "s-votes")

    # ---------- Субтитры и голосовые команды ----------
    await go(pg, "captions", wait=2200)
    await screen(pg, "s-captions", [("css:#sp-cap", "Модель распознавания — скачивается по кнопке", .03, .2), ("css:.cap-hero", "Предпросмотр и микрофон", .03, .08)])
    await frag(pg, "f-speech", ["css:#sp-cap"])
    if want("f-speech-adv"):
        await pg.evaluate("SP_ADV['sp-cap']=true; SP_SIG['sp-cap']=''; spRender('sp-cap')"); await pg.wait_for_timeout(300)
        await frag(pg, "f-speech-adv", ["css:#sp-cap"])
        await pg.evaluate("SP_ADV['sp-cap']=false; SP_SIG['sp-cap']=''; spRender('sp-cap')")
    await frag(pg, "f-cap-show", ["Что показывать"])
    await frag(pg, "f-voice", ["css:#vc-card"])

    # ---------- Модерация ----------
    await go(pg, "chat", "mod", wait=1800)
    await screen(pg, "s-mod")
    await frag(pg, "f-mod-filters", ["css:#p-chat [data-tab=mod] .mod-hero", "css:#p-chat [data-tab=mod] .mod-rules"], pad=8)
    await frag(pg, "f-mod-requests", ["Заявки на разбан", "Подозрительные аккаунты"])
    await frag(pg, "f-mod-log", ["Журнал срабатываний"])

    # ---------- Дополнительно ----------
    await go(pg, "pro", "wheel", wait=2200)
    await screen(pg, "s-wheel", [("css:#wl-sectors", "Сектора: вес, цвет, действие", .02, .05), ("css:.wl-prev", "Предпросмотр", .5, .08)])
    await frag(pg, "f-wheel-launch", ["Кто и как запускает"])
    await go(pg, "pro", "give")
    await frag(pg, "f-give", ["Розыгрыш"])
    await go(pg, "pro", "sstats", wait=1800)
    await screen(pg, "s-sstats")
    if want("f-stream-detail"):
        await pg.evaluate("document.querySelector('#ss-list tr') && document.querySelector('#ss-list tr').click()"); await pg.wait_for_timeout(1600)
        await frag(pg, "f-stream-detail", ["css:#ss-detail"])
    if want("f-summary"):
        await pg.evaluate("streamSummary(document.querySelector('#ss-list tr') ? 'demo' : 'demo')"); await pg.wait_for_timeout(1200)
        await frag(pg, "f-summary", ["css:.modal"], pad=0); await pg.evaluate("closeModal()"); await pg.wait_for_timeout(300)
    await go(pg, "pro", "prof")
    await screen(pg, "s-prof")
    await go(pg, "pro", "themes")
    if want("f-thbuilder"):
        await pg.evaluate("editTheme(null,'player')"); await pg.wait_for_timeout(2600)
        await frag(pg, "f-thbuilder", ["css:#th-editor"], pad=0)

    # ---------- Настройки ----------
    await go(pg, "settings", "s-ui")
    await frag(pg, "f-set-theme", ["Тема программы"])
    await frag(pg, "f-hotkeys", ["css:#hk-list"], pad=10)
    await go(pg, "settings", "s-about")
    await frag(pg, "f-about", ["css:#p-settings [data-tab=s-about] .card.about"])
    await frag(pg, "f-backups", ["Резервные копии", "Перенос на другой компьютер"])
    await frag(pg, "f-safe-top", ["css:.topbar"], pad=0)
    if want("f-cmdk"):
        await pg.evaluate("openCmdk(); const q=document.getElementById('ck-q'); q.value='колесо'; q.dispatchEvent(new Event('input'))"); await pg.wait_for_timeout(500)
        await frag(pg, "f-cmdk", ["css:#cmdk .ck"], pad=0); await pg.keyboard.press("Escape")
    if want("f-notifs"):
        await pg.evaluate("toast('Заказ от PixelMoth: «Copper Tide — Northbound» — в очереди'); toast('Рейд от BrambleBee: 34 зрителя'); document.querySelectorAll('.toast').forEach(t=>t.remove()); toggleNotifs(new Event('click'))")
        await pg.wait_for_timeout(500)
        await frag(pg, "f-notifs", ["css:#ncenter"], pad=0); await pg.evaluate("toggleNotifs(new Event('click'))")
    await go(pg, "alerts")
    await frag(pg, "f-safe-alerts", ["css:#p-alerts .card"], pad=8)

    # ---------- Мастер ----------
    for step, name in [(0, "f-wizard-1"), (3, "f-wizard-speech"), (4, "f-wizard-obs")]:
        if want(name):
            await pg.evaluate(f"openWizard({step})"); await pg.wait_for_timeout(1300)
            await frag(pg, name, ["css:#wz-body"], pad=0)
    await pg.evaluate("closeWizard(false)")

    # ---------- 8 тем: один и тот же «Эфир» ----------
    await go(pg, "live", wait=1600)
    for th in ["studio", "twitch", "light", "midnight", "neon", "graphite", "sunset", "forest"]:
        if want("t-" + th):
            await pg.evaluate(f"document.documentElement.dataset.theme='{th}'"); await pg.wait_for_timeout(600)
            await screen(pg, "t-" + th)
    await pg.evaluate("document.documentElement.dataset.theme='studio'")

    # ---------- оверлеи для OBS ----------
    scene({"now": {"title": "Velvet Harbor — Lanterns at Dusk", "duration": 214, "requester": "Nightjar_77", "cover": "/promo/cover.svg",
                   "pos0": 86, "t0": time.time(), "queue": [{"title": "Copper Tide — Northbound", "requester": "PixelMoth"},
                   {"title": "Mira Solenne — Paper Satellites", "requester": "kettle_crow"}, {"title": "The Quiet Engines — Glasshouse", "requester": "SableFinch"}]}})
    await overlay(browser, "/", "o-player", 620, 420)
    await overlay(browser, "/chat?demo", "o-chat", 460, 600, wait=9000)
    async def ev(p):
        scene({"events": [], "seq": 0}); await p.wait_for_timeout(1200)
        scene({"event_add": {"type": "raid", "title": "Входящий рейд", "user": "PixelMoth", "text": "Рейд: 34 зрителя"}})
    await overlay(browser, "/events", "o-events", 760, 260, prep=ev, wait=1800)
    await overlay(browser, "/votes?preview=1", "o-votes", 600, 420, wait=2500)
    caps = {"ok": True, "data": {"enabled": True, "lines": [{"id": 1, "text": "Так, сейчас попробуем пройти этот уровень с первого раза",
            "en": "Okay, now let's try to beat this level on the first try", "ts": time.time()}], "partial": "", "speaking": True, "now": time.time(),
            "cfg": {"show": "both", "position": "bottom", "font_size": 40, "max_lines": 2, "style": "studio"}}}
    await overlay(browser, "/captions", "o-captions", 1280, 300, routes={"**/api/captions*": caps})
    async def sp(p):
        sectors = [{"name": "Удача!", "weight": 2, "color": "#8b5cf6"}, {"name": "Трек от стримера", "weight": 3, "color": "#ec4899"},
                   {"name": "Таймаут 1 минута", "weight": 1, "color": "#ef4444"}, {"name": "Приветствие в чат", "weight": 3, "color": "#10b981"},
                   {"name": "Ничего", "weight": 4, "color": "#3b82f6"}, {"name": "Джекпот", "weight": 1, "color": "#f59e0b"}]
        scene({"spin": {"id": 7, "wheel": "Колесо фортуны", "user": "PixelMoth", "source": "reward", "test": False, "giveaway": False,
                        "sectors": sectors, "result": 5, "duration_ms": 5000, "delay": 0}})
    await overlay(browser, "/wheel", "o-wheel", 1000, 1000, prep=sp, wait=7200)
    scene({"spin": None})
    await overlay(browser, "/dock", "o-dock", 360, 640, wait=2500, transparent=False)


async def main():
    os.makedirs(OUT, exist_ok=True)
    meta_path = os.path.join(OUT, "shots.json")
    if ONLY and os.path.exists(meta_path):
        META.update(json.load(open(meta_path, encoding="utf-8")))
    srv = subprocess.Popen([sys.executable, os.path.join(HERE, "stub", "server.py"), str(PORT)])
    time.sleep(1.5)
    try:
        async with async_playwright() as p:
            b = await p.chromium.launch()
            pg = await b.new_page(viewport={"width": 1440, "height": 2200}, device_scale_factor=2)
            errs = []; pg.on("pageerror", lambda e: errs.append(str(e)))
            await patch_routes(pg)
            await pg.goto(BASE + "/panel"); await pg.wait_for_timeout(2500)
            await pg.evaluate("try{tourEnd()}catch(e){}; try{closeWizard(false)}catch(e){}; demoStart()"); await pg.wait_for_timeout(1500)
            await pg.add_script_tag(content=HELP)
            await run(b, pg)
            if errs: print("ошибки страницы:", errs)
            await b.close()
    finally:
        srv.kill()
    json.dump(META, open(meta_path, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print("снимков:", len(META))


asyncio.run(main())
