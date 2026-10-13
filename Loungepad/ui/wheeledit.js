"use strict";

/*
 * The wheel editor: how Settings arranges a wheel. Settings → Shortcuts and an app's page under
 * Settings → Actions are both this, so they look and answer exactly alike.
 *
 * The editor IS the wheel: the tab's items round the ring as the Power Wheel lays them out, a
 * dashed + spoke where the next one will go, the tabs across the top and the highlighted item's
 * details beside it. A edits (the model's edit sheet, over the wheel), Y picks an item up to slide
 * it round the ring or carry it to another tab, X is the tab's options, LB/RB turn the tab, B goes
 * back. A mouse drags, and the joystick mouse can rest on any control of the tab strip for A to
 * press it: in the launcher A is the page's A, never a click, and the joystick mouse only moves the
 * pointer, so a control the pointer cannot make the highlight could not be pressed from the pad.
 *
 * What is on the wheel and what an edit means come from a model (scModel in shortcuts.js, acModel
 * in actions.js):
 *
 *   key, focusPrefix    the Settings tab it belongs to, and the spokes' focus keys
 *   ui                  { tab, moving, pending, drag, droppedAt }, declared in app.js
 *   tabs()              the page's copy, [{ id, name, items: [{ id, name, button, ... }] }]
 *   setTabs(tabs)       put a copy back (a cancelled move)
 *   maxItems(), maxTabs(), shape()
 *   spokeHtml(it), spokeClass(it), cardHtml(tab, sel, onAdd), movingIconHtml(it), headHtml()
 *   edit(id), add(tab), rename(it), remove(it), back(), active(), applyPending()
 *   send.move / tabAdd / tabRename / tabMove / tabRemove
 *   addLabel, backLabel, newTabId(), deleteTabText(tab, n), tabOptionsExtra()
 *
 * Loaded before actions.js and shortcuts.js; nothing here runs until Settings is drawn.
 */

/* The editor's ring: 8 spokes of 140px at this radius sit 161px apart, so none overlap. */
const WE_R = 228, WE_C = 300;

/* The model on screen, if Settings is showing a wheel: the Shortcuts tab, or an app's actions. */
function activeWheelModel() {
  if (view !== "settings") return null;
  if (settingsTab === "shortcuts") return scModel;
  if (settingsTab === "actions" && actionsUi.level === "app") return acModel;
  return null;
}

function weTab(M) {
  const tabs = M.tabs();
  M.ui.tab = Math.max(0, Math.min(M.ui.tab, tabs.length - 1));
  return tabs[M.ui.tab] || null;
}

function weFind(M, id) {
  for (const tab of M.tabs()) {
    const index = (tab.items || []).findIndex(x => x.id === id);
    if (index >= 0) return { tab, item: tab.items[index], index };
  }
  return null;
}

/* Kept while an item is being moved, so picking one up does not shift every spoke round; a move
   never lands on it (weMoveTo stops at the last item). */
function weHasAdd(M, tab) { return !!tab && (tab.items || []).length < M.maxItems(); }

/* The rows the generic Settings code counts: one per spoke, the + spoke last. renderSettings hands
   the drawing to weRender; these are what enterSettingsPane clamps the index by. */
function weRows(M) {
  const tab = weTab(M);
  if (!tab) return [];
  const rows = (tab.items || []).map(it => ({ name: it.name, action: () => M.edit(it.id) }));
  if (weHasAdd(M, tab)) rows.push({ name: M.addLabel, action: () => M.add(tab) });
  return rows;
}

function weActive(M) { return activeWheelModel() === M; }

/* Tidy up on the way out: an item still picked up is put back where it was. */
function weReset(M) {
  if (M.ui.moving) weCancelMove(M);
  M.ui.tab = 0;
}

/*
 * Draws the editor into Settings' scroller: the tab strip, the wheel and the details card. The
 * spokes are kept by id and moved rather than rebuilt, so an item being moved slides round the ring
 * to its new place (and a node under the pointer is never destroyed by a hover). Called by
 * renderSettings after it has worked out the pane and the index from the pointer.
 */
