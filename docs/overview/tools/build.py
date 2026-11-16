"""Сборка документа «Beg1nnnerOS — обзор возможностей»: content.md + screenshots/ → index.html + assets/ → PDF.

PDF печатает тот же Chromium, что снимал скриншоты (Playwright). Два прохода: в первом узнаём, на какой странице
начинается каждая глава, во втором вписываем номера в содержание. Обложка и последняя страница — без колонтитула.

Запуск: python3 docs/overview/tools/build.py
"""
import asyncio, html, io, json, os, re, shutil

from PIL import Image
from playwright.async_api import async_playwright
import pypdf, pikepdf

HERE = os.path.dirname(os.path.abspath(__file__))
DOC = os.path.abspath(os.path.join(HERE, ".."))
ROOT = os.path.abspath(os.path.join(DOC, "..", ".."))
SHOTS = os.path.join(DOC, "screenshots")
ASSETS = os.path.join(DOC, "assets")
TITLE = "Beg1nnnerOS — обзор возможностей"
PDF = os.path.join(DOC, "Beg1nnnerOS-overview.pdf")
VERSION = re.search(r'Version = "([^"]+)"', open(os.path.join(ROOT, "Core", "Config.cs"), encoding="utf-8").read()).group(1)
META = json.load(open(os.path.join(SHOTS, "shots.json"), encoding="utf-8"))

# ---------- значки: линейные SVG в стиле программы ----------
ICONS = {
    "gift": '<rect x="3" y="8" width="18" height="4" rx="1"/><path d="M12 8v13M19 12v9H5v-9M7.5 8a2.5 2.5 0 0 1 0-5C11 3 12 8 12 8s1-5 4.5-5a2.5 2.5 0 0 1 0 5"/>',
    "monitor": '<rect x="2" y="4" width="20" height="13" rx="2"/><path d="M8 21h8M12 17v4"/>',
    "globe": '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18"/>',
    "obs": '<circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="3"/><path d="M12 3v6M19.8 16.5l-5.2-3M4.2 16.5l5.2-3"/>',
    "download": '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3"/>',
    "send": '<path d="M21 4L3 11l6 2 2 6 3-4 5 4z"/><path d="M9 13l9-6"/>',
    "twitch": '<path d="M4 3h16v11l-5 5h-4l-3 3v-3H4z"/><path d="M11 8v4M16 8v4"/>',
    "heart": '<path d="M20.8 4.6a5.5 5.5 0 0 0-7.8 0L12 5.7l-1-1.1a5.5 5.5 0 0 0-7.8 7.8L12 21l8.8-8.6a5.5 5.5 0 0 0 0-7.8z"/>',
    "tv": '<rect x="3" y="5" width="18" height="13" rx="2"/><path d="M8 21h8"/>',
}
def icon(name, size=20):
    return (f'<svg class="ic" width="{size}" height="{size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" '
            f'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">{ICONS.get(name, ICONS["tv"])}</svg>')

THEMES = [("studio", "Студия"), ("twitch", "Twitch (классика)"), ("light", "Светлая"), ("midnight", "Полночь"),
          ("neon", "Неон"), ("graphite", "Графит"), ("sunset", "Закат"), ("forest", "Лес")]

# ---------- типографика ----------
SHORT = r"(?:в|во|на|с|со|к|ко|о|об|от|до|из|за|по|у|и|а|но|не|ни|для|без|при|про|над|под|как|что|или|же|ли|бы|это|их|его|её|все|всё)"
def typo(t):
    t = re.sub(rf"(?<![\w-])({SHORT}) ", "\\1\u00a0", t, flags=re.I)
    t = re.sub(r"(\d) (?=[A-Za-zА-Яа-яЁё%₽])", "\\1\u00a0", t)        # 20 секунд, 8 тем
    t = re.sub(r" (—|–) ", "\u00a0\\1 ", t)                               # тире не начинает строку
    return t

