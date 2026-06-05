(async function () {
  const $ = (id) => document.getElementById(id);

  let cfg = await fetch("/api/config").then((r) => r.json());
  let liveWindowSec = cfg.liveWindowSeconds;
  let intervalSec = cfg.intervalSeconds || 1;
  let threshold = cfg.highLatencyMs;
  $("host").textContent = cfg.host;

  const MAX_WIDTH_SEC = 7 * 24 * 3600;

  // ---- state ----
  let mode = "live";            // "live" | "history"
  let widthSec = liveWindowSec; // current window width
  let endMs = Date.now();       // history window end (ms); ignored in live

  // live buffers (raw samples)
  let lxs = [], lys = [];       // unix seconds, rtt ms or null
  const liveCapSec = Math.max(liveWindowSec, 900);

  // displayed columnar series: x, avg, min, max, loss-fraction
  let dX = [], dAvg = [], dMin = [], dMax = [], dLoss = [];

  function trimLive() {
    const cutoff = Date.now() / 1000 - liveCapSec;
    let i = 0;
    while (i < lxs.length && lxs[i] < cutoff) i++;
    if (i > 0) { lxs = lxs.slice(i); lys = lys.slice(i); }
  }
  function addSample(s) {
    const x = s.ts / 1000;
    if (lxs.length && x <= lxs[lxs.length - 1]) return;
    lxs.push(x); lys.push(s.rttMs);
  }

  // ---- chart ----
  function size() {
    return { width: Math.max(320, window.innerWidth - 16), height: Math.max(240, window.innerHeight - 150) };
  }
  function stepSec() {
    if (dX.length >= 2) return dX[1] - dX[0];
    return mode === "live" ? intervalSec : Math.max(intervalSec, widthSec / 600);
  }
  function drawThreshold(u) {
    const y = Math.round(u.valToPos(threshold, "y", true));
    const c = u.ctx; c.save();
    c.strokeStyle = "rgba(255,167,38,0.7)"; c.lineWidth = 1; c.setLineDash([5, 4]);
    c.beginPath(); c.moveTo(u.bbox.left, y); c.lineTo(u.bbox.left + u.bbox.width, y); c.stroke();
    c.restore();
  }
  function drawLoss(u) {
    const x = u.data[0], loss = u.data[4];
    if (!x.length) return;
    const half = stepSec() * 0.5, c = u.ctx, top = u.bbox.top, h = u.bbox.height;
    c.save();
    for (let i = 0; i < loss.length; i++) {
      const f = loss[i];
      if (f && f > 0) {
        c.fillStyle = "rgba(239,83,80," + (0.18 + 0.5 * Math.min(1, f)) + ")";
        const cx = u.valToPos(x[i], "x", true);
        const w = Math.max(2, u.valToPos(x[i] + half, "x", true) - u.valToPos(x[i] - half, "x", true));
        c.fillRect(cx - w / 2, top, w, h);
      }
    }
    c.restore();
  }

  const clear = "rgba(0,0,0,0)";
  const fmtMs = (u, v) => (v == null ? "—" : v.toFixed(0) + " ms");
  const opts = {
    ...size(),
    cursor: { drag: { x: true, y: false, setScale: false } }, // drag selects a range to load
    scales: {
      x: { time: true },
      y: { range: (u, mn, mx) => { const top = Math.max(mx || 0, threshold) * 1.25; return [0, top > 0 ? top : 10]; } },
    },
    series: [
      {},
      { label: "avg", stroke: "#4fc3f7", width: 1.5, spanGaps: false, points: { show: false }, value: fmtMs },
      { label: "min", stroke: clear, points: { show: false }, value: fmtMs },
      { label: "max", stroke: clear, points: { show: false }, value: fmtMs },
      { label: "loss", stroke: clear, points: { show: false }, value: (u, v) => (v == null ? "—" : (v * 100).toFixed(1) + "%") },
    ],
    bands: [{ series: [3, 2], fill: "rgba(79,195,247,0.16)" }], // fill between max and min
    axes: [
      { stroke: "#6b7686", grid: { stroke: "#1b2230" }, ticks: { stroke: "#1b2230" } },
      { stroke: "#6b7686", grid: { stroke: "#1b2230" }, ticks: { stroke: "#1b2230" }, values: (u, t) => t.map((x) => x + " ms") },
    ],
    hooks: {
      draw: [drawThreshold, drawLoss],
      setSelect: [(u) => {
        if (u.select.width > 4) {
          const a = u.posToVal(u.select.left, "x");
          const b = u.posToVal(u.select.left + u.select.width, "x");
          enterHistory(b - a, b * 1000);
          u.setSelect({ left: 0, width: 0, top: 0, height: 0 }, false);
        }
      }],
    },
  };
  const chart = new uPlot(opts, [[], [], [], [], []], $("chart"));
  window.addEventListener("resize", () => chart.setSize(size()));

  function applyData() {
    chart.setData([dX, dAvg, dMin, dMax, dLoss]);
    if (mode === "live") {
      const now = Date.now() / 1000;
      chart.setScale("x", { min: now - widthSec, max: now });
    } else {
      const toS = endMs / 1000;
      chart.setScale("x", { min: toS - widthSec, max: toS });
    }
  }

  function buildLiveDisplay() {
    trimLive();
    dX = lxs; dAvg = lys; dMin = lys; dMax = lys;
    dLoss = lys.map((v) => (v == null ? 1 : 0));
  }

  function render() {
    if (mode === "live") buildLiveDisplay();
    applyData();
    updateStats();
    updateControls();
  }

  async function fetchSeries() {
    const toMs = endMs, fromMs = toMs - widthSec * 1000;
    const buckets = Math.max(60, Math.min(2000, Math.floor(size().width)));
    try {
      const r = await fetch(`/api/series?from=${fromMs}&to=${toMs}&buckets=${buckets}`).then((x) => x.json());
      dX = r.t.map((ms) => ms / 1000);
      dAvg = r.avg; dMin = r.min; dMax = r.max; dLoss = r.loss;
    } catch (_) {
      dX = []; dAvg = []; dMin = []; dMax = []; dLoss = [];
    }
    applyData(); updateStats(); updateControls();
  }

  async function enterLive() {
    mode = "live"; widthSec = liveWindowSec;
    try {
      const w = await fetch("/api/window?seconds=" + liveWindowSec).then((r) => r.json());
      lxs = []; lys = []; for (const s of w) addSample(s);
    } catch (_) {}
    render();
  }
  function enterHistory(width, end) {
    mode = "history";
    widthSec = Math.max(intervalSec * 4, Math.min(MAX_WIDTH_SEC, Math.round(width)));
    endMs = Math.min(Date.now(), Math.round(end));
    fetchSeries();
  }
  function seek(dir) {
    if (mode !== "history") return;
    endMs = Math.min(Date.now(), endMs + dir * widthSec * 1000 * 0.5);
    fetchSeries();
  }
  function zoomOut() {
    if (mode !== "history") { enterHistory(liveWindowSec * 2, Date.now()); return; }
    widthSec = Math.min(MAX_WIDTH_SEC, widthSec * 2);
    endMs = Math.min(Date.now(), endMs);
    fetchSeries();
  }

  function updateStats() {
    let mn = Infinity, mx = -Infinity, sum = 0, ok = 0, lossSum = 0, lossN = 0;
    for (let i = 0; i < dAvg.length; i++) {
      if (dAvg[i] != null) { ok++; sum += dAvg[i]; }
      if (dMin[i] != null && dMin[i] < mn) mn = dMin[i];
      if (dMax[i] != null && dMax[i] > mx) mx = dMax[i];
      if (dLoss[i] != null) { lossSum += dLoss[i]; lossN++; }
    }
    const cur = dAvg.length ? dAvg[dAvg.length - 1] : null;
    setStat("s-cur", cur == null ? (dAvg.length ? "loss" : "—") : Math.round(cur) + " ms", cur == null && dAvg.length > 0);
    $("s-min").textContent = ok ? Math.round(mn) + " ms" : "—";
    $("s-avg").textContent = ok ? Math.round(sum / ok) + " ms" : "—";
    $("s-max").textContent = ok ? Math.round(mx) + " ms" : "—";
    const lossPct = lossN ? (lossSum / lossN) * 100 : 0;
    const el = $("s-loss");
    el.textContent = lossPct.toFixed(lossPct > 0 && lossPct < 10 ? 1 : 0) + "%";
    el.className = "value " + (lossPct > 0 ? "loss" : "ok");
    $("s-win").textContent = fmtDuration(widthSec);
  }
  function setStat(id, text, isLoss) {
    const e = $(id); e.textContent = text; e.className = "value " + (isLoss ? "loss" : "ok");
  }

  function updateControls() {
    const conn = $("conn");
    if (mode === "live") { conn.textContent = "live"; conn.className = "conn live"; }
    else { conn.textContent = "history"; conn.className = "conn hist"; }
    document.querySelectorAll("#controls .ranges button").forEach((b) => {
      const isLive = b.dataset.live === "1";
      const active = (mode === "live" && isLive) || (mode === "history" && !isLive && Number(b.dataset.sec) === widthSec);
      b.classList.toggle("active", active);
    });
    $("older").disabled = mode !== "history";
    $("newer").disabled = mode !== "history" || endMs >= Date.now() - 1000;
    const lbl = $("range-label");
    if (mode === "history") {
      const to = new Date(endMs), from = new Date(endMs - widthSec * 1000);
      const sameDay = from.toDateString() === to.toDateString();
      lbl.textContent = (sameDay ? "" : from.toLocaleDateString() + " ") + from.toLocaleTimeString() +
        "  –  " + (sameDay ? "" : to.toLocaleDateString() + " ") + to.toLocaleTimeString();
    } else lbl.textContent = "";
  }

  // ---- controls ----
  document.querySelectorAll("#controls .ranges button").forEach((b) => {
    b.addEventListener("click", () => {
      if (b.dataset.live === "1") enterLive();
      else enterHistory(Number(b.dataset.sec), Date.now());
    });
  });
  $("older").addEventListener("click", () => seek(-1));
  $("newer").addEventListener("click", () => seek(+1));
  $("zoomout").addEventListener("click", zoomOut);

  // ---- initial fill (live) ----
  const initWin = await fetch("/api/window?seconds=" + liveWindowSec).then((r) => r.json());
  for (const s of initWin) addSample(s);
  render();

  // ---- live stream (SSE) ----
  let firstOpen = true;
  function connect() {
    const es = new EventSource("/api/live");
    es.onopen = async () => {
      if (mode === "live") { $("conn").textContent = "live"; $("conn").className = "conn live"; }
      if (!firstOpen) {
        try {
          const recent = await fetch("/api/window?seconds=" + liveWindowSec).then((r) => r.json());
          lxs = []; lys = []; for (const s of recent) addSample(s);
          if (mode === "live") render();
        } catch (_) {}
      }
      firstOpen = false;
    };
    es.onmessage = (e) => {
      let s; try { s = JSON.parse(e.data); } catch (_) { return; }
      addSample(s);
      if (mode === "live") render();
    };
    es.onerror = () => { if (mode === "live") { $("conn").textContent = "reconnecting…"; $("conn").className = "conn down"; } };
  }
  connect();

  // ---- settings panel ----
  function applyConfig(c) {
    cfg = c;
    liveWindowSec = c.liveWindowSeconds;
    intervalSec = c.intervalSeconds || 1;
    threshold = c.highLatencyMs;
    $("host").textContent = c.host;
  }
  function openSettings() {
    $("f-host").value = cfg.host;
    $("f-interval").value = cfg.intervalSeconds;
    $("f-timeout").value = cfg.timeoutMs;
    $("f-window").value = cfg.liveWindowSeconds;
    $("f-threshold").value = cfg.highLatencyMs;
    $("f-retention").value = cfg.retentionHours;
    const m = $("f-msg"); m.textContent = ""; m.className = "";
    $("settings").classList.remove("hidden");
  }
  function closeSettings() { $("settings").classList.add("hidden"); }
  async function saveSettings() {
    const body = {
      host: $("f-host").value.trim(),
      intervalSeconds: Number($("f-interval").value),
      timeoutMs: Number($("f-timeout").value),
      liveWindowSeconds: Number($("f-window").value),
      highLatencyMs: Number($("f-threshold").value),
      retentionHours: Number($("f-retention").value),
    };
    const msg = $("f-msg");
    try {
      const res = await fetch("/api/config", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      if (!res.ok) {
        const e = await res.json().catch(() => ({}));
        msg.textContent = e.error || "Error " + res.status; msg.className = "err";
        return;
      }
      applyConfig(await res.json());
      if (mode === "live") await enterLive(); else render();
      closeSettings();
    } catch (_) {
      msg.textContent = "Request failed"; msg.className = "err";
    }
  }
  $("settings-btn").addEventListener("click", openSettings);
  $("f-save").addEventListener("click", saveSettings);
  $("f-cancel").addEventListener("click", closeSettings);

  function fmtDuration(sec) {
    if (sec % 86400 === 0) return sec / 86400 + "d";
    if (sec % 3600 === 0) return sec / 3600 + "h";
    if (sec % 60 === 0) return sec / 60 + "m";
    return sec + "s";
  }
})();
