// Prints the preview mock's copy of the action packs (the PACKS block in ui/app.js's mockActions),
// read from ActionPacks in Loungepad/Models/Actions.cs, one action per line:
// [id, name, keys, description, button?, danger?]. Run it after changing a pack and paste the
// output over the block, so the preview shows the same tabs and the same words as the app.
//
//   node tools\mock-action-packs.mjs [app ids, comma separated]
import fs from "node:fs";

const file = new URL("../Loungepad/Models/Actions.cs", import.meta.url);
const src = fs.readFileSync(file, "utf8");
// The packs are written in a C# that is a few rewrites from JavaScript.
let body = src.slice(src.indexOf("private static ActionTab[] Browser"), src.indexOf("public static ActionApp? Find"));
body = body
  .replace(/private static ActionTab\[\] Browser\(string downloads, string privateWindow\) => new\[\]\s*\{/, "const Browser = (downloads, privateWindow) => [")
  .replace(/\};\s*public static IReadOnlyList<ActionApp> All \{ get; \} = new List<ActionApp>\s*\{/, "];\nconst All = [")
  .replace(/\};\s*$/, "];")
  .replace(/Array\.Empty<string>\(\)/g, "[]")
  .replace(/new\[\] \{ ([^}]*) \}/g, "[$1]")
  .replace(/EverywhereId/g, '"everywhere"')
  .replace(/danger: true/g, "null, true");
const A = (id, name, keys, desc, button = null, danger = false) => ({ id, name, keys, desc, button, danger });
const T = (id, name, ...items) => ({ id, name, items });
const App = (id, name, exes, ...tabs) => ({ id, name, exes, tabs: tabs.flat() });
const all = new Function("A", "T", "App", body + "\nreturn All;")(A, T, App);

const want = (process.argv[2] || "everywhere,firefox,chrome,vlc,explorer,spotify").split(",");
const q = (s) => JSON.stringify(s);
const lines = ["  const PACKS = ["];
for (const app of all.filter(a => want.includes(a.id))) {
  lines.push(`    { id: ${q(app.id)}, name: ${q(app.name)}, exes: ${JSON.stringify(app.exes)}, tabs: [`);
  for (const t of app.tabs) {
    lines.push(`      { id: ${q(t.id)}, name: ${q(t.name)}, items: [`);
    for (const a of t.items) {
      const tail = a.danger ? `, ${q(a.button)}, true` : a.button ? `, ${q(a.button)}` : "";
      lines.push(`        [${q(a.id)}, ${q(a.name)}, ${q(a.keys)}, ${q(a.desc)}${tail}],`);
    }
    lines.push("      ] },");
  }
  lines.push("    ] },");
}
lines.push("  ];");
console.log(lines.join("\n"));