def inline(t):
    """Экранирование + ссылки [текст](адрес) + типографика."""
    out, pos = [], 0
    for m in re.finditer(r"\[([^\]]+)\]\((https?://[^)]+)\)", t):
        out.append(typo(html.escape(t[pos:m.start()], quote=False)))
        out.append(f'<a href="{html.escape(m.group(2))}">{typo(html.escape(m.group(1), quote=False))}</a>')
        pos = m.end()
    out.append(typo(html.escape(t[pos:], quote=False)))
    return "".join(out)

# ---------- картинки: сжатые копии для вёрстки и PDF ----------
def asset(name):
    m = META[name]
    src = os.path.join(SHOTS, name + ".png")
    os.makedirs(os.path.join(ASSETS, "img"), exist_ok=True)
    im = Image.open(src)
    limit = {"screen": 2000, "frag": 1500, "overlay": 1300}[m["kind"]]
    if im.width > limit:
        im = im.resize((limit, round(im.height * limit / im.width)), Image.LANCZOS)
    if m["kind"] == "overlay":
        out = f"img/{name}.png"
        im.save(os.path.join(ASSETS, out), optimize=True)
    else:
        out = f"img/{name}.jpg"
        im.convert("RGB").save(os.path.join(ASSETS, out), quality=84, optimize=True, progressive=True)
    return out

def figure(name, labels=None, caption=None, cls="", nocallouts=False):
    m = META[name]
    src = asset(name)
    ratio = m["w"] / m["h"]
    co = [] if nocallouts else m.get("callouts", [])
    if labels:
        co = [dict(c, label=labels[i]) for i, c in enumerate(co) if i < len(labels)]
    marks = "".join(f'<span class="co" style="left:{c["x"]}%;top:{c["y"]}%">{c["n"]}</span>' for c in co)
    legend = ("<ol class=\"legend\">" + "".join(f'<li><span class="n">{c["n"]}</span>{inline(c["label"])}</li>' for c in co) + "</ol>") if co else ""
    alt = html.escape(caption or (co[0]["label"] if co else name))
    if m["kind"] == "overlay":
        cap = caption or "Так это выглядит на стриме"
        return (f'<figure class="ov {cls}" style="--r:{ratio:.3f}"><div class="stage"><img src="assets/{src}" alt="{alt}"></div>'
                f'<figcaption>{icon("tv", 13)}{inline(cap)}</figcaption></figure>')
    kind = "screen" if m["kind"] == "screen" else "frag"
    capt = f"<figcaption>{inline(caption)}</figcaption>" if caption else ""
    return (f'<figure class="{kind} {cls}" style="--r:{ratio:.3f}"><div class="shot"><img src="assets/{src}" alt="{alt}">{marks}</div>{legend}{capt}</figure>')

# ---------- разбор content.md ----------
def parse():
    text = open(os.path.join(DOC, "content.md"), encoding="utf-8").read()
    text = re.sub(r"<!--.*?-->", "", text, flags=re.S)
    chapters, cur = [], None
    for line in text.split("\n"):
        if line.startswith("# "):
            cid, eyebrow, title = [x.strip() for x in line[2:].split("|")]
            cur = {"id": cid, "eyebrow": eyebrow, "title": title, "blocks": []}
            chapters.append(cur)
        elif cur is not None:
            cur["blocks"].append(line)
    return chapters

def blocks_to_items(lines):
    """Строки главы → элементы: para, shot (с подписями выносок), feat (подзаголовок), list, directive."""
    items, para = [], []
    def flush():
        if para: items.append(("p", " ".join(para))); para.clear()
    for ln in lines:
        s = ln.strip()
        if not s: flush(); continue
        if s.startswith("## "): flush(); items.append(("h", s[3:])); continue
        if s.startswith("@shot "):
            flush(); name, _, cap = s[6:].partition("|"); items.append(["shot", name.strip(), cap.strip() or None, []]); continue
        if s.startswith("@"): flush(); items.append(("dir", s[1:])); continue
        m = re.match(r"(\d+)\. (.+)", s)
        if m and items and items[-1][0] == "shot": items[-1][3].append(m.group(2)); continue
        if s.startswith("- "): flush(); items.append(("li", s[2:])); continue
        para.append(s)
    flush()
    return items