function weRender(M, scope, underPointer) {
  const scroll = $("settingsScroll");
  scroll.classList.remove("apps-grid");
  scroll.classList.add("sc-mode");
  const tabs = M.tabs();
  const tab = weTab(M);
  const items = (tab && tab.items) || [];
  const withAdd = weHasAdd(M, tab);
  const count = items.length + (withAdd ? 1 : 0);
  settingsIdx = Math.max(0, Math.min(settingsIdx, Math.max(0, count - 1)));
  const onAdd = withAdd && settingsIdx === items.length;
  const sel = onAdd ? null : items[settingsIdx];

  let root = scroll.querySelector(":scope > .sc-editor");
  if (!root || scroll.children.length !== 1 || root.__shape !== M.shape()) {
    scroll.innerHTML = "";
    root = document.createElement("div");
    root.className = "sc-editor";
    root.innerHTML = `<div class="sc-tabs"></div>
      <div class="sc-body">
        <div class="sc-wheel"><div class="sc-ring"></div>
          <div class="sc-hub"><div class="sc-hub-tab mono"></div><div class="sc-hub-count"></div></div>
        </div>
        <div class="sc-card"></div>
      </div>`;
    root.__shape = M.shape();
    scroll.appendChild(root);
    scroll.scrollTop = 0;
    scroll.__shape = M.shape();
    const strip = root.querySelector(".sc-tabs");
    strip.addEventListener("click", (e) => weStripClick(M, e));
    // The pointer resting on a tab, the + or a shoulder makes it the highlight, so A presses it.
    // In the capture phase, because mouseenter does not bubble.
    strip.addEventListener("mouseenter", (e) => {
      if (!hoverEnabled() || M.ui.drag) return;
      const t = e.target instanceof Element ? e.target.closest("[data-focusable]") : null;
      if (!t || !strip.contains(t)) return;
      settingsPane = "rows";
      renderSettings();
    }, true);
  }

  // ---- the tab strip ----
  // Every control in it is a focusable: not on the D-pad's way (it goes round the ring), but the
  // pointer can rest on each and A then presses it, as a click does.
  const strip = root.querySelector(".sc-tabs");
  const stripHtml = (M.headHtml ? M.headHtml() : "") +
    `<div class="sc-tabs-btn" data-focusable data-focus-key="westrip:LB" data-we-press="LB"${tabs.length > 1 ? "" : " hidden"}>${slot("LB")}</div>` +
    `<div class="sc-tab-list">${tabs.map((t, i) =>
      `<div class="sc-tab${i === M.ui.tab ? " active" : ""}" data-focusable data-focus-key="westrip:tab:${esc(t.id)}" data-we-tab="${i}"><span>${esc(t.name)}</span>` +
      `<span class="sc-tab-count">${(t.items || []).length}</span>` +
      (i === M.ui.tab ? `<span class="sc-tab-more" data-focusable data-focus-key="westrip:more" data-we-press="X" title="Tab options">⋯</span>` : "") + `</div>`).join("")}` +
    (tabs.length < M.maxTabs() ? `<div class="sc-tab sc-tab-add" data-focusable data-focus-key="westrip:new" data-we-newtab="1">${iconSvg("plus")}<span>New tab</span></div>` : "") +
    `</div>` +
    `<div class="sc-tabs-btn" data-focusable data-focus-key="westrip:RB" data-we-press="RB"${tabs.length > 1 ? "" : " hidden"}>${slot("RB")}</div>`;
  if (strip.__html !== stripHtml) { strip.innerHTML = stripHtml; strip.__html = stripHtml; }

  // ---- the ring ----
  const ring = root.querySelector(".sc-ring");
  ring.__nodes = ring.__nodes || new Map();
  const keys = items.map(i => i.id).concat(withAdd ? ["+"] : []);
  const keep = new Set(keys);
  for (const [k, el] of ring.__nodes) if (!keep.has(k)) { el.remove(); ring.__nodes.delete(k); }
  keys.forEach((k, i) => {
    let el = ring.__nodes.get(k);
    if (!el) {
      el = weSpokeEl(M, k);
      ring.__nodes.set(k, el);
      ring.appendChild(el);
    }
    const p = scSpokeXY(i, count, WE_R, WE_C, WE_C);
    el.style.left = p.x.toFixed(1) + "px";
    el.style.top = p.y.toFixed(1) + "px";
    el.dataset.rowIndex = i;
    el.dataset.focusKey = M.focusPrefix + i;
    const it = k === "+" ? null : items[i];
    el.className = "radial-item sc-spoke" + (it ? "" : " add")
      + (it ? " " + M.spokeClass(it) : "")
      + (it && it.button ? " has-btn" : "")
      + (it && M.ui.moving && M.ui.moving.id === it.id ? " moving" : "");
    const html = M.spokeHtml(it);
    if (el.__html !== html) { el.innerHTML = html; el.__html = html; }
  });

  root.querySelector(".sc-hub-tab").textContent = tab ? tab.name.toUpperCase() : "";
  root.querySelector(".sc-hub-count").textContent = tab ? `${items.length} of ${M.maxItems()}` : "";

  // ---- the details card ----
  const card = root.querySelector(".sc-card");
  const cardHtml = M.ui.moving ? weMovingCardHtml(M) : M.cardHtml(tab, sel, onAdd);
  if (card.__html !== cardHtml) { card.innerHTML = cardHtml; card.__html = cardHtml; }

  // The highlight: under a resting pointer it is whatever the pointer is on (renderSettings'
  // rule, plus the tab strip's controls), else the spoke at the index, or the category when the
  // categories have it.
  const resting = weStripUnderPointer(root) || underPointer();
  if (resting) setFocusEl(resting);
  else if (settingsPane === "rows") setScopeKey(scope, M.focusPrefix + settingsIdx);
  else setScopeKey(scope, "settab:" + M.key);
  paintNav();

  // The legend: what each button does to what is under the highlight, or to the one being moved.
  const onStrip = resting && resting.closest(".sc-tabs") ? resting : null;
  const stripA = !onStrip ? null
    : onStrip.dataset.wePress === "LB" ? "Tab before" : onStrip.dataset.wePress === "RB" ? "Tab after"
    : onStrip.dataset.wePress === "X" ? "Tab options" : onStrip.dataset.weNewtab ? "New tab" : "Show this tab";
  const footEl = $("settingsFoot");
  if (footEl) footEl.innerHTML = settingsPane === "nav"
    ? foot(["A", "Open"], ["B", "Back"], ["DpadV", "Category"])
    : M.ui.moving
      ? foot(["DpadH", "Slide"], ...(tabs.length > 1 ? [["LB", "To the tab before"], ["RB", "To the tab after"]] : []), ["A", "Drop"], ["B", "Cancel"])
      : foot(["A", stripA || (onAdd ? "Add" : "Edit")], ...(sel && !onStrip ? [["Y", "Move"]] : []),
          ...(stripA === "Tab options" ? [] : [["X", "Tab options"]]),
          ...(tabs.length > 1 ? [["LB", "Tab before"], ["RB", "Tab after"]] : []), ["B", M.backLabel]);
}

