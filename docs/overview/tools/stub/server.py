"""Заглушка сервера Beg1nnnerOS для съёмки скриншотов документа-обзора.

Настоящий сервер программы — часть Windows-приложения (WPF + ASP.NET Core) и в Linux не запускается.
Эта заглушка отдаёт настоящие страницы программы из wwwroot/ и вымышленные данные:
  * панель — настройки по умолчанию из Core/Config.cs, остальное — снимки ответов (fixtures.json),
    сама панель снимается в демо-режиме «Показать на примере» (данные из wwwroot/demo.js);
  * оверлеи для OBS — данные сцены, которые задаёт сценарий записи через POST /promo/scene.
Никаких запросов наружу заглушка не делает.
"""
import base64, hashlib, json, os, re, socket, struct, sys, threading, time
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlsplit, parse_qs

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", ".."))
WWW = os.path.join(ROOT, "wwwroot")
HERE = os.path.dirname(os.path.abspath(__file__))
FX = json.load(open(os.path.join(HERE, "fixtures.json"), encoding="utf-8"))
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8095

cfg = open(os.path.join(ROOT, "Core", "Config.cs"), encoding="utf-8").read()
VERSION = re.search(r'Version = "([^"]+)"', cfg).group(1)
dj = cfg[cfg.index('DefaultsJson = """') + len('DefaultsJson = """'):]
DEFAULTS = json.loads(dj[:dj.index('""";')])


def settings():
    s = json.loads(json.dumps(DEFAULTS))
    s["ui"].update(theme="studio", wizard_done=True, stream_safe=True, language="ru", cover_tint=True)
    s["twitch"]["sender"] = "bot"
    s["captions"].update(enabled=True)
    s["captions"]["voice"]["enabled"] = True
    s["events"].setdefault("templates", {}).setdefault("reward", {})["screen"] = True
    s["overlay"]["theme"] = "studio"
    s["overlay"].update(show_cover=True, width=560, height=400, font_size=26, queue_size=3)
    s["chat_overlay"]["theme"] = "studio"
    s["chat_overlay"]["emotes"] = {"7tv": False, "bttv": False, "ffz": False}
    s["chat_overlay"]["hide_app"] = False
    s["chat_overlay"]["font_size"] = 22
    s["wheel"]["spin_sec"] = 5
    s["wheel"]["show_title"] = False   # «Название колеса и ник над ним» выключено: ник уже в событии награды
    return s


# ---------- сцена для оверлеев (задаёт сценарий записи) ----------
SCENE = {"now": None, "events": [], "chat": [], "spin": None, "seq": 0}
WS = set()


def now_payload():
    n = SCENE["now"]
    o = settings()["overlay"]
    cur = None
    if n:
        pos = n["pos0"] if n.get("paused") else n["pos0"] + (time.time() - n["t0"])
        cur = {"id": "promo", "vid": "", "title": n["title"], "page_url": "", "duration": n["duration"], "requester": n["requester"],
               "login": n["requester"].lower(), "priority": 0, "source": "youtube", "views": -1, "paid": True,
               "position": round(min(pos, n["duration"]), 2), "paused": bool(n.get("paused")), "cover": n.get("cover", "")}
    return {"current": cur, "queue": n.get("queue", []) if n else [], "toast": {"id": 0, "text": ""}, "overlay": o, "custom_themes": [],
            "volume": 70, "votes": 0, "votes_needed": 3}


def wheel_payload():
    sp = SCENE["spin"]
    w = settings()["wheel"]
    if sp:
        sp = dict(sp)
        sp["elapsed_ms"] = int((time.time() - sp.pop("t0")) * 1000)
    return {"spin": sp, "sound": False, "volume": w["volume"], "theme": w["theme"], "title": w["show_title"], "custom_themes": []}



PAGES = {"/panel": "panel.html", "/": "overlay.html", "/chat": "chat.html", "/events": "events.html", "/wheel": "wheel.html",
         "/captions": "captions.html", "/music-obs": "music-obs.html", "/dock": "dock.html", "/votes": "votes.html"}
TYPES = {".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8", ".json": "application/json",
         ".png": "image/png", ".woff2": "font/woff2", ".svg": "image/svg+xml", ".mp3": "audio/mpeg", ".ico": "image/x-icon"}
LOCK = threading.Lock()


def ws_frame(obj):
    data = json.dumps(obj, ensure_ascii=False).encode()
    n = len(data)
    hdr = bytes([0x81, n]) if n < 126 else bytes([0x81, 126]) + struct.pack(">H", n) if n < 65536 else bytes([0x81, 127]) + struct.pack(">Q", n)
    return hdr + data


def ws_send_all(obj):
    for sock in list(WS):
        try:
            sock.sendall(ws_frame(obj))
        except OSError:
            WS.discard(sock)


