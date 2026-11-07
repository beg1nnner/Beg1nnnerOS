// Общая отрисовка МедиаАлерта: используется источником /alerts и предпросмотром в панели.
window.renderAlert = function (stage, a, opts) {
  opts = opts || {};
  const W = opts.width || stage.clientWidth || window.innerWidth;
  const H = opts.height || stage.clientHeight || window.innerHeight;
  const vol = Math.max(0, Math.min(1, (a.volume / 100) * ((opts.master ?? 100) / 100)));
  const box = document.createElement("div");
  box.className = "ah-alert";
  box.style.cssText = "position:absolute;display:flex;flex-direction:column;align-items:center;gap:12px;" +
    "opacity:0;transition:opacity .35s ease, transform .35s ease;transform:scale(.92);pointer-events:none;";
  const media = [];
  let mediaEl = null;
  if (a.media_url) {
    if (a.media_kind === "video") {
      mediaEl = document.createElement("video");
      mediaEl.src = a.media_url; mediaEl.autoplay = true; mediaEl.playsInline = true;
      mediaEl.muted = !!opts.muted; mediaEl.volume = vol;
      media.push(mediaEl);
    } else {
      mediaEl = document.createElement("img");
      mediaEl.src = a.media_url;
    }
    mediaEl.style.display = "block";
    box.appendChild(mediaEl);
  }
  if (a.text) {
    const t = document.createElement("div");
    t.textContent = a.text;
    t.style.cssText = "font:800 " + Math.round(44 * a.scale) + "px 'Segoe UI',system-ui,sans-serif;color:#fff;" +
      "text-align:center;max-width:" + (W - 40) + "px;text-shadow:0 3px 0 #000,0 0 18px rgba(0,0,0,.8);" +
      "-webkit-text-stroke:1px rgba(0,0,0,.6);";
    box.appendChild(t);
  }
  let sound = null;
  if (a.sound_url) {
    sound = new Audio(a.sound_url);
    sound.volume = vol; sound.muted = !!opts.muted;
    media.push(sound);
  }
  stage.appendChild(box);

  function place() {
    if (mediaEl) {
      const nw = mediaEl.videoWidth || mediaEl.naturalWidth || 400;
      const nh = mediaEl.videoHeight || mediaEl.naturalHeight || 300;
      let w = nw * a.scale, h = nh * a.scale;
      const k = Math.min(1, (W - 10) / w, (H - 10) / h);   // не выходить за границы источника
      mediaEl.style.width = (w * k) + "px"; mediaEl.style.height = (h * k) + "px";
    }
    const bw = box.offsetWidth, bh = box.offsetHeight;
    let x, y;
    if (a.random_pos) {
      x = Math.random() * Math.max(0, W - bw); y = Math.random() * Math.max(0, H - bh);
    } else {
      x = Math.min(Math.max(0, a.x), Math.max(0, W - bw)); y = Math.min(Math.max(0, a.y), Math.max(0, H - bh));
    }
    box.style.left = x + "px"; box.style.top = y + "px";
    requestAnimationFrame(() => { box.style.opacity = 1; box.style.transform = "none"; });
    media.forEach(m => { const p = m.play(); if (p && p.catch) p.catch(() => {}); });
  }
  if (mediaEl && mediaEl.tagName === "VIDEO") mediaEl.addEventListener("loadedmetadata", place, {once: true});
  else if (mediaEl && !mediaEl.complete) { mediaEl.onload = place; mediaEl.onerror = place; }
  else setTimeout(place, 0);

  return {
    setVolume(master, alertVol) {
      const v = Math.max(0, Math.min(1, ((alertVol ?? a.volume) / 100) * (master / 100)));
      media.forEach(m => m.volume = v);
    },
    remove() {
      box.style.opacity = 0;
      media.forEach(m => { try { m.pause(); } catch (e) {} });
      setTimeout(() => box.remove(), 400);
    },
  };
};
