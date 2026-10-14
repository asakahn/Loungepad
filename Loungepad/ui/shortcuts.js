"use strict";

/*
 * Shortcuts: the Power Wheel's wheels of things to open (the host's ShortcutService).
 *
 * Three pieces, all fed by S.shortcuts (the host's `shortcuts` message):
 *
 *   1. The shortcut wheel. The Power Wheel's Shortcuts spoke opens it on the first tab; LB and RB
 *      turn between tabs, the stick or the D-pad picks a spoke, A opens it. A shortcut with a
 *      button can be pressed straight from the wheel too, as an action can.
 *
 *   2. Settings → Shortcuts. The editor IS the wheel: the tab's shortcuts round the ring exactly
 *      as the Power Wheel lays them out, a dashed + spoke where the next one will go, the tabs
 *      across the top and the highlighted shortcut's details beside it. A edits (the form sheet,
 *      over the wheel), X picks a shortcut up to slide it round the ring or carry it to another
 *      tab, Y is the options card, LB/RB turn the tab. A mouse drags, and the joystick mouse
 *      can rest on any control of the tab strip for A to press it.
 *
 *   3. The pickers: what to open (an app from the Start menu, a program, a game, a folder, a file,
 *      a website, a command, one of Windows' own windows or a power action) and an icon.
 *
 * shortcutWheelOpen and scUi are declared in app.js with the other overlay flags: app.js reads them
 * on its own paths, and a `let` in a later script is out of reach until that script has run.
 * Loaded after actions.js, whose capture dialog and binding checks the buttons use.
 */

const SC_KIND_LABELS = { app: "App", program: "Program", folder: "Folder", file: "File", url: "Website", game: "Game", command: "Command" };
const SC_KIND_ICONS = { app: "apps", program: "apps", folder: "folder", file: "file", url: "globe", game: "gamepad", command: "terminal" };

/* ---- the data ---- */

function scData() { return S.shortcuts || { tabs: [], catalog: [], knownFolders: [], maxItems: 8, maxTabs: 12 }; }
function scTabs() { return scData().tabs || []; }
function scMaxItems() { return scData().maxItems || 8; }
function scMaxTabs() { return scData().maxTabs || 12; }
function scAllItems() { return scTabs().flatMap(t => t.items || []); }
function scBuiltin(id) { return (scData().catalog || []).find(b => b.id === id) || null; }
function scFindItem(id) {
  for (const tab of scTabs()) {
    const index = (tab.items || []).findIndex(x => x.id === id);
    if (index >= 0) return { tab, item: tab.items[index], index };
  }
  return null;
}
function scNewId() { return "s" + Math.random().toString(16).slice(2, 12).padEnd(10, "0"); }

function scGame(it) { return it && it.kind === "game" ? gameById(it.target) : null; }
function scMissing(it) {
  if (it.kind === "game") { const g = scGame(it); return !g || !g.installed; }
  return !!it.missing;
}
function scDanger(it) { return it.kind === "builtin" && !!(scBuiltin(it.target) || {}).danger; }
function scNeedsConfirm(it) { return !!it.confirm || (it.kind === "builtin" && !!(scBuiltin(it.target) || {}).confirm); }
function scKindLabel(it) {
  if (it.kind === "builtin") return (scBuiltin(it.target) || {}).group === "power" ? "Power" : "Windows";
  return SC_KIND_LABELS[it.kind] || "Shortcut";
}

/* What a shortcut opens, in words: the line under its name. */
function scWhat(it) {
  switch (it.kind) {
    case "builtin": return (scBuiltin(it.target) || {}).desc || "";
    case "app": return it.exe ? it.exe : "From the Start menu";
    case "game": { const g = scGame(it); return g ? `${g.title} · ${g.platform}` : "No longer in the library"; }
    case "url": try { const u = new URL(it.target); return u.host + (u.pathname === "/" ? "" : u.pathname) + u.search; } catch { return it.target; }
    case "command": return it.target + (it.args ? " " + it.args : "");
    case "program": return it.target + (it.args ? " " + it.args : "");
    default: return it.target;
  }
}

function scUrlName(url) {
  try { return new URL(url).host.replace(/^www\./i, ""); } catch { return url; }
}