function weMovingCardHtml(M) {
  const f = weFind(M, M.ui.moving.id);
  return `<div class="sc-card-icon">${M.movingIconHtml(f && f.item)}</div>` +
    `<div class="sc-card-kind mono">MOVING</div>` +
    `<div class="sc-card-name">${esc(f ? f.item.name : "")}</div>` +
    `<div class="sc-card-what">${hintHtml("[[Left]] [[Right]] slide it round the ring" + (M.tabs().length > 1 ? ", [[LB]] [[RB]] carry it to another tab" : "") + ". [[A]] drops it there, [[B]] puts it back.")}</div>`;
}

/* The tab strip's control under a resting pointer: the innermost one (the ⋯ sits inside its tab),
   from the browser's hover state, or from the pointer's position under a strip just rebuilt. */
function weStripUnderPointer(root) {
  if (!hoverEnabled() || !pointerOnItem) return null;
  const hot = [...root.querySelectorAll(".sc-tabs [data-focusable]:hover")].pop();
  if (hot) return hot;
  const under = document.elementFromPoint(lastClientX, lastClientY);
  return under && root.contains(under) ? under.closest(".sc-tabs [data-focusable]") : null;
}

/* A spoke, with its handlers. They go by the element's current index and read the list afresh,
   since the same node moves round the ring as items are moved. */
