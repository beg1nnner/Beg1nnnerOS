// Язык интерфейса Beg1nnnerOS. Страницы написаны по-русски; при английском языке тексты
// переводятся на лету по словарю /static/i18n/en.json (тот же словарь, что и в программе).
// Порядок: точное совпадение → шаблон с {подстановками} → замена известных фрагментов внутри строки.
(function () {
  const CYR = /[А-Яа-яЁё]/;
  let lang = "ru";
  try {
    const q = new URLSearchParams(location.search).get("lang");
    if (q) lang = q;
    else {
      const x = new XMLHttpRequest();
      x.open("GET", "/api/lang", false);   // синхронно: перевод нужен до первой отрисовки
      x.send();
      if (x.status === 200) lang = (JSON.parse(x.responseText).data || {}).lang || "ru";
    }
  } catch (e) {}
  window.I18N_LANG = lang;
  window.tr = s => s;
  if (lang !== "en") return;
  document.documentElement.lang = "en";

  let exact = {}, patterns = [], fragRe = null, frag = {};
  try {
    const x = new XMLHttpRequest();
    x.open("GET", "/static/i18n/en.json", false);
    x.send();
    if (x.status === 200) exact = JSON.parse(x.responseText);
  } catch (e) {}
  const PH = /\{([A-Za-z_][A-Za-z0-9_]*|\d+)\}/g;
  const esc = s => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const keys = [];
  for (const [ru, en] of Object.entries(exact)) {
    if (!ru || !en) continue;
    if (PH.test(ru)) {
      PH.lastIndex = 0;
      const names = []; let src = "^", last = 0, lit = "", m;
      while ((m = PH.exec(ru))) {
        const part = ru.slice(last, m.index); if (part.length > lit.length) lit = part;
        src += esc(part) + "(.+?)"; names.push(m[1]); last = m.index + m[0].length;
      }
      const tail = ru.slice(last); if (tail.length > lit.length) lit = tail;
      if (lit.trim().length >= 3) patterns.push({ re: new RegExp(src + esc(tail) + "$", "s"), en, names, lit });
    } else if (ru.length >= 1) { frag[ru] = en; keys.push(ru); }
    PH.lastIndex = 0;
  }
  keys.sort((a, b) => b.length - a.length);
  try {
    const L = /[А-Яа-яЁёA-Za-z0-9]/;
    // граница слова — только там, где фрагмент начинается или кончается буквой («» добавлен…» после названия трека тоже найдётся)
    fragRe = new RegExp(keys.map(k => (L.test(k[0]) ? "(?<![А-Яа-яЁёA-Za-z0-9])" : "") + esc(k) + (L.test(k[k.length - 1]) ? "(?![А-Яа-яЁё])" : "")).join("|"), "g");
  } catch (e) { fragRe = null; }

  function translate(s, depth) {
    if (!s || !CYR.test(s)) return s;
    const core = s.trim();
    if (!core) return s;
    const i = s.indexOf(core), pre = s.slice(0, i), post = s.slice(i + core.length);
    if (Object.prototype.hasOwnProperty.call(exact, core) && exact[core]) return pre + exact[core] + post;
    for (const p of patterns) {
      if (!core.includes(p.lit)) continue;
      const m = core.match(p.re);
      if (!m) continue;
      let out = p.en;
      p.names.forEach((n, k) => {
        let v = m[k + 1];
        if (depth < 2 && CYR.test(v)) v = exact[v.trim()] || translate(v, depth + 1);
        out = out.split("{" + n + "}").join(v);
      });
      return pre + out + post;
    }
    if (depth > 0 || !fragRe) return s;
    // короткие слова («с», «от», «и»…) переводим, только если после замены не осталось русских слов —
    // иначе получится каша из языков в пользовательском тексте
    let out = s.replace(fragRe, m => (m.length > 3 || !CYR.test(m)) ? (frag[m] || m) : m);
    if (CYR.test(out)) {
      const rest = out.replace(fragRe, m => frag[m] || m);
      if (!CYR.test(rest)) out = rest;
    } else out = out;
    return out;
  }
  const tr = s => { try { return translate(s, 0); } catch (e) { return s; } };
  window.tr = tr;

  // ---------- DOM ----------
  const SKIP = new Set(["SCRIPT", "STYLE", "TEXTAREA", "CODE"]);
  const ATTRS = ["placeholder", "title", "aria-label", "data-tip"];
  function skip(el) {
    for (let e = el; e && e.nodeType === 1; e = e.parentElement) {
      if (SKIP.has(e.tagName) || e.hasAttribute("data-noi18n") || e.isContentEditable) return true;
    }
    return false;
  }
  function doText(n) {
    const v = n.nodeValue;
    if (!v || !CYR.test(v) || skip(n.parentElement)) return;
    const t = tr(v);
    if (t !== v) n.nodeValue = t;
  }
  function doEl(el) {
    if (skip(el)) return;
    for (const a of ATTRS) {
      const v = el.getAttribute && el.getAttribute(a);
      if (v && CYR.test(v)) { const t = tr(v); if (t !== v) el.setAttribute(a, t); }
    }
    if (el.tagName === "INPUT" && (el.type === "button" || el.type === "submit") && CYR.test(el.value)) el.value = tr(el.value);
  }
  function walk(root) {
    if (!root) return;
    if (root.nodeType === 3) { doText(root); return; }
    if (root.nodeType !== 1 && root.nodeType !== 9 && root.nodeType !== 11) return;
    if (root.nodeType === 1) { if (skip(root)) return; doEl(root); }
    const w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT);
    let n;
    while ((n = w.nextNode())) {
      if (n.nodeType === 3) doText(n); else doEl(n);
    }
  }
  /** Поля настроек: переводим только значения по умолчанию (точное совпадение со словарём). */
  window.i18nInputs = function (root) {
    (root || document).querySelectorAll("input:not([type]),input[type=text],textarea").forEach(el => {
      const v = el.value;
      if (v && CYR.test(v) && exact[v.trim()]) el.value = exact[v.trim()];
      else if (v && CYR.test(v) && el.tagName === "TEXTAREA" && v.includes("\n")) {
        // список строк (например, тексты по умолчанию): переводим строки, которые есть в словаре целиком
        const lines = v.split("\n"); let ch = false;
        const outl = lines.map(l => { const x = exact[l.trim()]; if (x) { ch = true; return x; } return l; });
        if (ch) el.value = outl.join("\n");
      }
    });
  };
  const obs = new MutationObserver(list => {
    for (const m of list) {
      if (m.type === "characterData") doText(m.target);
      else if (m.type === "attributes") doEl(m.target);
      else m.addedNodes.forEach(walk);
    }
  });
  function start() {
    if (document.title && CYR.test(document.title)) document.title = tr(document.title);
    walk(document.body);
    obs.observe(document.documentElement, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ATTRS });
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
  // диалоги браузера
  const _c = window.confirm, _a = window.alert, _p = window.prompt;
  window.confirm = m => _c.call(window, tr(String(m ?? "")));
  window.alert = m => _a.call(window, tr(String(m ?? "")));
  window.prompt = (m, d) => _p.call(window, tr(String(m ?? "")), d);
  // даты и числа — по-английски
  const _tl = Date.prototype.toLocaleTimeString, _dl = Date.prototype.toLocaleDateString, _nl = Number.prototype.toLocaleString;
  Date.prototype.toLocaleTimeString = function (l, o) { return _tl.call(this, l === "ru-RU" ? "en-GB" : l, o); };
  Date.prototype.toLocaleDateString = function (l, o) { return _dl.call(this, l === "ru-RU" ? "en-GB" : l, o); };
  Number.prototype.toLocaleString = function (l, o) { return _nl.call(this, l === "ru-RU" ? "en-US" : l, o); };
})();