/* A value for background-image:url('…') that cannot close the quote or the call. */
function scCssUrl(u) { return String(u || "").replace(/["'()\\\s<>]/g, c => encodeURIComponent(c)); }

/* The picture on a spoke: a picture the user chose, a built-in's own glyph, a game's art, the icon
   the host found (an app's, a file's, a website's own), a website's first letter, or the kind's glyph. */
function scIconHtml(it) {
  if (!it) return iconSvg("plus");
  if (it.customIcon) return `<i class="sc-img" style="background-image:url('${scCssUrl(it.customIcon)}')"></i>`;
  if (it.kind === "builtin") return iconSvg((scBuiltin(it.target) || {}).icon || "apps");
  if (it.kind === "game") {
    const g = scGame(it);
    const url = g && bannerUrl(g);
    return url ? `<i class="sc-art" style="background-image:url('${scCssUrl(url)}')"></i>` : iconSvg("gamepad");
  }
  if (it.autoIcon) return `<i class="sc-img" style="background-image:url('${scCssUrl(it.autoIcon)}')"></i>`;
  if (it.kind === "url") return `<i class="sc-mono">${esc(scUrlName(it.target).charAt(0).toUpperCase() || "?")}</i>`;
  return iconSvg(SC_KIND_ICONS[it.kind] || "apps");
}

/* The angle of spoke `i` of `n`, clockwise from the top: the Power Wheel's own layout. */
function scSpokeXY(i, n, R, cx, cy) {
  const ang = (-90 + i * (360 / Math.max(n, 1))) * Math.PI / 180;
  return { x: cx + R * Math.cos(ang), y: cy + R * Math.sin(ang), dx: -R * 0.55 * Math.cos(ang), dy: -R * 0.55 * Math.sin(ang) };
}

function scSpokeInner(it, opts = {}) {
  if (!it) return `<div class="sc-glyph">${iconSvg("plus")}</div><span class="spoke-name">Add a shortcut</span>`;
  return `<div class="sc-glyph">${scIconHtml(it)}</div><span class="spoke-name">${esc(it.name)}</span>` +
    (it.button && opts.badge !== false ? spokeBtnHtml(it.button) : "");
}

/* ============================== the wheel ============================== */

let scWheelTab = 0, scWheelIdx = 0;

function openShortcutWheel() {
  shortcutWheelOpen = true;
  radialSub = null;
  // Always the first tab: the wheel is a place the hand learns, and opening on whichever tab was
  // last turned to would put a different spoke under the same push of the stick.
  scWheelTab = 0; scWheelIdx = 0;
  hideOverlay("overlay-radial");
  showOverlay("overlay-shortcuts");
  renderShortcutWheel(true);
}

/* Back to the Power Wheel, or away entirely (the host closes the overlay on a pick). */
function closeShortcutWheel(backToRadial) {
  shortcutWheelOpen = false;
  hideOverlay("overlay-shortcuts");
  if (backToRadial) { renderRadial(); showOverlay("overlay-radial"); }
}

function scWheelItems() { const t = scTabs()[scWheelTab]; return (t && t.items) || []; }

function renderShortcutWheel(rebuild) {
  const tabs = scTabs();
  scWheelTab = Math.max(0, Math.min(scWheelTab, tabs.length - 1));
  const tab = tabs[scWheelTab] || { id: "", name: "", items: [] };
  const items = tab.items || [];
  const n = items.length;
  scWheelIdx = n ? Math.max(0, Math.min(scWheelIdx, n - 1)) : 0;
  const ring = $("scRing");
  const R = 310, cx = 440, cy = 440;
  // Built once per tab and order. A spoke carries the wheel's opening bloom (see .radial-item), and
  // rebuilding them on every move would replay it each step.
  const key = tab.id + "|" + items.map(i => i.id).join(",");
  if (rebuild || ring.__key !== key) {
    ring.innerHTML = "";
    items.forEach((it, i) => {
      const p = scSpokeXY(i, n, R, cx, cy);
      const el = document.createElement("div");
      el.style.left = p.x + "px";
      el.style.top = p.y + "px";
      el.style.setProperty("--i", i);
      el.style.setProperty("--dx", p.dx.toFixed(1) + "px");
      el.style.setProperty("--dy", p.dy.toFixed(1) + "px");
      el.addEventListener("mouseenter", () => {
        if (hoverEnabled() && scWheelIdx !== i) { scWheelIdx = i; renderShortcutWheel(); }
      });
      el.addEventListener("click", () => { scWheelIdx = i; const it2 = scWheelItems()[i]; if (it2) scRunFromWheel(it2); });
      ring.appendChild(el);
    });
    ring.__key = key;
  }
  items.forEach((it, i) => {
    const el = ring.children[i];
    el.className = "radial-item sc-spoke"
      + (i === scWheelIdx && focusVisible() ? " focused" : "")
      + (scDanger(it) ? " danger" : "")
      + (scMissing(it) ? " missing" : "")
      + (it.button ? " has-btn" : "");
    const html = scSpokeInner(it);
    if (el.__html !== html) { el.innerHTML = html; el.__html = html; }
  });

  $("scTitle").textContent = "SHORTCUTS";
  const sel = items[scWheelIdx];
  if (sel) {
    $("scKind").innerHTML = `<span>${esc(scKindLabel(sel).toUpperCase())}</span>`;
    $("scSelName").textContent = sel.name;
    $("scDesc").textContent = scWhat(sel);
    $("scTarget").innerHTML = scMissing(sel) ? "NOT FOUND ON THIS PC"
      : sel.button ? `<span class="sc-target-btn">${comboHtml(sel.button)}</span>` : "";
  } else {
    $("scKind").innerHTML = tab.name ? `<span>${esc(tab.name.toUpperCase())}</span>` : "";
    $("scSelName").textContent = "Nothing here yet";
    $("scDesc").textContent = "Put apps, folders, websites, games and more on this wheel";
    $("scTarget").textContent = "SETTINGS › SHORTCUTS";
  }
  // The tabs by name between the shoulders, each clickable, in a row of its own under the ring.
  const pager = $("scPager");
  pager.hidden = tabs.length < 2;
  pager.innerHTML = tabs.length > 1
    ? `<div class="legend-item" data-press="LB">${slot("LB")}</div>` +
      `<div class="sc-pages">${tabs.map((t, i) =>
        `<span class="sc-page${i === scWheelTab ? " on" : ""}" data-sc-page="${i}">${esc(t.name)}</span>`).join("")}</div>` +
      `<div class="legend-item" data-press="RB">${slot("RB")}</div>`
    : "";
  centerPager(pager.querySelector(".sc-pages"));
  $("scFoot").innerHTML = n ? foot(["A", "Open"], ["B", "Back"]) : foot(["A", "Set up"], ["B", "Back"]);
}

$("scPager").addEventListener("click", (e) => {
  const page = e.target instanceof Element ? e.target.closest("[data-sc-page]") : null;
  if (!page || !shortcutWheelOpen) return;
  scWheelTab = parseInt(page.dataset.scPage, 10) || 0;
  scWheelIdx = 0;
  renderShortcutWheel(true);
});

function scWheelStep(dir) {
  const total = scTabs().length;
  if (total < 2) return;
  scWheelTab = (scWheelTab + dir + total) % total;
  scWheelIdx = 0;
  renderShortcutWheel(true);
}

function shortcutWheelInput(btn) {
  const items = scWheelItems();
  const n = items.length;
  switch (btn) {
    case "Right": case "Down": if (n) { scWheelIdx = (scWheelIdx + 1) % n; renderShortcutWheel(); } break;
    case "Left": case "Up": if (n) { scWheelIdx = (scWheelIdx - 1 + n) % n; renderShortcutWheel(); } break;
    case "LB": scWheelStep(-1); break;
    case "RB": scWheelStep(1); break;
    case "A":
      if (!focusVisible()) break;
      if (n) scRunFromWheel(items[scWheelIdx]); else scOpenSettings();
      break;
    case "B": closeShortcutWheel(true); break;
    // A shortcut's own button opens it from the wheel too: the badge on its spoke says it will.
    case "X": case "Y": case "View": case "Menu": {
      const hit = scAllItems().find(i => i.button && comboEq(i.button, btn));
      if (hit) scRunFromWheel(hit);
      break;
    }
  }
}

/* The stick points at a spoke, as on the Power Wheel. Called from the host's stick message. */
function shortcutWheelStick(x, y) {
  const n = scWheelItems().length;
  if (!n) return;
  const i = stickToSpoke(x, y, n);
  if (i !== scWheelIdx) { scWheelIdx = i; renderShortcutWheel(); }
}

/* The list changed under an open wheel: keep the tab and the highlight if they still exist. */
function refreshShortcutWheel() {
  if (shortcutWheelOpen) renderShortcutWheel();
}

/* What the dialog says before a shortcut that asks first. */
function scConfirmText(it) {
  const running = S.gameRunning ? gameById(S.runningGameId) : null;
  const still = running ? ` ${running.title} is still running and is asked to close too.` : "";
  switch (it.kind === "builtin" ? it.target : "") {
    case "signOut": return { title: "Sign out of Windows?", body: "Every program is asked to close first, the way the Start menu does it." + still, yesLabel: "Sign out", icon: "signOut", danger: true };
    case "restart": return { title: "Restart the PC?", body: "Every program is asked to close first, the way the Start menu does it." + still, yesLabel: "Restart", icon: "restart", danger: true };
    case "shutdown": return { title: "Shut down the PC?", body: "Every program is asked to close first, the way the Start menu does it." + still, yesLabel: "Shut down", icon: "power", danger: true };
  }
  if (it.kind === "command") return { title: `Run ${it.name}?`, body: scWhat(it), yesLabel: "Run", icon: "terminal", danger: false };
  return { title: `Open ${it.name}?`, body: scWhat(it), yesLabel: "Open", icon: "play", danger: false };
}

/* What a game shortcut asks while another game runs: the library's own swap question. */
function scSwapText(it) {
  const g = scGame(it);
  const running = gameById(S.runningGameId);
  return {
    title: running ? `Close ${running.title}?` : "Close the running game?",
    body: `${g ? g.title : "The game"} will start once it has closed.`,
    yesLabel: `Close and play ${g ? g.title : "it"}`, icon: "play", danger: false,
  };
}

/* A pick on the wheel. Anything that asks first asks here, over the wheel (the confirm is drawn
   after it in index.html), and only a yes sends it. */
function scRunFromWheel(it) {
  if (it.kind === "game") {
    const g = scGame(it);
    if (!g) { toast("That game is no longer in the library"); return; }
    if (S.gameRunning && S.runningGameId !== g.id) { askConfirm({ ...scSwapText(it), onYes: () => scSend(it, true) }); return; }
  } else if (scNeedsConfirm(it)) {
    askConfirm({ ...scConfirmText(it), onYes: () => scSend(it, true) });
    return;
  }
  scSend(it, false);
}

/* The host closes the overlay itself (it has to park the launcher and hand the foreground on
   before it starts anything), so this only takes the page's menus down. */
function scSend(it, confirmed) {
  shortcutWheelOpen = false; radialOpen = false; radialSub = null;
  hideOverlay("overlay-shortcuts");
  hideOverlay("overlay-radial");
  setOverlayMode(false);
  const g = scGame(it);
  if (g && !(S.gameRunning && S.runningGameId === g.id)) toast(`Launching ${g.title}…`);
  send({ cmd: "shortcutRun", id: it.id, confirmed: !!confirmed });
}

/* An empty wheel's A: Settings → Shortcuts, with Loungepad brought up for it. */
function scOpenSettings() {
  const tab = scWheelTab;
  shortcutWheelOpen = false; radialOpen = false; radialSub = null;
  hideOverlay("overlay-shortcuts");
  hideOverlay("overlay-radial");
  setOverlayMode(false);
  send({ cmd: "goHome" });
  switchView("settings");
  setSettingsTab("shortcuts");
  scUi.tab = tab;
  enterSettingsPane("rows");
}

/* A shortcut's button was pressed on the desktop and it asks first: the host has brought Loungepad
   up, and the question is asked here. */
function onShortcutAsk(m) {
  const f = scFindItem(m.id);
  if (!f) return;
  const it = f.item;
  const yes = () => send({ cmd: "shortcutRun", id: it.id, confirmed: true });
  if (it.kind === "game") askConfirm({ ...scSwapText(it), onYes: yes });
  else askConfirm({ ...scConfirmText(it), onYes: yes });
}

/* ============================== Settings → Shortcuts ============================== */

/* The editor is the wheel editor (wheeledit.js) over this model. scUi: { tab, moving, pending, drag }
   -- the tab it shows, a shortcut picked up with Y (or a drag), and a host push held back while one
   is (see onShortcuts). Declared in app.js. */
const scModel = {
  key: "shortcuts",
  focusPrefix: "setrow:shortcuts:",
  addLabel: "Add a shortcut",
  backLabel: "Categories",
  get ui() { return scUi; },
  tabs: () => scTabs(),
  setTabs: (tabs) => { S.shortcuts = { ...scData(), tabs }; },
  maxItems: () => scMaxItems(),
  maxTabs: () => scMaxTabs(),
  shape: () => "shortcuts",
  spokeHtml: (it) => scSpokeInner(it),
  spokeClass: (it) => (scDanger(it) ? "danger " : "") + (scMissing(it) ? "missing" : ""),
  cardHtml: (tab, sel, onAdd) => scCardHtml(tab, sel, onAdd),
  movingIconHtml: (it) => scIconHtml(it),
  headHtml: null,
  edit: (id) => scEdit(id),
  add: () => scStartAdd(),
  rename: (it) => openInput("SHORTCUT NAME", it.name, v => { if (v) scSaveItem({ ...it, name: v }); }),
  remove: (it) => scConfirmRemove(it),
  back: () => enterSettingsPane("nav"),
  applyPending: () => scApplyPending(),
  newTabId: () => scNewId(),
  deleteTabText: (t, n) => ({
    sub: n ? `Its ${n === 1 ? "shortcut goes" : n + " shortcuts go"} with it` : "It is empty",
    body: n ? "The shortcuts on it go too. Nothing on the PC is touched." : "It has nothing on it.",
  }),
  optionsHead: () => {
    const tab = scTabs()[scUi.tab];
    return { kicker: "OPTIONS", title: "Shortcuts", sub: tab ? `${tab.name} tab · ${(tab.items || []).length} of ${scMaxItems()}` : "",
      iconHtml: `<span class="panel-app-icon">${iconSvg("bolt")}</span>` };
  },
  optionSections: () => [{
    title: "Every tab",
    items: [{
      key: "sc:restore", label: "Restore default shortcuts", icon: "refresh", danger: true,
      hint: "Every tab is replaced by the System tab Loungepad started with",
      action: () => askConfirm({
        title: "Restore the default shortcuts?", body: "Every tab and every shortcut you added goes, and the System tab comes back as it was.",
        yesLabel: "Restore", icon: "refresh", danger: true,
        onYes: () => { send({ cmd: "shortcutsReset" }); scUi.tab = 0; settingsIdx = 0; },
      }),
    }],
  }],
  send: {
    move: (id, tabId, index) => send({ cmd: "shortcutMove", id, tabId, index }),
    tabAdd: (id, name, index) => send({ cmd: "shortcutTabAdd", id, name, index }),
    tabRename: (id, name) => send({ cmd: "shortcutTabRename", id, name }),
    tabMove: (id, index) => send({ cmd: "shortcutTabMove", id, index }),
    tabRemove: (id) => send({ cmd: "shortcutTabRemove", id }),
  },
};

function shortcutsTabReset() { weReset(scModel); }
function scEditorTab() { return weTab(scModel); }
function shortcutsSettingsRows() { return weRows(scModel); }

function scCardHtml(tab, sel, onAdd) {
  if (!tab) return "";
  if (onAdd || !sel) {
    const left = scMaxItems() - (tab.items || []).length;
    return `<div class="sc-card-icon add">${iconSvg("plus")}</div>` +
      `<div class="sc-card-kind mono">${esc(tab.name.toUpperCase())}</div>` +
      `<div class="sc-card-name">Add a shortcut</div>` +
      `<div class="sc-card-what">An app, a game, a folder or a file, a website, a command, or one of Windows' own windows.</div>` +
      `<div class="sc-card-note">${left === 1 ? "Room for one more on this tab" : `Room for ${left} more on this tab`}</div>`;
  }
  const notes = [];
  if (scMissing(sel)) notes.push(`<div class="sc-card-warn">${sel.kind === "game" ? "Not installed, or no longer in the library" : "Not found on this PC"}</div>`);
  const warn = sel.button ? scBindingWarn(sel) : null;
  if (warn) notes.push(`<div class="sc-card-warn">${hintHtml(warn)}</div>`);
  else if (sel.button) scBindingNotes(sel).forEach(n => notes.push(`<div class="sc-card-note">${hintHtml(n)}</div>`));
  const extras = [];
  if (scNeedsConfirm(sel)) extras.push("Asks first");
  if (sel.kind === "command" && sel.console === "hide") extras.push("No console window");
  return `<div class="sc-card-icon">${scIconHtml(sel)}</div>` +
    `<div class="sc-card-kind mono">${esc(scKindLabel(sel).toUpperCase())}</div>` +
    `<div class="sc-card-name">${esc(sel.name)}</div>` +
    `<div class="sc-card-what">${esc(scWhat(sel))}</div>` +
    `<div class="sc-card-row"><span>Button</span><span>${sel.button ? comboHtml(sel.button) : `<span class="set-none">None</span>`}</span></div>` +
    (extras.length ? `<div class="sc-card-note">${esc(extras.join(" · "))}</div>` : "") +
    notes.join("");
}

/* A push that arrived while something was picked up (icons, usually) waits until it is put down,
   or it would put the list back under the hand. */
function scApplyPending() {
  if (!scUi.pending) return;
  S.shortcuts = scUi.pending;
  scUi.pending = null;
}

/* ---- editing one ---- */

/* Into the page's copy first, so the wheel shows the change before the host answers; its
   shortcuts push then replaces the list with what it stored. */
function scSaveItem(next) {
  const f = scFindItem(next.id);
  if (!f) return;
  f.tab.items[f.index] = { ...f.item, ...next };
  send({ cmd: "shortcutSave", tabId: f.tab.id, item: scWire(f.tab.items[f.index]) });
  if (view === "settings" && settingsTab === "shortcuts") renderSettings();
}

/* What goes to the host: the stored fields, never the page's own (the icon it found, missing). */
function scWire(it) {
  return {
    id: it.id, name: it.name, kind: it.kind, target: it.target, args: it.args || null, startIn: it.startIn || null,
    exe: it.exe || null, console: it.console || null, confirm: !!it.confirm, iconFile: it.iconFile || null, button: it.button || null,
  };
}

let scEditingId = null;

function scEdit(id, idx) {
  scEditingId = id;
  const item = () => (scFindItem(id) || {}).item;
  openForm({
    kind: "shortcut",
    kicker: () => { const f = scFindItem(id); return f ? `${f.tab.name.toUpperCase()} · ${scKindLabel(f.item).toUpperCase()}` : ""; },
    title: () => (item() || {}).name || "",
    sub: () => item() ? scWhat(item()) : "",
    rows: () => scEditRows(id),
    backLabel: "Done",
    onClose: () => { if (scEditingId === id) scEditingId = null; },
  });
  if (idx) { formState.idx = idx; renderForm(); }
}

function scEditRows(id) {
  const f = scFindItem(id);
  if (!f) return [{ note: "This shortcut is no longer on the wheel." }];
  const it = f.item;
  const save = (patch) => scSaveItem({ ...it, ...patch });
  const rows = [];
  rows.push({
    name: "Name", type: "html", aLabel: "Rename",
    valueHtml: `<span class="set-text">${esc(it.name)}</span>`,
    action: () => openInput("SHORTCUT NAME", it.name, v => { if (v) save({ name: v }); }),
  });
  rows.push({
    name: "Opens", hint: scWhat(it), type: "html", aLabel: "Change",
    valueHtml: `<span class="set-text">${esc(scKindLabel(it))}</span>`,
    // A new target keeps the name it is given, the icon, the button and the question; arguments
    // and the folder only while it is the same kind of thing, and the old target's own icon never.
    action: () => scFromForm(back => scPickAnything(picked => {
      const same = picked.kind === it.kind;
      save({
        ...picked, exe: picked.exe || null, autoIcon: null, missing: false,
        args: same ? it.args : null, startIn: same ? it.startIn : null, console: same ? it.console : null,
      });
      back();
    }, "Change what it opens", back)),
  });
  if (it.kind === "program" || it.kind === "command") {
    rows.push({
      name: "Arguments", hint: it.args || "None", type: "html", aLabel: "Type", valueHtml: "",
      action: () => openInput("ARGUMENTS", it.args || "", v => save({ args: v || null })),
    });
    rows.push({
      name: "Start in", hint: it.startIn || (it.kind === "program" ? "The program's own folder" : "Its own folder, or your user folder"), type: "html", aLabel: "Type", valueHtml: "",
      action: () => openInput("START IN (A FOLDER'S FULL PATH)", it.startIn || "", v => save({ startIn: v || null })),
    });
  }
  if (it.kind === "command") {
    const hide = it.console === "hide";
    rows.push({
      name: "Hide the console window", hint: "Runs with no window at all, so nothing on screen says it ran",
      type: "toggle", value: hide, action: () => save({ console: hide ? null : "hide" }), adjust: () => save({ console: hide ? null : "hide" }),
    });
  }
  const builtinAsks = it.kind === "builtin" && !!(scBuiltin(it.target) || {}).confirm;
  if (it.kind === "command" || it.confirm) rows.push({
    name: "Ask before running", hint: "A question first, so a stray press does nothing",
    type: "toggle", value: !!it.confirm, action: () => save({ confirm: !it.confirm }), adjust: () => save({ confirm: !it.confirm }),
  });
  else if (builtinAsks) rows.push({ note: "This one always asks first." });
  rows.push({
    name: "Icon", type: "html", aLabel: "Change",
    valueHtml: `<span class="sc-icon-val">${scIconHtml(it)}</span><span class="set-text">${it.iconFile ? "Your picture" : "Automatic"}</span>`,
    action: () => scFromForm(back => scPickIcon(it, back)),
  });
  rows.push({
    name: "Controller button",
    hint: (it.button
      ? `Opens “${it.name}” from the desktop, with Loungepad and games out of the way`
      : "Optional. Press it on the desktop and this opens, without the wheel")
      + (it.button ? scBindingNotes(it).map(n => ". " + n).join("") : ""),
    warn: it.button ? scBindingWarn(it) : null,
    type: "html", aLabel: "Record",
    valueHtml: it.button ? comboHtml(it.button) : `<span class="set-none">None</span>`,
    action: () => openCapture(null, { name: it.name }, combo => save({ button: combo }), combo => scBindingBlock(it, combo)),
  });
  if (it.button) rows.push({ name: "Clear the button", type: "action", label: "Clear", action: () => save({ button: null }) });
  if (scTabs().length > 1) rows.push({
    name: "Move to another tab", hint: `On ${f.tab.name} now`, type: "action", label: "Move",
    action: () => scFromForm(back => scPickTab(it, back)),
  });
  rows.push({
    name: "Remove this shortcut", type: "action", label: "Remove", danger: true,
    action: () => scConfirmRemove(it),
  });
  return rows;
}

/* The choice sheet is drawn UNDER the form sheet (overlays stack in document order), so the form
   steps aside while a list is open and comes back on the same row afterwards. */
function scFromForm(open) {
  const id = scEditingId, idx = formState ? formState.idx : 0;
  closeForm();
  open(() => { if (scFindItem(id)) scEdit(id, idx); });
}

function scConfirmRemove(it) {
  askConfirm({
    title: `Remove “${it.name}”?`, body: "It comes off the wheel. Nothing on the PC is touched.",
    yesLabel: "Remove", icon: "trash", danger: true,
    onYes: () => scRemove(it.id),
  });
}

function scRemove(id) {
  const f = scFindItem(id);
  if (!f) return;
  if (formState && formState.kind === "shortcut") closeForm();
  f.tab.items.splice(f.index, 1);
  send({ cmd: "shortcutRemove", id });
  settingsIdx = Math.max(0, f.index - (f.index >= f.tab.items.length ? 1 : 0));
  if (view === "settings" && settingsTab === "shortcuts") renderSettings();
}

function scPickTab(it, back) {
  const here = scFindItem(it.id);
  const items = scTabs().filter(t => !here || t.id !== here.tab.id).map(t => {
    const full = (t.items || []).length >= scMaxItems();
    return {
      label: t.name, icon: "apps", sub: full ? "Full" : `${(t.items || []).length} of ${scMaxItems()}`,
      action: () => {
        if (full) { toast(`${t.name} is full: a tab holds ${scMaxItems()}`); back(); return; }
        const f = scFindItem(it.id);
        if (!f) return;
        f.tab.items.splice(f.index, 1);
        t.items = t.items || [];
        t.items.push(f.item);
        scUi.tab = scTabs().indexOf(t);
        settingsIdx = t.items.length - 1;
        send({ cmd: "shortcutMove", id: it.id, tabId: t.id, index: t.items.length - 1 });
        if (view === "settings" && settingsTab === "shortcuts") renderSettings();
        back();
      },
    };
  });
  openChoice("Move to", items, { onBack: back });
}

/* Two choices (the user's call, Oct 13 2026): the automatic picture, or one from the PC through
   Windows' own dialog on the host, which makes it a small PNG and answers shortcutIconPicked. */
function scPickIcon(it, back) {
  openChoice("Icon", [
    {
      label: "Automatic", icon: SC_KIND_ICONS[it.kind] || "apps", sub: scAutoIconWords(it), checked: !it.iconFile, radio: true,
      action: () => { if (it.iconFile) scSaveItem({ ...it, iconFile: null, customIcon: null }); back(); },
    },
    {
      label: "Choose an icon…", icon: "image", sub: "A picture or an icon from this PC: PNG, JPEG, ICO, or a program's own icon",
      checked: !!it.iconFile, radio: true, action: () => scChooseIcon(it, back),
    },
  ], { onBack: back });
}

function scAutoIconWords(it) {
  switch (it.kind) {
    case "builtin": return "Loungepad's own symbol for it";
    case "game": return "The game's art";
    case "url": return "The website's own icon";
    default: return "The icon Windows shows for it";
  }
}

let scIconWaiting = null;
function scChooseIcon(it, back) {
  scIconWaiting = { id: it.id, back };
  send({ cmd: "shortcutIconPick", id: it.id });
}
function onShortcutIconPicked(m) {
  const w = scIconWaiting;
  scIconWaiting = null;
  if (m.error) toast(m.error);
  if (w && w.id === m.id) w.back();
}

/* ---- adding one ---- */

function scStartAdd() {
  const tab = scEditorTab();
  if (!tab) return;
  if ((tab.items || []).length >= scMaxItems()) { toast(`A tab holds ${scMaxItems()}. Y opens the options, to add another tab`); return; }
  scPickAnything(picked => scAddItem(tab.id, picked), "Add a shortcut", null);
}

/* What to open: the same list for a new shortcut and for "Opens" on an existing one. `done` gets
   { kind, name, target, exe? }. B on a sub-list comes back here; B here goes to `back`. */
function scPickAnything(done, title, back) {
  const again = () => scPickAnything(done, title, back);
  openChoice(title, [
    { cat: "PROGRAMS" },
    { label: "An app", icon: "apps", sub: "From the Start menu, Store apps included", action: () => scPickApp(done, again) },
    { label: "A program on this PC", icon: "terminal", sub: "Browse for its .exe", action: () => scBrowse("program", done, again) },
    { label: "A game from your library", icon: "gamepad", sub: "Starts it, the same as Play", action: () => scPickGame(done, again) },
    { cat: "PLACES" },
    { label: "A folder", icon: "folder", sub: "Opens in File Explorer", action: () => scPickFolder(done, again) },
    { label: "A file", icon: "file", sub: "Opens in the app it belongs to", action: () => scBrowse("file", done, again) },
    { label: "A website", icon: "globe", sub: "Opens in your browser", action: () => scPickUrl(done, again) },
    { cat: "MORE" },
    { label: "A command or script", icon: "terminal", sub: "A .bat, .cmd or .ps1 file, or a command line", action: () => scPickCommand(done, again) },
    { label: "Windows", icon: "gear", sub: "File Explorer, Settings pages, the Volume Mixer", action: () => scPickBuiltin("windows", done, again) },
    { label: "Power", icon: "power", sub: "Lock, rest, sleep, sign out, restart, shut down", action: () => scPickBuiltin("power", done, again) },
  ], { onBack: back });
}

/* Into the page's copy at the + spoke's place (the end), then the edit sheet for whatever is
   optional; B there is Done. */
function scAddItem(tabId, picked) {
  const tab = scTabs().find(t => t.id === tabId);
  if (!tab) return;
  if ((tab.items || []).length >= scMaxItems()) { toast(`${tab.name} is full: a tab holds ${scMaxItems()}`); return; }
  const it = { id: scNewId(), args: null, startIn: null, exe: null, console: null, confirm: false, iconFile: null, customIcon: null, button: null, autoIcon: null, missing: false, ...picked };
  tab.items = tab.items || [];
  tab.items.push(it);
  send({ cmd: "shortcutSave", tabId, item: scWire(it), index: tab.items.length - 1 });
  if (view === "settings" && settingsTab === "shortcuts") {
    settingsPane = "rows";
    settingsIdx = tab.items.length - 1;
    renderSettings();
  }
  scEdit(it.id);
}

function scPickBuiltin(group, done, back) {
  const items = (scData().catalog || []).filter(b => b.group === group).map(b => ({
    label: b.name, icon: b.icon, sub: b.desc + (b.confirm ? ". Asks first" : ""), danger: !!b.danger,
    action: () => done({ kind: "builtin", name: b.name, target: b.id }),
  }));
  openChoice(group === "power" ? "Power" : "Windows", items, { onBack: back });
}

function scPickUrl(done, back) {
  openInput("WEBSITE ADDRESS", "https://", v => {
    if (!v) { back(); return; }
    const url = scNormalUrl(v);
    if (!url) { toast("That is not a web address"); back(); return; }
    done({ kind: "url", name: scUrlName(url), target: url });
  });
}

/* An address as typed, made whole: "youtube.com" is https://youtube.com/. Only http and https. */
function scNormalUrl(v) {
  let s = String(v || "").trim();
  if (!s || s === "https://" || s === "http://") return null;
  if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(s)) s = "https://" + s;
  try {
    const u = new URL(s);
    if (!/^https?:$/.test(u.protocol) || !u.host || !u.host.includes(".") && u.hostname !== "localhost") return null;
    return u.href;
  } catch { return null; }
}

function scPickCommand(done, back) {
  const again = () => scPickCommand(done, back);
  openChoice("A command or script", [
    { label: "Pick a script", icon: "file", sub: "A .bat, .cmd or .ps1 file (or an .exe)", action: () => scBrowse("script", done, again) },
    { label: "Type a command", icon: "terminal", sub: "Run by cmd, the way the Run box runs one", action: () => openInput("COMMAND", "", v => {
        if (!v) { again(); return; }
        done({ kind: "command", name: v.split(/\s+/)[0].replace(/^.*[\\/]/, "") || "Command", target: v });
      }) },
  ], { onBack: back });
}

function scPickFolder(done, back) {
  const again = () => scPickFolder(done, back);
  const items = (scData().knownFolders || []).map(f => ({
    label: f.name, icon: "folder", sub: f.path,
    action: () => done({ kind: "folder", name: f.name, target: f.path }),
  }));
  items.push({ cat: "OR" });
  items.push({ label: "Browse for a folder…", icon: "folderPlus", sub: "Any folder, on any drive", action: () => scBrowse("folder", done, again) });
  openChoice("A folder", items, { onBack: back });
}

/* Windows' own dialog, on the host. The answer comes back as shortcutPicked. */
let scBrowseWaiting = null;
function scBrowse(kind, done, back) {
  scBrowseWaiting = { kind, done, back };
  send({ cmd: "shortcutBrowse", kind });
}
function onShortcutPicked(m) {
  const w = scBrowseWaiting;
  scBrowseWaiting = null;
  if (!w || w.kind !== m.kind) return;
  if (!m.path) { if (w.back) w.back(); return; }
  const kind = { program: "program", file: "file", folder: "folder", script: "command" }[m.kind];
  w.done({ kind, name: m.name || m.path, target: m.path });
}

/* ---- the app and game lists ---- */

/* The Start menu's apps, kept for a few minutes: reading it takes a second or two, and the icons
   arrive after the list, as blob URLs so the list stays light to redraw (renderMenu rebuilds every
   row on every step, and a few hundred data URIs in the markup made each step slow). */
let scApps = null;            // { list, icons: Map(id -> blob url), at }
let scAppsWaiting = null;     // the picker waiting on the host's list

function scPickApp(done, back, query) {
  if (scApps && Date.now() - scApps.at < 5 * 60000) { scShowApps(done, back, query); return; }
  scAppsWaiting = { done, back };
  toast("Reading the Start menu…");
  send({ cmd: "shortcutApps" });
}

function onShortcutApps(m) {
  const icons = scApps ? scApps.icons : new Map();
  scApps = { list: m.apps || [], icons, at: Date.now() };
  const w = scAppsWaiting;
  scAppsWaiting = null;
  if (m.error) toast(m.error);
  if (w) scShowApps(w.done, w.back);
}

function onShortcutAppIcons(m) {
  if (!scApps) return;
  Object.entries(m.icons || {}).forEach(([id, uri]) => {
    if (scApps.icons.has(id)) return;
    const url = scBlobUrl(uri);
    if (url) scApps.icons.set(id, url);
  });
  if (choiceState && choiceState.scApps) {
    choiceState.items.forEach(r => { if (r.appId && scApps.icons.has(r.appId)) r.img = scApps.icons.get(r.appId); });
    clearTimeout(onShortcutAppIcons.t);
    onShortcutAppIcons.t = setTimeout(() => { if (choiceState && choiceState.scApps) renderChoice(); }, 60);
  }
}

function scBlobUrl(dataUri) {
  try {
    const s = String(dataUri);
    const comma = s.indexOf(",");
    const head = s.slice(0, comma), body = s.slice(comma + 1);
    const mime = (/data:([^;,]+)/.exec(head) || [])[1] || "image/png";
    if (!/;base64$/i.test(head)) return URL.createObjectURL(new Blob([decodeURIComponent(body)], { type: mime }));
    const bin = atob(body);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return URL.createObjectURL(new Blob([bytes], { type: mime }));
  } catch { return null; }
}

function scLetter(name) {
  const c = titleKey(name).charAt(0).toUpperCase();
  return c >= "A" && c <= "Z" ? c : "#";
}

/* A search row first (the on-screen keyboard comes up with it), then everything A to Z under a
   letter each; LB and RB jump a letter. */
function scShowApps(done, back, query) {
  const q = query ? titleKey(query) : "";
  const list = scApps.list.filter(a => !q || titleKey(a.name).includes(q));
  const items = [{
    label: query ? `Search: “${query}”` : "Search by name…", icon: "search", sub: query ? "A to change it" : null,
    action: () => openInput("SEARCH APPS", query || "", v => scShowApps(done, back, v || "")),
  }];
  let letter = null;
  list.forEach(a => {
    const L = scLetter(a.name);
    if (L !== letter) { letter = L; items.push({ cat: L }); }
    items.push({
      label: a.name, icon: "apps", img: scApps.icons.get(a.id) || null, appId: a.id,
      action: () => done({ kind: "app", name: a.name, target: a.id, exe: a.exe || null }),
    });
  });
  if (!list.length) items.push({ label: query ? "Nothing matches" : "No apps found", icon: "info", action: () => scShowApps(done, back) });
  openChoice(query ? "Apps" : "Pick an app", items, { onBack: query ? () => scShowApps(done, back) : back, jump: ["", "Jump a letter"] });
  choiceState.scApps = true;
}

/* Installed games only: an uninstalled one cannot be started from a wheel. Recently played first,
   then favourites, then everything A to Z. */
function scPickGame(done, back, query) {
  const q = query ? titleKey(query) : "";
  const all = (S.games || []).filter(g => g.installed && !g.hidden && (!q || titleKey(g.title).includes(q)));
  const row = (g) => ({
    label: g.title, sub: g.platform, icon: "gamepad", img: bannerUrl(g) || null, imgWide: true,
    action: () => done({ kind: "game", name: g.title, target: g.id }),
  });
  const items = [{
    label: query ? `Search: “${query}”` : "Search by name…", icon: "search", sub: query ? "A to change it" : null,
    action: () => openInput("SEARCH GAMES", query || "", v => scPickGame(done, back, v || "")),
  }];
  if (!q) {
    const recent = all.filter(g => g.lastPlayed).sort((a, b) => new Date(b.lastPlayed) - new Date(a.lastPlayed)).slice(0, 6);
    if (recent.length) { items.push({ cat: "RECENTLY PLAYED" }); recent.forEach(g => items.push(row(g))); }
    const favs = all.filter(g => g.favorite).sort((a, b) => a.title.localeCompare(b.title));
    if (favs.length) { items.push({ cat: "FAVOURITES" }); favs.forEach(g => items.push(row(g))); }
  }
  let letter = null;
  all.slice().sort((a, b) => a.title.localeCompare(b.title)).forEach(g => {
    const L = scLetter(g.title);
    if (L !== letter) { letter = L; items.push({ cat: L }); }
    items.push(row(g));
  });
  if (!all.length) items.push({ label: query ? "Nothing matches" : "No installed games", icon: "info", action: () => scPickGame(done, back) });
  openChoice(query ? "Games" : "Pick a game", items, { onBack: query ? () => scPickGame(done, back) : back, jump: ["", "Jump a section"] });
}

/* ---- buttons ---- */

/* Why a shortcut's button would not do what it says: the system's own rules (bindingIssues with no
   app), another shortcut or an Everywhere action on it, and the apps whose own action wins while
   they are in front. */
function scBindingIssues(it, combo) {
  const issues = bindingIssues(null, null, combo);
  if (!comboParts(combo).length) return issues;
  const twin = scAllItems().find(x => x.id !== it.id && comboEq(x.button, combo));
  if (twin) issues.push({ level: "block", text: `The shortcut “${twin.name}” already uses that button. Pick another, or clear it there first.` });
  const ev = everywhereApp();
  const evHit = appActions(ev).find(a => comboEq(a.button, combo));
  if (evHit) issues.push({ level: "block", text: `Everywhere's “${evHit.name}” already uses that button (Settings › Actions). Pick another.` });
  const apps = shownApps().filter(a => a.id !== EVERYWHERE_ID)
    .map(a => ({ a, hit: appActions(a).find(x => comboEq(x.button, combo)) })).filter(x => x.hit);
  if (apps.length) issues.push({ level: "note", text: `In ${apps.slice(0, 2).map(x => x.a.name).join(" and ")}${apps.length > 2 ? " and others" : ""}, their own action takes this button while they are in front.` });
  return issues;
}
function scBindingBlock(it, combo) { const i = scBindingIssues(it, combo).find(x => x.level === "block"); return i ? i.text : null; }
function scBindingWarn(it) {
  const i = scBindingIssues(it, it.button).find(x => x.level === "warn" || x.level === "block");
  return i ? i.text : null;
}
function scBindingNotes(it) { return scBindingIssues(it, it.button).filter(x => x.level === "note").map(x => x.text); }

/* ---- the host's messages ---- */

function onShortcuts(data) {
  if (!data) return;
  if (scUi.moving) { scUi.pending = data; return; }
  S.shortcuts = data;
  if (view === "settings" && settingsTab === "shortcuts") renderSettings();
  if (formState && formState.kind === "shortcut") renderForm();
  refreshShortcutWheel();
}