function weSpokeEl(M, key) {
  const el = document.createElement("div");
  el.dataset.focusable = "";
  el.dataset.weKey = key;
  el.addEventListener("mouseenter", () => {
    if (!hoverEnabled() || M.ui.drag) return;
    const i = parseInt(el.dataset.rowIndex, 10) || 0;
    if (settingsIdx === i && settingsPane === "rows") return;
    settingsIdx = i; settingsPane = "rows"; renderSettings();
  });
  el.addEventListener("click", () => {
    if (Date.now() - (M.ui.droppedAt || 0) < 300) return;   // the end of a drag, not a click
    if (M.ui.moving) { weDrop(M); return; }
    settingsIdx = parseInt(el.dataset.rowIndex, 10) || 0;
    settingsPane = "rows";
    renderSettings();
    weActivate(M, settingsIdx);
  });
  if (key !== "+") el.addEventListener("pointerdown", (e) => weDragStart(M, e, key));
  return el;
}

/* Clicks on the strip: a tab, the + New tab chip, the shoulders, the ⋯ on the active tab. */
function weStripClick(M, e) {
  const t = e.target instanceof Element ? e.target : null;
  if (!t) return;
  if (t.closest("[data-we-press]")) e.stopPropagation();
  weStripAct(M, t);
}

/* What a click does to a control of the strip, and A with the pointer resting on one. */
function weStripAct(M, t) {
  const press = t.closest("[data-we-press]");
  if (press) { weInput(M, press.dataset.wePress); return; }
  if (t.closest("[data-we-newtab]")) { weNewTab(M, M.tabs().length); return; }
  const chip = t.closest("[data-we-tab]");
  if (chip && !M.ui.moving) {
    M.ui.tab = parseInt(chip.dataset.weTab, 10) || 0;
    settingsIdx = 0; settingsPane = "rows";
    renderSettings();
  }
}

/* The strip's control the highlight is on, when it is on one: only the pointer ever puts it there. */
function weStripFocus() {
  const el = focusEl(document.getElementById("screen-settings"));
  return el && el.closest && el.closest(".sc-tabs") ? el : null;
}

/* Every button while the highlight is on the wheel. True when it was answered here. Up and Down go
   round the ring like Left and Right, as on the Power Wheel; B is the way back. */
function weInput(M, btn) {
  const tab = weTab(M);
  const n = tab ? (tab.items || []).length : 0;
  if (M.ui.moving) {
    switch (btn) {
      case "Right": case "Down": weMoveStep(M, 1); break;
      case "Left": case "Up": weMoveStep(M, -1); break;
      case "LB": weMoveToTab(M, -1); break;
      case "RB": weMoveToTab(M, 1); break;
      case "A": case "Y": weDrop(M); break;
      case "B": weCancelMove(M); break;
    }
    return true;
  }
  const count = n + (weHasAdd(M, tab) ? 1 : 0);
  switch (btn) {
    case "Right": case "Down":
      if (count) { settingsIdx = (settingsIdx + 1) % count; renderSettings(); }
      return true;
    case "Left": case "Up":
      if (count) { settingsIdx = (settingsIdx - 1 + count) % count; renderSettings(); }
      return true;
    case "A": {
      if (!focusVisible()) return true;
      const onStrip = weStripFocus();
      if (onStrip) weStripAct(M, onStrip); else weActivate(M, settingsIdx);
      return true;
    }
    // Y picks up the spoke under the highlight (never one behind a highlighted tab), X is the
    // tab's options (the user's calls, Oct 13 2026).
    case "Y":
      if (!weStripFocus() && settingsIdx < n) weStartMove(M, tab.items[settingsIdx].id);
      return true;
    case "X": weTabOptions(M); return true;
    case "LB": case "RB": {
      const total = M.tabs().length;
      if (total > 1) { M.ui.tab = (M.ui.tab + (btn === "LB" ? -1 : 1) + total) % total; settingsIdx = 0; renderSettings(); }
      return true;
    }
    case "B": M.back(); return true;
  }
  return false;
}

function weActivate(M, i) {
  const tab = weTab(M);
  if (!tab) return;
  const it = (tab.items || [])[i];
  if (it) M.edit(it.id); else if (weHasAdd(M, tab)) M.add(tab);
}

/* ---- moving ---- */