def render_features(items):
    """Подзаголовок + текст + картинки → блок функции. Узкие картинки — рядом с текстом, широкие — под ним."""
    out, feat = [], None
    def close():
        nonlocal feat
        if not feat: return
        h, paras, shots = feat
        keep = " keep" if shots and all(META[x[1]]["kind"] != "screen" for x in shots) else ""
        txt = f'<h2>{inline(h)}</h2>' + "".join(f"<p>{inline(p)}</p>" for p in paras)
        if not shots:
            out.append(f'<div class="feat text">{txt}</div>')
        else:
            narrow = [s for s in shots if META[s[1]]["w"] / META[s[1]]["h"] < 1.9 and META[s[1]]["kind"] != "screen"]
            figs = "".join(figure(n, labels or None, cap) for _, n, cap, labels in shots)
            if len(shots) == 1 and narrow:
                out.append(f'<div class="feat side"><div class="ft">{txt}</div><div class="fi">{figs}</div></div>')
            elif len(shots) == 2 and all(META[s[1]]["w"] / META[s[1]]["h"] < 2.4 for s in shots) and not any(s[3] for s in shots):
                out.append(f'<div class="feat{keep}"><div class="ft">{txt}</div><div class="pair">{figs}</div></div>')
            else:
                out.append(f'<div class="feat{keep}"><div class="ft">{txt}</div>{figs}</div>')
        feat = None
    for it in items:
        if it[0] == "h":
            close(); feat = [it[1], [], []]
        elif feat and it[0] == "p":
            feat[1].append(it[1])
        elif feat and it[0] == "shot":
            feat[2].append(it)
        elif it[0] == "shot":
            close(); out.append(figure(it[1], it[3] or None, it[2]))
        elif it[0] == "dir" and it[1] == "themes":
            if feat: out.append(f'<div class="feat text"><h2>{inline(feat[0])}</h2></div>'); feat = None
            cells = "".join(f'<figure class="th">{figure_theme(k)}<figcaption>{n}</figcaption></figure>' for k, n in THEMES)
            out.append(f'<p class="th-note">{typo("Один и тот же экран «Эфир» во всех темах. Тема меняется в «Настройки → Интерфейс и клавиши» и сразу применяется к панели и док-панели OBS.")}</p><div class="themes">{cells}</div>')
        elif it[0] == "p":
            close(); out.append(f"<p>{inline(it[1])}</p>")
    close()
    return "".join(out)

def figure_theme(key):
    src = asset("t-" + key)
    return f'<div class="shot"><img src="assets/{src}" alt="Тема «{key}»"></div>'

