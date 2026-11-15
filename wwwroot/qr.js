// QR-код без сторонних библиотек: байтовый режим, уровень коррекции M, версии 1–6 (до 106 байт).
// Нужен для «Пульта с телефона»: панель рисует ссылку, телефон сканирует камерой.
// qrSvg(text, px) → строка <svg>; QR.matrix(text) → массив строк из true/false.
(function () {
  // уровень M: [байт данных в блоке по группам, байт коррекции на блок]
  const EC = {1: [[16], 10], 2: [[28], 16], 3: [[44], 26], 4: [[32, 32], 18], 5: [[43, 43], 24], 6: [[27, 27, 27, 27], 16]};
  const ALIGN = {1: [], 2: [6, 18], 3: [6, 22], 4: [6, 26], 5: [6, 30], 6: [6, 34]};
  const REMAINDER = {1: 0, 2: 7, 3: 7, 4: 7, 5: 7, 6: 7};

  // поле Галуа GF(256), образующий многочлен 0x11D
  const EXP = new Array(512), LOG = new Array(256);
  for (let i = 0, x = 1; i < 255; i++) { EXP[i] = x; LOG[x] = i; x <<= 1; if (x & 0x100) x ^= 0x11D; }
  for (let i = 255; i < 512; i++) EXP[i] = EXP[i - 255];
  const mul = (a, b) => (a && b) ? EXP[LOG[a] + LOG[b]] : 0;

  function rsGenerator(deg) {
    let g = [1];
    for (let i = 0; i < deg; i++) {
      const n = new Array(g.length + 1).fill(0);
      for (let j = 0; j < g.length; j++) { n[j] ^= g[j]; n[j + 1] ^= mul(g[j], EXP[i]); }
      g = n;
    }
    return g;   // старший коэффициент первым, g[0] = 1
  }
  function rsRemainder(data, deg) {
    const g = rsGenerator(deg), res = data.concat(new Array(deg).fill(0));
    for (let i = 0; i < data.length; i++) {
      const c = res[i];
      if (c) for (let j = 1; j < g.length; j++) res[i + j] ^= mul(g[j], c);
    }
    return res.slice(data.length);
  }

  function utf8(text) { return Array.from(new TextEncoder().encode(text)); }

  function codewords(bytes) {
    let ver = 0;
    for (let v = 1; v <= 6; v++) {
      const cap = EC[v][0].reduce((a, b) => a + b, 0);
      if (4 + 8 + bytes.length * 8 <= cap * 8) { ver = v; break; }
    }
    if (!ver) throw new Error("QR: слишком длинный текст");
    const [groups, ecLen] = EC[ver], cap = groups.reduce((a, b) => a + b, 0);
    // поток битов: режим «байты» 0100, длина (8 бит), данные, терминатор, выравнивание, заполнитель EC 11
    const bits = [];
    const put = (val, n) => { for (let i = n - 1; i >= 0; i--) bits.push((val >>> i) & 1); };
    put(4, 4); put(bytes.length, 8); bytes.forEach(b => put(b, 8));
    put(0, Math.min(4, cap * 8 - bits.length));
    while (bits.length % 8) bits.push(0);
    const data = [];
    for (let i = 0; i < bits.length; i += 8) data.push(bits.slice(i, i + 8).reduce((a, b) => (a << 1) | b, 0));
    for (let pad = 0xEC; data.length < cap; pad ^= 0xEC ^ 0x11) data.push(pad);
    // блоки, коррекция и чередование
    const blocks = [], ecs = [];
    let off = 0;
    for (const n of groups) { const b = data.slice(off, off + n); off += n; blocks.push(b); ecs.push(rsRemainder(b, ecLen)); }
    const out = [];
    const maxLen = Math.max(...groups);
    for (let i = 0; i < maxLen; i++) blocks.forEach(b => { if (i < b.length) out.push(b[i]); });
    for (let i = 0; i < ecLen; i++) ecs.forEach(e => out.push(e[i]));
    return {ver, out};
  }

  function build(text) {
    const {ver, out} = codewords(utf8(text));
    const size = 17 + ver * 4;
    const m = Array.from({length: size}, () => new Array(size).fill(false));
    const fn = Array.from({length: size}, () => new Array(size).fill(false));
    const set = (x, y, dark) => { m[y][x] = dark; fn[y][x] = true; };
    // поисковые узоры с разделителями
    const finder = (cx, cy) => {
      for (let dy = -4; dy <= 4; dy++) for (let dx = -4; dx <= 4; dx++) {
        const x = cx + dx, y = cy + dy;
        if (x < 0 || y < 0 || x >= size || y >= size) continue;
        const d = Math.max(Math.abs(dx), Math.abs(dy));
        set(x, y, d !== 2 && d !== 4);
      }
    };
    finder(3, 3); finder(size - 4, 3); finder(3, size - 4);
    // линии синхронизации
    for (let i = 0; i < size; i++) { if (!fn[6][i]) set(i, 6, i % 2 === 0); if (!fn[i][6]) set(6, i, i % 2 === 0); }
    // выравнивающие узоры
    const al = ALIGN[ver];
    for (const ay of al) for (const ax of al) {
      if ((ax === 6 && ay === 6) || (ax === 6 && ay === al[al.length - 1]) || (ax === al[al.length - 1] && ay === 6)) continue;
      for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) set(ax + dx, ay + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
    }
    // место под формат (заполним после выбора маски) и тёмный модуль
    const formatBits = mask => {
      const data = (0 << 3) | mask;   // уровень M = 00
      let rem = data;
      for (let i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >>> 9) * 0x537);
      return ((data << 10) | rem) ^ 0x5412;
    };
    const drawFormat = mask => {
      const bits = formatBits(mask), bit = i => ((bits >>> i) & 1) === 1;
      for (let i = 0; i <= 5; i++) set(8, i, bit(i));
      set(8, 7, bit(6)); set(8, 8, bit(7)); set(7, 8, bit(8));
      for (let i = 9; i < 15; i++) set(14 - i, 8, bit(i));
      for (let i = 0; i < 8; i++) set(size - 1 - i, 8, bit(i));
      for (let i = 8; i < 15; i++) set(8, size - 15 + i, bit(i));
      set(8, size - 8, true);
    };
    drawFormat(0);
    // данные змейкой снизу вверх по два столбца
    const total = out.length * 8 + REMAINDER[ver];
    let i = 0;
    for (let right = size - 1; right >= 1; right -= 2) {
      if (right === 6) right = 5;
      for (let vert = 0; vert < size; vert++) for (let j = 0; j < 2; j++) {
        const x = right - j, up = ((right + 1) & 2) === 0, y = up ? size - 1 - vert : vert;
        if (!fn[y][x] && i < total) {
          m[y][x] = i < out.length * 8 ? ((out[i >>> 3] >>> (7 - (i & 7))) & 1) === 1 : false;
          i++;
        }
      }
    }
    // маски: берём ту, что даёт меньше «штрафа» (читается надёжнее)
    const MASKS = [
      (x, y) => (x + y) % 2 === 0, (x, y) => y % 2 === 0, (x, y) => x % 3 === 0, (x, y) => (x + y) % 3 === 0,
      (x, y) => (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0, (x, y) => (x * y) % 2 + (x * y) % 3 === 0,
      (x, y) => ((x * y) % 2 + (x * y) % 3) % 2 === 0, (x, y) => ((x + y) % 2 + (x * y) % 3) % 2 === 0,
    ];
    const apply = k => { for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) if (!fn[y][x] && MASKS[k](x, y)) m[y][x] = !m[y][x]; };
    const penalty = () => {
      let p = 0, dark = 0;
      for (let a = 0; a < 2; a++) for (let r = 0; r < size; r++) {
        let run = 1;
        for (let c = 1; c <= size; c++) {
          const prev = a ? m[c - 1][r] : m[r][c - 1], cur = c < size ? (a ? m[c][r] : m[r][c]) : null;
          if (cur === prev) run++; else { if (run >= 5) p += run - 2; run = 1; }
        }
      }
      for (let y = 0; y < size - 1; y++) for (let x = 0; x < size - 1; x++) {
        const v = m[y][x];
        if (v === m[y][x + 1] && v === m[y + 1][x] && v === m[y + 1][x + 1]) p += 3;
      }
      for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) if (m[y][x]) dark++;
      p += Math.floor(Math.abs(dark * 20 - size * size * 10) / (size * size)) * 10;
      return p;
    };
    let best = 0, bestP = Infinity;
    for (let k = 0; k < 8; k++) {
      apply(k); drawFormat(k);
      const p = penalty();
      if (p < bestP) { bestP = p; best = k; }
      apply(k);   // маска обратима — снимаем
    }
    apply(best); drawFormat(best);
    return m;
  }

  function qrSvg(text, px) {
    const m = build(text), n = m.length, q = 4, all = n + q * 2;
    let d = "";
    for (let y = 0; y < n; y++) for (let x = 0; x < n; x++) if (m[y][x]) d += `M${x + q} ${y + q}h1v1h-1z`;
    return `<svg xmlns="http://www.w3.org/2000/svg" width="${px}" height="${px}" viewBox="0 0 ${all} ${all}" shape-rendering="crispEdges" role="img" aria-label="QR-код">`
      + `<rect width="${all}" height="${all}" fill="#fff"/><path d="${d}" fill="#000"/></svg>`;
  }
  window.QR = {matrix: build, svg: qrSvg};
  window.qrSvg = qrSvg;
})();