/* Picked up: it slides round the ring with the D-pad and crosses tabs with the shoulders, all in
   the page's copy; only the drop tells the host, and B puts everything back as it was. */
function weStartMove(M, id) {
  const f = weFind(M, id);
  if (!f) return;
  M.ui.moving = { id, snapshot: JSON.parse(JSON.stringify(M.tabs())), fromTab: f.tab.id, fromIndex: f.index };
  settingsPane = "rows";
  settingsIdx = f.index;
  renderSettings();
}

function weMoveTo(M, index) {
  const f = M.ui.moving && weFind(M, M.ui.moving.id);
  if (!f) return;
  const list = f.tab.items;
  index = Math.max(0, Math.min(index, list.length - 1));
  if (index === f.index) return;
  list.splice(f.index, 1);
  list.splice(index, 0, f.item);
  settingsIdx = index;
  renderSettings();
}

function weMoveStep(M, dir) {
  const f = M.ui.moving && weFind(M, M.ui.moving.id);
  if (!f || f.tab.items.length < 2) return;
  const n = f.tab.items.length;
  weMoveTo(M, (f.index + dir + n) % n);
}

function weMoveToTab(M, dir, toIndex) {
  const f = M.ui.moving && weFind(M, M.ui.moving.id);
  const tabs = M.tabs();
  if (!f || tabs.length < 2) return;
  const from = tabs.indexOf(f.tab);
  const to = toIndex !== undefined ? toIndex : (from + dir + tabs.length) % tabs.length;
  if (to === from) return;
  const dest = tabs[to];
  if ((dest.items || []).length >= M.maxItems()) { toast(`${dest.name} is full: a tab holds ${M.maxItems()}`); return; }
  f.tab.items.splice(f.index, 1);
  dest.items = dest.items || [];
  const at = Math.min(f.index, dest.items.length);
  dest.items.splice(at, 0, f.item);
  M.ui.tab = to;
  settingsIdx = at;
  renderSettings();
  pulse(document.querySelector(".sc-ring"));
}

function weDrop(M) {
  const mv = M.ui.moving;
  if (!mv) return;
  const f = weFind(M, mv.id);
  M.ui.moving = null;
  M.ui.droppedAt = Date.now();
  if (f && (f.tab.id !== mv.fromTab || f.index !== mv.fromIndex)) M.send.move(mv.id, f.tab.id, f.index);
  M.applyPending();
  renderSettings();
}

function weCancelMove(M) {
  const mv = M.ui.moving;
  if (!mv) return;
  M.ui.moving = null;
  M.ui.drag = null;
  M.setTabs(mv.snapshot);
  M.applyPending();
  const tabs = M.tabs();
  M.ui.tab = Math.max(0, tabs.findIndex(t => t.id === mv.fromTab));
  settingsIdx = mv.fromIndex;
  if (weActive(M)) renderSettings();
}

/* ---- dragging with a mouse ---- */

/* The model a drag belongs to, while the button is down. */
let weDragModel = null;

function weDragStart(M, e, id) {
  if (e.button !== 0 || M.ui.moving) return;
  M.ui.drag = { id, x: e.clientX, y: e.clientY, started: false };
  weDragModel = M;
}

window.addEventListener("pointermove", (e) => {
  const M = weDragModel;
  const d = M && M.ui.drag;
  if (!d) return;
  if (!d.started) {
    if (Math.hypot(e.clientX - d.x, e.clientY - d.y) < 8) return;
    d.started = true;
    weStartMove(M, d.id);
  }
  if (!M.ui.moving) { M.ui.drag = null; weDragModel = null; return; }
  // Over a tab: that is where it would go on letting go. Over the wheel: the slot nearest the
  // pointer's angle round the hub.
  document.querySelectorAll(".sc-tab.drop").forEach(x => x.classList.remove("drop"));
  const under = document.elementFromPoint(e.clientX, e.clientY);
  const chip = under && under.closest("[data-we-tab]");
  if (chip) { chip.classList.add("drop"); return; }
  const wheel = document.querySelector(".sc-wheel");
  const f = weFind(M, M.ui.moving.id);
  if (!wheel || !f) return;
  const r = wheel.getBoundingClientRect();
  const x = e.clientX - (r.left + r.width / 2), y = e.clientY - (r.top + r.height / 2);
  if (Math.hypot(x, y) < r.width * 0.12) return;   // the hub: no direction to read
  // The slots are the ring's, the + spoke's included; a move stops at the last item.
  const slots = f.tab.items.length + (weHasAdd(M, f.tab) ? 1 : 0);
  weMoveTo(M, Math.min(stickToSpoke(x, -y, slots), f.tab.items.length - 1));
});

