# Shortcuts: the Power Wheel's own wheels

The Power Wheel's Shortcuts spoke used to open a fixed list of six things. It now opens a wheel the
user arranges: tabs of up to eight shortcuts each, laid round the ring the way the action wheel lays
out its spokes (evenly, in order, so adding one moves the others round). Settings → Shortcuts is
where they are arranged, and its editor is the wheel itself.

## What a shortcut can open

| Kind | Target | Started by |
|---|---|---|
| `builtin` | an id in `ShortcutCatalog` | the catalogue: an exe or an `ms-settings:` page, or the host itself (lock, rest, sleep, sign out, restart, shut down) |
| `app` | a Start menu app's id: a packaged app's AUMID, or the shell's id for a desktop program | `IApplicationActivationManager` for a packaged app (it says which process it became), else `shell:AppsFolder\<id>` through the shell |
| `program` | an exe's full path, with `args` and `startIn` | the exe |
| `folder` / `file` | a full path | Explorer / the file's own app |
| `url` | an http or https address | the default browser |
| `game` | a library game's id | the library's own launch, swap question included |
| `command` | a `.bat`, `.cmd`, `.ps1` or `.exe` path, or a command line; `console: "hide"` runs it with no window | cmd, or PowerShell with the execution policy set aside for that one run |

Sign out, restart and shut down always ask first; any shortcut can be set to ask too (the edit sheet
offers it for commands). The host enforces it: `shortcutRun` without `confirmed` for one of those
brings Loungepad up and pushes `shortcutAsk`, so a button press on the desktop can never power the
PC off unseen.

## Storage

`%APPDATA%\Loungepad\shortcuts.json` (`ShortcutsFileData`): tabs, each with its shortcuts. Nothing is
merged over it, unlike the action packs: the arrangement is the point. A missing file is seeded with
the six the wheel had before (`ShortcutCatalog.Defaults`), and "Restore default shortcuts" goes back
to exactly that. Written to a temporary file and moved over the old one. A file that does not parse
is kept beside it as `shortcuts.json.broken`.

It is changed only through the `shortcut*` bridge commands and is not part of `CopySettings`, so a
settings save or "Restore default settings" never touches it.

Every value is checked by `ShortcutService.Clean` before it is stored, whether it came from the page
or from a hand edit: a kind the file knows, an http(s) address, full paths, a catalogue id, no
control characters, a chosen picture's name in its own shape (`ic-….png`, never a path), a button in the launcher's own spelling and never
A, B or Guide on its own. A shortcut that fails is dropped rather than kept.

## The bridge

Page → host: `shortcutRun {id, confirmed}`, `shortcutSave {tabId, item, index?}`,
`shortcutRemove {id}`, `shortcutMove {id, tabId, index}`, `shortcutTabAdd {id, name, index}`,
`shortcutTabRename`, `shortcutTabMove`, `shortcutTabRemove`, `shortcutsReset`, `shortcutApps`,
`shortcutBrowse {kind}`.

Host → page: `shortcuts` (everything, with the icons found so far, the catalogue and the folders
worth a shortcut; sent on `ready` and after every change, never in a state push), `shortcutApps`
then `shortcutAppIcons` (forty at a time), `shortcutPicked` (a file dialog's answer, a null path for
cancel), `shortcutAsk`.

The page edits its own copy first and the host's push replaces it. A push that arrives while a
shortcut is picked up is held until it is put down (`scUi.pending`).

## The wheel

`overlay-shortcuts`, opened from the Power Wheel. It always opens on the first tab. LB and RB turn
the tab, the stick or the D-pad picks a spoke, A opens it, B goes back to the Power Wheel. A
shortcut's own button opens it from the wheel too, as an action's does. A game while another one
runs, and anything that asks first, asks over the wheel: the overlay sits before `overlay-confirm`
in `index.html` for exactly that.

## The editor

Settings → Shortcuts draws the tab strip, the ring and a details card into Settings' scroller. The
editor is `wheeledit.js`, shared with Settings → Actions (each app's wheel of actions is arranged
the same way): `scModel` in shortcuts.js says what is on the wheel, what a spoke and the card show,
and which bridge command each edit sends; the engine does the drawing, the moving, the dragging, the
strip and the tab options. The spokes are its focusables (`data-row-index`), so hover, the pane
logic and the highlight work as they do for rows. The spokes are kept by id and moved rather than
rebuilt, so a moved shortcut slides to its new place.