def chapter_html(ch, n_pages=None):
    cid = ch["id"]
    items = blocks_to_items(ch["blocks"])
    if cid == "cover":
        sub = next(i[1] for i in items if i[0] == "p")
        shot = next(i for i in items if i[0] == "shot")
        return f'''<section class="cover" id="cover">
  <div class="cv-top"><span class="mark">B1</span><span class="cv-ver">Версия {VERSION}</span></div>
  <h1 class="cv-title">Beg1nnnerOS</h1><p class="cv-sub">{inline(sub)}</p>
  <p class="cv-kicker">ОБЗОР ВОЗМОЖНОСТЕЙ</p>
  <div class="cv-shot">{figure(shot[1], labels=[], nocallouts=True)}</div>
</section>'''
    if cid == "back":
        sub = next(i[1] for i in items if i[0] == "p")
        rows = "".join(f'<li>{icon(a.split("|")[0].strip(), 22)}<div><span class="lb">{inline(a.split("|")[1].strip())}</span>{inline(a.split("|", 2)[2].strip())}</div></li>'
                       for t, a in items if t == "li")
        return f'''<section class="back" id="back"><span class="mark big">B1</span><h1>Beg1nnnerOS</h1><p class="cv-sub">{inline(sub)}</p>
  <ul class="contacts">{rows}</ul><p class="fine">Версия {VERSION}. Все данные на скриншотах вымышленные.</p></section>'''
    if cid == "toc":
        return "<section class=\"chapter toc\" id=\"toc\"><p class=\"eyebrow\">СОДЕРЖАНИЕ</p><h1>Содержание</h1><ol class=\"toc-list\">" + "".join(
            f'<li><a href="#{c["id"]}"><span class="te">{html.escape(c["eyebrow"].split("·")[0].strip()) if "·" in c["eyebrow"] else ""}</span>'
            f'<span class="tt">{inline(c["title"])}</span><span class="dots"></span><span class="tp">{(n_pages or {}).get(c["id"], "")}</span></a></li>'
            for c in CHAPTERS if c["id"] not in ("cover", "toc", "back")) + "</ol></section>"
    lead = ""
    if items and items[0][0] == "p":
        lead = f'<p class="lead">{inline(items[0][1])}</p>'; items = items[1:]
    body = ""
    if cid == "about":
        paras = [i for i in items if i[0] == "p"]
        facts = [i[1] for i in items if i[0] == "li"]
        body = "".join(f"<p>{inline(p[1])}</p>" for p in paras) + '<div class="facts">' + "".join(
            f'<div class="fact">{icon(f.split("|")[0].strip(), 22)}<b>{inline(f.split("|")[1].strip())}</b><span>{inline(f.split("|")[2].strip())}</span></div>' for f in facts) + "</div>"
    elif cid == "start":
        steps, cur = [], None
        for it in items:
            if it[0] == "h": cur = [it[1], [], []]; steps.append(cur)
            elif cur and it[0] == "p": cur[1].append(it[1])
            elif cur and it[0] == "shot": cur[2].append(it[1])
        body = '<ol class="steps">' + "".join(
            f'<li><span class="sn">{i}</span><div><h2>{inline(h)}</h2>{"".join(f"<p>{inline(p)}</p>" for p in ps)}{"".join(figure(s) for s in ss)}</div></li>'
            for i, (h, ps, ss) in enumerate(steps, 1)) + "</ol>"
    else:
        body = render_features(items)
    eyebrow = html.escape(ch["eyebrow"].upper())
    return f'<section class="chapter" id="{cid}"><p class="eyebrow">{eyebrow}</p><h1>{inline(ch["title"])}</h1>{lead}{body}</section>'

CSS = open(os.path.join(HERE, "overview.css"), encoding="utf-8").read()

def build_html(n_pages=None):
    body = "\n".join(chapter_html(c, n_pages) for c in CHAPTERS)
    return f'''<!doctype html>
<html lang="ru"><head><meta charset="utf-8"><title>{TITLE}</title>
<meta name="author" content="Beg1nnner"><meta name="description" content="Обзор всех функций Beg1nnnerOS со скриншотами интерфейса">
<link rel="stylesheet" href="assets/overview.css"></head>
<body>
{body}
</body></html>'''

FOOTER = ('<div style="width:100%;font:500 7.5px \'Geist Mono\',monospace;letter-spacing:.08em;color:#9d9dae;padding:0 18mm;'
          'display:flex;justify-content:space-between;-webkit-print-color-adjust:exact"><span>BEG1NNNEROS — ОБЗОР ВОЗМОЖНОСТЕЙ</span>'
          '<span class="pageNumber"></span></div>')

async def render(page, path, footer=True):
    await page.goto("file://" + os.path.join(DOC, "index.html"))
    await page.evaluate("document.fonts.ready")
    # все картинки загружены и раскодированы — иначе разбивка на страницы плавает от запуска к запуску
    await page.wait_for_function("[...document.images].every(i => i.complete && i.naturalWidth > 0)", timeout=60000)
    await page.evaluate("Promise.all([...document.images].map(i => i.decode().catch(() => {})))")
    await page.wait_for_timeout(400)
    await page.pdf(path=path, format="A4", print_background=True, prefer_css_page_size=True, outline=True, tagged=True,
                   display_header_footer=footer, header_template="<div></div>", footer_template=FOOTER if footer else "<div></div>")