window.addEventListener("pointerup", (e) => {
  const M = weDragModel;
  const d = M && M.ui.drag;
  weDragModel = null;
  if (!d) return;
  M.ui.drag = null;
  if (!d.started || !M.ui.moving) return;
  const under = document.elementFromPoint(e.clientX, e.clientY);
  const chip = under && under.closest("[data-we-tab]");
  document.querySelectorAll(".sc-tab.drop").forEach(x => x.classList.remove("drop"));
  if (chip) weMoveToTab(M, 0, parseInt(chip.dataset.weTab, 10) || 0);
  weDrop(M);
});

/* ---- tabs ---- */

function weTabOptions(M) {
  const tabs = M.tabs();
  const t = weTab(M);
  if (!t) return;
  const i = M.ui.tab;
  const n = (t.items || []).length;
  const items = [
    { label: "Rename this tab", icon: "edit", action: () => openInput("TAB NAME", t.name, v => {
        if (!v) return;
        t.name = v;
        M.send.tabRename(t.id, v);
        renderSettings();
      }) },
  ];
  if (tabs.length < M.maxTabs()) items.push({ label: "New tab", icon: "plus", sub: "Another wheel, after this one", action: () => weNewTab(M, i + 1) });
  if (i > 0) items.push({ label: "Move this tab left", icon: "arrowLeft", action: () => weMoveTab(M, t, i - 1) });
  if (i < tabs.length - 1) items.push({ label: "Move this tab right", icon: "arrowRight", action: () => weMoveTab(M, t, i + 1) });
  if (tabs.length > 1) {
    const words = M.deleteTabText(t, n);
    items.push({
      label: "Delete this tab", icon: "trash", danger: true, sub: words.sub,
      action: () => askConfirm({
        title: `Delete ${t.name}?`, body: words.body,
        yesLabel: "Delete", icon: "trash", danger: true,
        onYes: () => {
          tabs.splice(i, 1);
          M.send.tabRemove(t.id);
          M.ui.tab = Math.max(0, i - 1); settingsIdx = 0;
          renderSettings();
        },
      }),
    });
  }
  items.push(...M.tabOptionsExtra());
  openChoice(`${t.name} · Tab options`, items);
}

function weNewTab(M, index) {
  if (M.tabs().length >= M.maxTabs()) { toast(`Up to ${M.maxTabs()} tabs`); return; }
  openInput("NEW TAB", "", name => {
    if (!name) return;
    const id = M.newTabId();
    const tabs = M.tabs();
    index = Math.max(0, Math.min(index, tabs.length));
    tabs.splice(index, 0, { id, name, items: [] });
    M.send.tabAdd(id, name, index);
    M.ui.tab = index; settingsIdx = 0; settingsPane = "rows";
    renderSettings();
    pulse(document.querySelector(".sc-ring"));
  });
}

function weMoveTab(M, t, index) {
  const tabs = M.tabs();
  const from = tabs.indexOf(t);
  if (from < 0) return;
  tabs.splice(from, 1);
  tabs.splice(index, 0, t);
  M.send.tabMove(t.id, index);
  M.ui.tab = index;
  renderSettings();
}

/* Delete and F2 on a keyboard, on a wheel: remove and rename the item under the highlight. In the
   capture phase only to see them first; anything else is left alone. */
window.addEventListener("keydown", (e) => {
  const M = activeWheelModel();
  if (!M || settingsPane !== "rows" || M.ui.moving) return;
  if (overlayOpen() || overlayMode || e.ctrlKey || e.altKey || e.metaKey) return;
  if (e.key !== "Delete" && e.key !== "F2") return;
  const tab = weTab(M);
  const it = tab && (tab.items || [])[settingsIdx];
  if (!it) return;
  e.preventDefault();
  setInputFamily("keyboard");
  if (e.key === "Delete") M.remove(it);
  else M.rename(it);
}, true);