| | Pad | Keyboard | Mouse |
|---|---|---|---|
| Pick a spoke | D-pad, round the ring | arrows | hover |
| Edit, or add on the + | A | Enter | click |
| Move | Y, then the D-pad; LB / RB carry it to another tab; A drops, B puts it back | Y, arrows, Enter / Esc | drag onto the ring or a tab |
| Tab options | X | X | the ⋯ on the active tab |
| Turn the tab | LB / RB | [ / ] | click a tab |
| Remove | the edit sheet | Delete | the edit sheet |
| Rename | the edit sheet | F2 | the edit sheet |
| Back to the categories | B | Esc | click a category |

Every control of the tab strip (the tabs, the ⋯, + New tab, the shoulders) is a focusable that the
D-pad never visits. In the launcher a pad's A is the page's A, never a click, and the joystick mouse
only moves the pointer, so a control the pointer cannot make the highlight cannot be pressed from
the pad at all: that was + New tab working with a real mouse and not with the stick.

The D-pad goes round the ring on all four directions, as it does on the Power Wheel, so Left does
not cross to the categories here; B does.

The edit sheet is the form sheet (`openForm`) over the wheel. `overlay-choice` comes before
`overlay-form` in the document, so a list opened from the sheet (what it opens, the icon, another
tab) would be drawn under it: the sheet steps aside while the list is up and comes back on the same
row (`scFromForm`).

## Icons

What a spoke draws, first that applies: a picture the user chose; a built-in's own glyph; a game's
art; the icon the host found (the shell's for an app, program, folder, file or script; the site's
own for a website); a website's first letter; the kind's glyph.

The edit sheet's Icon row has two choices: Automatic, or Choose an icon… (Windows' dialog on the
host, `shortcutIconPick`). Any picture WIC reads (PNG, JPEG, GIF, BMP, WebP, the largest picture in an
.ico) or a program's own icon is made into a PNG of at most 128 pixels a side
(`ShortcutIcons.FromFile`) and kept in `shortcut-icons` beside shortcuts.json as `ic-<id>-<random>.png`.

A website's icon is asked of the site itself and nobody else (`ShortcutIcons.FetchFavicon`): its
`<link rel="icon">` and apple-touch-icon pictures, biggest first, then `/favicon.ico`; SVG is skipped,
since WIC cannot draw it. It is kept as `fav-<hash of the host>.png`, one per site, so a site is asked
once; one with nothing usable is asked again on the next start. Websites are looked up after
everything on disk, so a slow site never holds up the other icons.

`ShortcutIcons.Prune` runs after every change and deletes the `ic-` and `fav-` files no shortcut uses
any more, and nothing else in the folder.

## Bringing the window forward

`WindowService.ShortcutWindow` says how the window a shortcut opened is recognised: the process that
was started, its exe (`ActionService.ExeOf` looks inside a Store app's frame), a window class, a
fresh `ApplicationFrameHost` frame, or a new window of any process that started after the launch.
`AllowExisting` is for programs that bring their one window forward instead of opening another
(Settings, the mixer, browsers, most Store apps); Explorer always opens a new one. The six old
shortcuts keep exactly the rules they had. Something started through the shell names no process, so
every process is allowed the foreground for that moment (`ASFW_ANY`) while Loungepad still has it.

## Buttons

A shortcut's button goes to `ActionService` with the actions' own (`SetShortcutBindings`). It answers
in every app, at Everywhere's rank: an app's own action on the same button wins while that app is in
front, and a shortcut and an Everywhere action never share one (both editors refuse it when
recording). It fires only where action buttons do: on the desktop, with Loungepad and games out of
the way.

## Testing

- The preview's mock (`mockShortcuts` in app.js) has three tabs, a Start menu and the file dialogs.
  P opens the Power Wheel.
- A scratch console project over `Loungepad.dll` by reflection, with the real log held shut, checks
  the file, the cleaning, the moves and tabs, starting hidden commands and scripts, the buttons
  through `ActionService.Evaluate` with a fake clock and foreground, and this PC's Start menu (read
  only), the chosen pictures and their tidying, and live favicons: 83 checks, Oct 13 2026.
- A windowed Debug build driven only over CDP (`--remote-debugging-port`): the payload on `ready`,
  the Start menu (246 apps here, icons in batches of 40), a save and a remove through the bridge's
  JSON, and the Volume Mixer opened from the wheel's own send, parked behind it and in front.
- Never start lock, sign out, restart or shut down from a test.