def api(method, p, q, body):
    if p == "/api/scene" and method == "POST":
        with LOCK:
            for k, v in body.items():
                if k == "chat_add":
                    SCENE["seq"] += 1
                    m = dict(v, id="c%d" % SCENE["seq"])
                    SCENE["chat"].append(m)
                    ws_send_all(dict(m, type="msg"))
                elif k == "event_add":
                    SCENE["seq"] += 1
                    SCENE["events"].append(dict(v, id=SCENE["seq"], ts=time.time()))
                elif k == "spin":
                    SCENE["spin"] = dict(v, t0=time.time() + v.get("delay", 0)) if v else None
                else:
                    SCENE[k] = v
        return {"ok": True}
    ok = lambda d: {"ok": True, "data": d}
    if p == "/api/settings" and method == "GET":
        ex = dict(FX["/api/settings#extras"])
        hk = open(os.path.join(ROOT, "Desktop", "Hotkeys.cs"), encoding="utf-8").read()   # действия горячих клавиш — из кода
        acts = re.findall(r'\["(\w+)"\] = \("([^"]+)", "([^"]*)"\)', hk)
        ex["hotkey_actions"] = {k: t for k, t, _ in acts} if isinstance(ex.get("hotkey_actions"), dict) else [[k, t] for k, t, _ in acts]
        data = settings()
        for k, _, d in acts: data["hotkeys"].setdefault(k, d)
        return dict({"ok": True, "data": data}, **ex)
    if p == "/api/state":
        d = json.loads(json.dumps(FX["/api/state"]["data"]))
        d.update(version=VERSION, stream_safe=True, ui_theme="studio", support_url=re.search(r'SupportUrl = "([^"]+)"', cfg).group(1))
        return ok(d)
    if p == "/api/commands":   # стандартные команды — прямо из Features/Commands.cs, свои — из снимка
        d = json.loads(json.dumps(FX["/api/commands"]))
        dd = d.get("data", d)
        src = open(os.path.join(ROOT, "Features", "Commands.cs"), encoding="utf-8").read()
        std = {}
        for key, name, al, desc, cd in re.findall(r'\("(\w+)", "(![^"]+)", (?:new\[\] \{ ([^}]*) \}|Array\.Empty<string>\(\)), "([^"]*)", (\d+)\)', src):
            std[key] = {"name": name, "aliases": re.findall(r'"([^"]+)"', al or ""), "desc": desc, "cooldown": int(cd),
                        "access": "mod" if key == "wheel" else "everyone", "template": "{result}", "random_max": 100, "enabled": True, "deleted": False}
        dd["standard"] = list(std.items()) if isinstance(dd.get("standard"), list) else std
        return d
    if p == "/api/now":
        return ok(now_payload())
    if p == "/api/wheel/overlay":
        return ok(wheel_payload())
    if p == "/api/events/feed":
        after = int(q.get("after", ["-1"])[0])
        items = [] if after < 0 else [e for e in SCENE["events"] if e["id"] > after]
        return ok({"seq": SCENE["seq"], "items": items, "duration": 5, "position": "top", "size": 100, "accent": "#8b5cf6", "sound": False})
    if p == "/api/chat/overlay":
        return ok({"settings": settings()["chat_overlay"], "broadcaster_id": "", "channel": "", "custom_themes": []})
    if p == "/api/captions":
        return ok({"enabled": False, "lines": [], "partial": "", "speaking": False, "now": time.time(), "cfg": {}})
    full = p + ("?" + "&".join(f"{k}={v[0]}" for k, v in q.items()) if q else "")
    if method == "GET" and (full in FX or p in FX):
        return FX.get(full) or FX[p]
    if method == "GET":
        return ok({"items": []})
    return {"ok": True, "data": {}}


class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def send(self, code, data, ctype):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def file(self, path):
        try:
            data = open(path, "rb").read()
        except OSError:
            return self.send(404, b"not found", "text/plain")
        self.send(200, data, TYPES.get(os.path.splitext(path)[1], "application/octet-stream"))

    def handle_ws(self, kind="chat"):
        key = self.headers.get("Sec-WebSocket-Key", "")
        acc = base64.b64encode(hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()).decode()
        self.send_response(101)
        self.send_header("Upgrade", "websocket")
        self.send_header("Connection", "Upgrade")
        self.send_header("Sec-WebSocket-Accept", acc)
        self.end_headers()
        sock = self.connection
        if kind == "state":   # как в программе: состояние приходит само, пока панель открыта
            try:
                while True:
                    sock.sendall(ws_frame(api("GET", "/api/state", {}, {})))
                    time.sleep(0.5)
            except OSError:
                pass
            self.close_connection = True
            return
        sock.sendall(ws_frame({"type": "history", "items": SCENE["chat"]}))
        WS.add(sock)
        try:
            while sock.recv(4096):
                pass
        except OSError:
            pass
        WS.discard(sock)
        self.close_connection = True

    def route(self, method):
        u = urlsplit(self.path)
        p, q = u.path, parse_qs(u.query)
        if p in ("/ws/chat", "/ws/state"):
            return self.handle_ws(p[4:])
        if p in PAGES:
            return self.file(os.path.join(WWW, PAGES[p]))
        if p.startswith("/static/"):
            return self.file(os.path.join(WWW, p[8:]))
        if p.startswith("/promo/"):
            return self.file(os.path.join(HERE, "pages", p[7:]))
        n = int(self.headers.get("Content-Length") or 0)
        body = json.loads(self.rfile.read(n) or b"{}") if n else {}
        self.send(200, json.dumps(api(method, p, q, body), ensure_ascii=False).encode(), "application/json; charset=utf-8")

    def do_GET(self):
        self.route("GET")

    def do_POST(self):
        self.route("POST")

    def do_DELETE(self):
        self.route("DELETE")


ThreadingHTTPServer.daemon_threads = True
ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