def chapter_pages(pdf_path):
    """На какой странице начинается каждая глава: ищем её заголовок в тексте страниц."""
    r = pypdf.PdfReader(pdf_path)
    texts = [(p.extract_text() or "").replace("\u00a0", " ") for p in r.pages]
    res = {}
    for c in CHAPTERS:
        if c["id"] in ("cover", "toc", "back"): continue
        key = re.sub(r"\s+", "", c["eyebrow"].upper())
        for i, t in enumerate(texts):
            if i >= 1 and key in re.sub(r"\s+", "", t.upper()):
                res[c["id"]] = i + 1; break
    return res, len(r.pages)

async def main():
    global CHAPTERS
    CHAPTERS = parse()
    if os.path.exists(ASSETS): shutil.rmtree(ASSETS)
    os.makedirs(os.path.join(ASSETS, "fonts"))
    for f in ("Geist.woff2", "GeistMono.woff2"):
        shutil.copy(os.path.join(ROOT, "wwwroot", "fonts", f), os.path.join(ASSETS, "fonts", f))
    open(os.path.join(ASSETS, "overview.css"), "w", encoding="utf-8").write(CSS)
    html1 = build_html()
    open(os.path.join(DOC, "index.html"), "w", encoding="utf-8").write(html1)
    tmp = os.path.join(HERE, ".tmp"); os.makedirs(tmp, exist_ok=True)
    async with async_playwright() as p:
        b = await p.chromium.launch(); page = await b.new_page()
        await render(page, f"{tmp}/pass1.pdf")
        pages, total = chapter_pages(f"{tmp}/pass1.pdf")
        open(os.path.join(DOC, "index.html"), "w", encoding="utf-8").write(build_html(pages))
        await render(page, f"{tmp}/body.pdf")
        await render(page, f"{tmp}/plain.pdf", footer=False)
        await b.close()
    check, total2 = chapter_pages(f"{tmp}/body.pdf")
    if check != pages:
        print("номера страниц сдвинулись:", pages, check)
    # свойства документа, язык; обложка и последняя страница — из прохода без колонтитула
    with pikepdf.open(f"{tmp}/body.pdf") as pdf, pikepdf.open(f"{tmp}/plain.pdf") as pl:
        for i in (0, len(pdf.pages) - 1):
            pdf.pages[i] = pl.pages[i]
        for pg in pdf.pages:
            x0, y0, x1, y1 = [float(v) for v in pg.MediaBox]
            pg.contents_add(pikepdf.Stream(pdf, f"q 0.0353 0.0353 0.0471 rg {x0} {y0} {x1 - x0} {y1 - y0} re f Q\n".encode()), prepend=True)
        pdf.Root.Lang = pikepdf.String("ru-RU")
        pdf.Root.ViewerPreferences = pikepdf.Dictionary(DisplayDocTitle=True)
        pdf.Root.PageMode = pikepdf.Name.UseOutlines
        with pdf.open_metadata() as meta:
            meta["dc:title"] = TITLE
            meta["dc:creator"] = ["Beg1nnner"]
            meta["dc:description"] = "Обзор функций программы Beg1nnnerOS для стримеров Twitch"
            meta["dc:language"] = ["ru-RU"]
            meta["pdf:Keywords"] = "Beg1nnnerOS, Twitch, OBS, стрим"
        pdf.docinfo["/Title"] = TITLE
        pdf.docinfo["/Author"] = "Beg1nnner"
        pdf.docinfo["/Subject"] = "Обзор функций программы Beg1nnnerOS для стримеров Twitch"
        pdf.docinfo["/Keywords"] = "Beg1nnnerOS, Twitch, OBS, стрим"
        pdf.save(PDF, compress_streams=True, object_stream_mode=pikepdf.ObjectStreamMode.generate)
    shutil.rmtree(tmp, ignore_errors=True)
    print(f"готово: {PDF} · страниц: {total2} · {os.path.getsize(PDF) / 1048576:.1f} МБ")
    print("главы:", pages)

CHAPTERS = []
asyncio.run(main())
