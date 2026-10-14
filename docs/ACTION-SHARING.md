# Sharing actions: copies, files and community layouts

A design (Oct 13 2026). **All three phases are built**: copying between apps, files, and community
layouts. What is left for community layouts is outside the code: deploying the stats worker and
branch protection on `loungepad-addons` (see [Phases](#phases)). The most requested thing about actions is bringing them in
from somewhere else: copying one app's wheel to another (Chrome's to Thorium, or to any other
Chromium browser), and using a wheel somebody else has already built for a program. This is how
that works, and what stops it from being a way into somebody's PC.

- [What is shared, and what never is](#what-is-shared-and-what-never-is)
- [The format](#the-format)
- [Importing: one way in](#importing-one-way-in)
- [Copying between apps](#copying-between-apps)
- [Files](#files)
- [Community layouts](#community-layouts)
- [Duplicates](#duplicates)
- [Threats](#threats)
- [Phases](#phases)
- [Still open](#still-open)

## What is shared, and what never is

An action is data: a name, a sentence, one chord in the `ShortcutKeys` grammar (modifiers, then one
key), and maybe a button. It holds no code, no address, no path, and it cannot type text: the most
it can do is press one chord in the program it belongs to. That is why it can be shared at all.

What is never shared:

- **The Power Wheel's shortcuts.** They start programs, open addresses and run `.ps1` and `.cmd`
  files. A shared one would be a download that runs; they stay on the PC that made them.
- **Everywhere's wheel.** Its actions fire in every program, and its buttons answer in every
  program. It is not a target for a file or a community layout, and not a source or target for a
  copy.
- **Anything about the PC.** An export carries no `Path` (the program's full path has the Windows
  user name in it), no icon, and no ids from `actions.json`.

Two lines that stay drawn unless the threat review below is done again from the start: **no "type
this text" actions and no multi-step macros in anything shared.** One chord per action is what keeps
a layout harmless.

## The format

One self-contained layout, resolved: every tab and every action written out, not the difference
from a pack that `actions.json` keeps. A file made by any launcher version reads the same in any
other, whatever the packs looked like when it was written.

```json
{
  "format": "loungepad-actions/1",
  "app": { "name": "Thorium", "exes": ["thorium"], "product": "Thorium" },
  "title": "Thorium from the sofa",
  "summary": "Browsing with one hand on the pad: tabs on the shoulders, zoom on the triggers.",
  "author": "someone",
  "version": "1.0.0",
  "tabs": [
    { "name": "Browse", "actions": [
      { "name": "New tab", "keys": "Ctrl+T", "desc": "Opens a new tab.", "button": "Y" },
      { "name": "Close tab", "keys": "Ctrl+W" }
    ]}
  ]
}
```

| field | rule |
|---|---|
| `format` | `loungepad-actions/<major>`. A major the launcher does not know is refused whole |
| `app.name` | up to 48 characters |
| `app.exes` | 1 to 8 exe names, lower case, without `.exe`, as `ActionService.ExeName` writes them |
| `app.product` | optional: the program's own name from its exe's version information, up to 64 |
| `title`, `author` | up to 48 and 40. `summary` up to 120. All optional in a file, required in the catalogue |
| `version` | `major.minor.patch`, required in the catalogue only |
| `tabs` | 1 to 12 (`ActionPacks.MaxTabs`); a tab's `name` up to 24 |
| `tabs[].actions` | 1 to 8 each (`ActionPacks.MaxItems`): `name` (up to 48) and `keys` required, `desc` (up to 120) and `button` optional |

The whole file is at most 64 KB and is read with a depth limit. Fields the reader does not know are
ignored. There are no ids: they are made on import, for the app being imported into. There is no
`danger` flag either: the launcher decides that itself (see [Threats](#threats)).

## Importing: one way in

A copy, a file and a community layout all become this format, and all go in through one method,
`ActionService.Import`. It runs every value through the cleaners `Update` already uses (`ValidId`,
`Truncate`, `ShortcutKeys.IsValid` and `Normalize`, `CleanButton`, `CleanDesc`) plus one text rule
for anything that came from outside: no control or format characters (bidi overrides, zero-width
characters), and no `<` or `>`. So an import cannot store anything a hand edit could not, and a
hand edit has never been able to store markup.

**The host holds the layout; the page only sees it.** The host reads and cleans the layout, keeps
it under a token, and pushes a preview. The page shows the preview and answers with the token and
the choices. A layout never crosses the bridge from the page to the host.

```
page → host   actionsImportPreview { source, target? }
              source: { kind: "app", appId, tabId? } | { kind: "file" } | { kind: "catalogue", id }
host → page   actionsImportPreview { token, target, madeFor, tabs, buttons, warnings, fits }
page → host   actionsImport { token, mode: "add" | "replace", buttons: true | false }
host → page   actions   (the usual push)
page → host   actionsUndoImport { appId }
```

A token is good for one import and ten minutes.

### What an import does to the wheel

**In one app, the same chord is the same action.** This is the whole duplicate rule on the PC:

- An incoming action whose chord is already on the wheel is skipped, and the name you gave it stays.
- One whose chord the app's pack knows, for a pack action not on the wheel (taken off, or every one
  of them in replace mode), **is that pack action, with the pack's own name and description**, not
  the layout's. A layout can arrange an app's own actions but never relabel them: Firefox's "Back"
  (Alt+Left) copied into VLC arrives as VLC's own "Skip back 10 s", and a "Next tab" that is really
  Ctrl+W cannot hide behind a pack action's chord. The pack's id comes with it, so a better sentence
  in a later build still reaches it.
- Anything else is a new action of yours, with an id made from its name, unique in the app and
  never one of the pack's (a custom action under a pack's id would be read as a change to it).
- A chord that appears twice in the incoming layout is taken once.

Two ways to apply it, picked in the preview:

- **Add as new tabs** (the default). Each incoming tab becomes a tab after yours, holding what was
  not skipped; a tab left with nothing in it is not made. A name you already use gets " 2". Tabs
  beyond the twelfth do not fit, and the preview says so before anything is written.
- **Replace my wheel.** The layout's tabs become the app's. For a pack this is an arrangement:
  pack actions it does not mention are taken off (`Removed`) and offered again by the + spoke, and
  the ones it does mention are stored only as far as their button differs. For an app of your own
  the entry is replaced. A wheel with nothing on it is always replaced, and the sheet then just says
  Add.

### Buttons: off unless asked for

A button fires without the wheel being open, so a layout's buttons come in only when the preview's
**Bring the buttons too** is switched on (the user's call, Oct 13 2026). Off, what is copied comes
without buttons and the app's own actions keep the ones they come with. Either way a button is
dropped, and the action's line says why, when:

- it is A, B or Guide on its own (`IsReservedButton`);
- another action of this app already has it (your binding wins);
- it is the menu combo, the screenshot combo, or the keyboard toggle on Press;
- a Power Wheel shortcut or an Everywhere action already answers to it.

The host decides all of these (`ActionService.ButtonProblem`), so what the preview shows is what the
import does; it is stricter than editing by hand, where a binding over Everywhere's is only a note.
An import never takes a button from anything the user set up.

### The import screen

A panel over the wheel editor (`acImportHtml`; `openPanel` in app.js), laid out top to bottom the
way the user asked (Oct 13 2026), with one rule: **only what can be pressed is a button**. The
wheel is a picture, the explanations are text, and a disabled import is a dashed line of text, not
a button that does nothing.

1. **Who made it and its numbers**: "Bring actions into Firefox", the title, then by whom, its
   likes, how often it was brought in, and its size (a copy says it is another app's wheel, a file
   that it is a file). A community layout has a **Like** button here.
2. **What its maker said** (the summary), and the notes: made for another program, actions the
   cleaner left out, a product name that disagrees.
3. **The wheel as it would come in**, a tab at a time: tab chips (buttons only when there is more
   than one tab; LB and RB turn them from anywhere), and the ring drawn flat, each spoke's name, its
   chord and the button it would have, under a tag saying what happens to it: NEW, BUILT IN (the
   app's own, under its own name), ON YOUR WHEEL, REPEATED, NO ROOM, and WINDOWS for a Win chord.
   Skipped spokes are faded, a closing chord is red, a button left off is struck through. Beside
   it, what happens to that tab in sentences: which are new, which are the app's own, which are
   there already, which buttons stay off and why, what is red and what acts on Windows.
4. **The two choices**: How (Add as new tabs | Replace my wheel, a two-button switch; on an empty
   wheel a sentence instead) and Controller buttons (a switch: Bring its button mappings), each
   with a line saying what it means now, including how many buttons are for actions already on the
   wheel, which only Replace takes.
5. **Cancel**, and the import itself as the one filled button: "Add 2 actions", or red "Replace
   with 6 actions". The highlight starts on it.

The host sends the plan all four ways (add or replace, with or without the layout's buttons), so a
choice on the screen redraws at once; the import plans again against the wheel when it happens.

### Undo

The app's entry from before the import is kept on it (`ActionAppFile.BeforeImport`, with
`ImportedFrom` and `ImportedAt`). **Undo the import** is on the options card (Y) until the next
edit of that app: every edit goes through `Commit`, which drops it. One level only: two imports in a
row and undo takes back the second. An import into an untouched pack had no entry, and undo puts it
back to having none (keeping the pin and the path); `Tidy` never removes an entry that still holds
an undo, even when the import left it the pack's own.

## Copying between apps

Copies, never links (the user's call, Oct 13 2026). A copy is a snapshot of the source wheel:
editing Thorium never touches Chrome. The cost is that a later improvement to Chrome's pack does
not reach the copy.

Where it is offered: **Y, Options**, on every app's wheel but Everywhere's. The options card has a
part of its own for bringing actions in, "Bring actions into <app>", three tiles side by side:
**Community layouts**, **Another app's wheel** and **A file**, so the ways in are never mistaken for
something done to the tab (the user's report, Oct 13 2026: the tab's options and the import's were
one list, and confusing). On an empty wheel that part comes first, and the wheel's card says so.

It asks which app (any in the grid with something on its wheel, but not this one and not
Everywhere), then the whole wheel or one tab, then shows the same preview as any import.
Copying between two browser packs mostly reports "already on your wheel". That is the duplicate rule
doing its job, not an error.

## Files

- **Save to a file** on the options card, under the app's own (any app with a sendable action, not Everywhere).
  Windows' Save dialog, defaulting to `<App name>.actions.json` in Documents (`ActionLayouts.FileName`
  leaves out what a file name cannot hold; a name typed without `.json` gets `.actions.json`). It
  writes the layout through `Clean` first, so what is written is what an import reads, with `title`
  set to the app's name and `product` read from the exe's version information when the path is
  known (`ActionService.ProgramPath`, `ProductName`). No path, no icon, no ids, no author. Actions
  with no shortcut are left out, and the toast says so.
- **A file**, the third tile under "Bring actions into <app>": Windows' Open dialog, then the
  preview into that app, whatever program the file was made for. The screen says so: this is how a
  layout reaches a program nobody has made one for.
- **A file of actions…** in the grid's Add an app list: the preview goes into the app the file's
  `exes` name. When the grid does not have it (no such app, or a pack whose program was not found),
  the page asks "Add Thorium?" (`actionsImportAddApp`), the host adds it as Add an app would and
  then previews. A file that names no program is refused there with the way round: open the
  program's wheel, then Y, A file.

Reading (`ActionLayouts.ReadFile` / `Parse`): 64 KB at most, checked on the file's size before a
byte is read; a UTF-8 byte-order mark, comments and trailing commas are tolerated; it must be a JSON
object with a string `format`, or it is somebody else's file however close its shape; nesting past
16 is refused; any field of the wrong kind refuses the whole file. Every refusal is a toast naming
the file and saying why. Writing (`ToJson`): camelCase, nothing for an empty field, text as itself
(`Tabs & windows`, not `&`), since the file is read in editors and on GitHub.

The file's `product` is checked when the file is for the program it goes into: when the exe on this
PC names itself differently (`SameProduct`: letters and digits, either inside the other, so "Brave
Browser" is "Brave"), the sheet says which two names disagree. A different answer is usually a
different program under the same exe name.

The sheet's title is "Import <title>" for a file ("Copy from <app>" for a copy), its summary and
author are a note under the switches, and the undo says "Imported from <file name>".

A file is also how a layout is submitted: the exported file, given a `summary`, `author` and
`version`, is exactly what goes into the catalogue.

## Community layouts

### Where they live

In **`loungepad-addons`**, beside the themes and extensions, as a third kind. It uses what
already exists:

- the public review of every pull request;
- `index.json` with hashes and commit-pinned `base` URLs;
- `AddonService`'s download-and-verify;
- the stats service for downloads and likes.

It also keeps the full history and needs no moderation backend. A merge is the release, as for add-ons.

```
loungepad-addons/
  actions/<id>/layout.json     the layout, in the format above
  actions/<id>/README.md       optional, for people browsing the repository (never downloaded)
  actions/LICENSE              CC0 1.0 for everything in actions/ (the user's call, Oct 13 2026)
```

`<id>` follows the add-ons' rule and, by convention, starts with the program: `thorium-sofa`,
`obs-streaming`. The repository's README says how to send one in: export from the launcher, add a
summary, author and version, run the checks, open a pull request. Submitting dedicates the layout
to the public domain (CC0), so a good one can be folded into the packs Loungepad ships; the notice
covers `actions/` only, not the themes and extensions.

Submissions are **pull requests only, for now** (the user's call, Oct 13 2026). Submitting from
inside the launcher, where the service would open the pull request with a GitHub App token, is
deferred until the pull request turns out to be what stops people. It would add a secret, a spam
surface and a queue in D1. The [threats](#threats) for it are written down below for when it comes.

### The index entry

`build-index.mjs` writes one per folder:

```json
{
  "id": "thorium-sofa", "kind": "actions", "name": "Thorium from the sofa", "version": "1.0.0",
  "summary": "…", "author": "someone", "homepage": "…/tree/main/actions/thorium-sofa",
  "minLauncher": "1.10.0",
  "app": { "name": "Thorium", "exes": ["thorium", "chromium"] },
  "counts": { "tabs": 2, "actions": 8, "buttons": 5 },
  "fingerprint": "<sha-256, see Duplicates>",
  "base": "https://raw.githubusercontent.com/sukumar-v/loungepad-addons/main/actions/thorium-sofa/",
  "files": [{ "path": "layout.json", "sha256": "…", "size": 870 }]
}
```

`minLauncher` is `1.10.0` (`ACTIONS_MIN_LAUNCHER` in `tools/actions-lib.mjs`) unless the layout
asks for more. **Launchers before 1.10 skip the entry safely**: their `AddonService.CheckEntry`
refuses an unknown kind entry by entry ("bad kind", one log line) and keeps reading. So layouts can
be published before the launcher that reads them is out.

In the launcher (`AddonService`), `AddonKind.Actions` is a kind the index may hold
(`AddonKind.IsValid`) but not one Settings → Add-ons lists or installs (`IsInstallable`):
`Describe` leaves it out, `InstallFromCatalogueAsync` refuses it. `CheckEntry` holds an entry's
title, summary and author to the text rule and its exes to exe names, and refuses one that names no
program; the list is `LayoutsFor(exes)`. A layout is never installed as a folder:
`DownloadLayoutAsync` fetches `layout.json` (64 KB at most), checks it against the hash in the
index, refuses an entry that needs a newer launcher before fetching anything, and hands the bytes
back; the bridge reads them with `ActionLayouts.Parse`, cleans them, and previews as for a file. A
download is counted (`LayoutImported`, the `actions:<id>` key) once an import from the catalogue has
been applied, as an add-on's is once its hashes pass. The stats worker's `KEY` pattern takes
`actions`, and it only counts keys the index lists.

### In the launcher

Layouts belong to a program, so they are found where the program's wheel is, not under Add-ons:

- **Community layouts**, the first tile under "Bring actions into <app>" on the options card (Y).
  The tile counts them from the cached list (`communityLayouts` on each app in the actions
  payload), so the card costs no request. Opening it shows "Reading the list…", asks the host
  (`actionsCatalogue`), which reads the index if it is stale and the counts if they are, and answers
  with every layout whose `exes` include one of the app's: title, author, size, hearts and how often
  it was brought in. One that needs a newer launcher says so and does not open. "Read the list
  again" forces a read.
- A opens the [import screen](#the-import-screen), with the layout's author, likes and downloads in
  its heading and a **Like** button there: the heart is kept in `addons.json` as an add-on's is, and
  the service hears only +1 or −1 (the existing `addonLike` command).
- With the layout's buttons on in Add mode, the screen says how many of its buttons are for actions
  already on the wheel (which adding leaves as they are) and that Replace takes them: a layout whose
  point is its buttons would otherwise look like it brings almost nothing.
- Later: a tile on the Actions grid for **layouts for programs on this PC**. This checks the
  catalogue's exes against the same App Paths and uninstall entries `Detect` already reads.

### What CI checks on every pull request

A new `tools/check-actions.mjs`, run by the existing workflow beside `build-index.mjs --check`:

- the format and every limit above, and every text rule: no control or format characters, no `<`
  or `>`, no `://` or `www.` (descriptions are not advertising);
- every `keys` parses with the launcher's grammar, and every `button` is in its spelling;
- **no Win chord** (they act on Windows, not the program), no A, B or Guide alone, no chord
  twice, no button twice;
- `app.exes` are not generic: `launcher`, `app`, `game`, `main`, `start`, `setup`, `update`,
  `updater`, `client`, `helper`, `host`, `java`, `javaw`, `python`, `pythonw`, `node`,
  `electron`, `cmd`, `powershell`, `pwsh`, `rundll32`, `conhost`, `applicationframehost`, and
  whatever else turns up. A layout for "launcher.exe" would show up on every game launcher;
- the [duplicate](#duplicates) rules.

**The grammar has two copies, kept in step by one file.** CI is JavaScript (`tools/actions-lib.mjs`:
the parts, the normal form, buttons, the fingerprint) and `ShortcutKeys` is C#.
`tests/shortcut-keys-vectors.json` in Loungepad holds 45 chords with the launcher's normal form (or
`null` for refused) and one layout with its fingerprint. The launcher's harness checks the C#
against it, and `check-actions.mjs` checks the JavaScript against it before anything else, failing
with every chord it reads differently. Punctuation keys are looked up on the keyboard layout when
the chord is pressed (`VkKeyScanW`), so CI accepts any single printable ASCII punctuation key, and
the launcher checks again on its own layout.

**The launcher's data is a copy in the repository, not fetched**, a change from the first design.
`tools/launcher-data.json` holds the key names, the bundled packs (for the "adds nothing" rule) and
the vectors, written by `node tools/action-data.mjs > ../loungepad-addons/tools/launcher-data.json`
in Loungepad (which `mock-action-packs.mjs` now reads its packs through). Fetching Loungepad's
`main` in CI would make every pull request's checks depend on the network and on whatever the
launcher's branch says that day; a copy is reviewed like any other file. It has to be refreshed
when the packs, the key names or the vectors change, which the README says.

The workflow (`.github/workflows/index.yml`): `build-index.mjs --check`, then
`test-check-actions.mjs` (44 cases: every refusal, the duplicate rules, a drifted rule), then
`check-actions.mjs --base "origin/$BASE_REF"` with the whole history checked out. It also runs when
a label is added or taken off, and `ALLOW_OVERLAP` is set from the `not-a-duplicate` label, which
only people who can label the pull request can set. The base branch reaches the script through an
environment variable, not written into it.

Branch protection on `main` must **require a branch to be up to date before merging**. Two layouts
for one program opened at the same time each pass against `main` alone. Forcing the second to
rebase after the first merges runs its checks again against the first. This is a repository
setting, not yet set.

## Duplicates

**A few layouts per program, and each has to differ** (the user's call, Oct 13 2026). Several
are allowed: Photoshop for painting is not Photoshop for photographs. A layout that is mostly
another is refused, and the way forward is a pull request improving the one that exists.

- **The fingerprint** is the SHA-256 of the exes, sorted, then each tab's chords in order, in their
  normal form. Names, descriptions and buttons are left out: rewording a layout does not make it a
  new one. The same fingerprint as a layout already in the catalogue fails.
- **Too close:** for every layout sharing an exe, the overlap of the two sets of chords (shared
  over all, Jaccard). At 0.8 or more the check fails, naming the other layout. This also catches
  the pack's chords rearranged a second time. A maintainer can override it with the
  `not-a-duplicate` label, for the rare layout whose point is its buttons.
- **Adds nothing:** a layout whose every exe belongs to one bundled pack, with the pack's chords in
  the pack's tabs and order, fails. The pack is already on everyone's wheel. The same chords for a
  program the pack does not cover pass.
- **Only what a pull request changed is compared** (`--base`), each against every other layout. A
  check of every pair would fail every later pull request on two layouts a maintainer once allowed
  with the label. `--all` compares everything, for a look by hand.

The same chord set for a *different* program is not a duplicate. Chrome's layout put up for Thorium
is useful to a Thorium user. Better still, a layout can list several exes (`chrome`, `thorium`,
`chromium`) and be offered to all of them.

## Threats

| Threat | Example | Defence |
|---|---|---|
| Markup in a name, reaching the page | `<img src=x onerror=…>` as an action name; the page is the trust root and can post `shortcutRun` for a script shortcut | Three layers. CI refuses it. The host's text rule strips it. The page escapes the names in every place checked so far (the spokes, the card, the pager, the Settings rows, and keycaps through `svgText`), and the CSP refuses inline handlers. Part of the work is a pass over every place an imported string is drawn, including the new preview |
| A label that lies | "Next tab" that is Ctrl+W; "Refresh" in File Explorer that is Shift+Delete | The preview and the card draw each action's real chord, from its keys and never from its name. A chord the app's pack knows always arrives under the pack's own name. Any action Loungepad did not ship is drawn red when its chord closes, quits or deletes for good (`ShortcutKeys.IsDestructive`: Alt+F4, Ctrl+F4, Ctrl+W, Ctrl+Q and their Shift forms, Shift+Delete; not plain Delete, an editing key), and so is a pack action given such a chord; the packs' own keep the pack's word, so Firefox's Close tab is not red and a copy of it in Thorium is. Review reads every chord against its name |
| Reaching past the program | Win+R, Win+X, Win+L in a "Spotify" layout | CI refuses Win chords. A file may carry one, from somebody's own wheel, and the preview warns that it acts on Windows. A chord cannot type, so Win+R opens a box and nothing more |
| A button taken over | X bound to Alt+F4 in a browser | Buttons are off unless asked for, and dropped on every conflict, as above |
| A layout for the wrong program | A layout for `code` that is not VS Code; one for `launcher` | CI refuses generic exe names. `product` is compared with the exe's own version information, and a mismatch is a warning in the preview |
| A private path in an export | `C:\Users\<name>\AppData\…` | An export writes no path, no icon and no local ids; the author is whatever the person types |
| A file built to break the reader | 50 MB, nesting a thousand deep, ten thousand actions | 64 KB checked on the file's size before reading, a depth of 16, the tab and action limits, and a `format` it must declare. Any error rejects the whole file and nothing is written |
| A download swapped on the way | A different `layout.json` at the URL | The index's SHA-256, checked before the bytes are read, as for add-ons |
| The repository itself | A bad merge, a stolen maintainer account | The same trust the add-ons and the launcher's own releases rest on: two-factor sign-in, branch protection, and review. What limits the damage is the format: the worst a layout can be is a chord in one program |
| Spam pull requests | A hundred junk layouts | GitHub's own limits for now; CI fails them without a human looking |
| Inflated counts | A script liking one layout | As for add-ons: one change per address per layout per day, salted and forgotten. Accepted |

For submissions from inside the launcher, when they come: the service runs the same checks before
storing anything; the fingerprint is a unique column, so a duplicate is refused at the door; there
is a daily cap per address through the add-ons' salted marks; the size cap is 64 KB; the GitHub App
can only open pull requests on `loungepad-addons`; and nothing is published without a merge.

## Phases

1. **Import and copy. Done (Oct 13 2026).** `Models/ActionLayout.cs` (the format, `Clean`, `Text`,
   `Fingerprint`, `Slug`); `ShortcutKeys.Normalize`, `HasWin`, `IsDestructive`;
   `ActionService.Hold`, `Plan`, `Import`, `UndoImport`, `ImportState`; the bridge's
   `actionsImportPreview`, `actionsImport`, `actionsUndoImport`, and `canUndoImport` /
   `importedFrom` on each app in the actions payload; the copy flow and the sheet in actions.js, and
   the preview mock's cut-down planner. Checked with a scratch harness over Loungepad.dll (90
   checks: the text rule, the cleaner, the fingerprint, every merge rule, the buttons, room, ids,
   replace, undo, a reload, the real actions.json untouched) and in the preview, by script and by
   key presses (X, the lists, Enter to add). Not yet run in the app itself.
2. **Files. Done (Oct 13 2026).** `ActionLayouts.Parse`, `ReadFile`, `ToJson`, `FileName`;
   `ActionService.ProgramPath`, `ProductName`, `SameProduct`; the bridge's `actionsExport`,
   `actionsImportFile`, `actionsImportAddApp` (the held file waits under a token while the page
   asks); "Import from a file…", "Export to a file…" and the grid's "A file of actions…" on the page;
   the preview mock's made-up file ("Thorium from the sofa", with a Win chord, buttons and a
   left-out action). The harness grew to 116 checks: the file's spelling, a read back to the same
   fingerprint, the byte-order mark, comments, every refusal, the size limit on a real file, a
   hostile file's text, file names, a real exe's product name, the product match. In the preview,
   both routes: the grid asking to add Thorium then previewing into its new wheel, and a Thorium
   file brought into Firefox. The dialogs themselves only exist in the app and are not yet run.
3. **Community layouts. Built (Oct 13 2026).** In `loungepad-addons`: `actions/LICENSE` (CC0),
   `tools/actions-lib.mjs`, `check-actions.mjs`, `test-check-actions.mjs` (44 cases),
   `launcher-data.json`, the layout entries in `build-index.mjs`, the workflow, and the README's
   section on sending one in. In Loungepad: `tools/action-data.mjs`,
   `tests/shortcut-keys-vectors.json`, `AddonKind.Actions`, `CatalogueEntry.App` / `Counts` /
   `Fingerprint`, `AddonService.LayoutsFor` / `DownloadLayoutAsync` / `LayoutImported` /
   `CountsOf`, the bridge's `actionsCatalogue` and the `catalogue` source, the list and the Like row
   on the page, the preview mock's community list; the stats worker's `actions` key (35 harness
   checks). Checked: the C# against the vectors (45 chords and the fingerprint, in the import
   harness, now 118 checks); a scratch copy of the add-ons repository with one real layout, run
   through `check-actions` and `build-index`, served from loopback, and read by `AddonService` in a
   catalogue harness (18 checks: the entry, kept out of Add-ons and out of installs, listed for both
   of its exes, downloaded and verified, **the C# fingerprint of what arrived equal to the one the
   JavaScript wrote**, the like kept, and a tampered file, an entry naming no program, one for a
   newer launcher and one with markup and a path all refused or cleaned); the preview's list,
   sheet, like and import. **Not done, and not code:** deploying the stats worker (`npx wrangler
   deploy` in `addons-stats/`; until then a layout's like and download answer 400 and only the
   heart on this PC moves), branch protection on `loungepad-addons`, and committing both
   repositories. The worker must be deployed before the first layout is merged.
4. **Later.** An update for an imported layout, offered only while the app is untouched since the
   import (its fingerprint is still the imported one), so it never has to merge anything. Layouts
   for programs on this PC. Submitting from the launcher, if it is ever needed.

## Still open

- **The themes and extensions in `loungepad-addons` still have no licence.** `actions/` is CC0; the
  rest needs its own decision before outside themes or extensions are merged.
- The file extension is `.actions.json` for now: readable on GitHub and in any editor. A name of its
  own could be opened by double-click later, but a double-click that imports is a front door of its
  own, and would need the same threat review.
- Whether the catalogue also gets a tab under Settings → Add-ons for browsing. The design above
  keeps it with the program's wheel only.
