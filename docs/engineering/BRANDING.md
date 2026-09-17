# Code Wicket — naming and brand assets

How the product is spelled on every surface, and the assets that carry it. The identifier rules come
first because they are the part that breaks a build.

## Identifier conventions

Full name everywhere -- no bare "wicket" identifiers.

**The rule: PascalCase for .NET identity, `CWKT_` for engine environment variables, kebab-case for
everything else.** There is one lowercase token, `code-wicket`, so a reader who has seen any one form
can derive the others.

| Surface | Form |
|---|---|
| Display name, docs prose, Marketplace | Code Wicket (two words) |
| C# root namespace, VSIX package ID | `CodeWicket` |
| User storage dir | `%APPDATA%\code-wicket`, `%LOCALAPPDATA%\code-wicket` |
| Per-project settings dir | `.code-wicket` |
| MCP server name | `code-wicket` (tools render as `@code-wicket/run_tests`) |
| Breakpoint tag prefix | `code-wicket:` |
| Engine environment variables | `CWKT_` |
| Asset filenames, CI artifacts, lowercase ids | `code-wicket-128.png`, `code-wicket-vsix` |
| GitHub repo, domains | code-wicket |

Informal shortening to "Wicket" in conversation is fine; identifiers never shorten.

The MCP server carries **no `-ide` qualifier**. That catalog is the whole of what the extension
exposes to an agent, so the suffix restated the product's purpose in every tool name the model
reads and every rule the user stores; MCP also names servers for the thing (`github`,
`filesystem`), not thing-plus-category. Room for a second server is no reason to keep it:
`IsOurServer` would need editing for one whatever the product is called. Pre-release builds used
`code-wicket-ide`, which is no longer accepted: a tool rule stored under it no longer matches.

### The separator-less `codewicket` is not a form of this name

It is the one spelling to watch, because it is what anyone types when they are not looking at this
table -- and it had already appeared twice unbidden, in seven activity-log event names and in the
icon asset filenames, neither of which anybody chose. **`RenameResidueTests` now fails the build on
it**, guarded as `codewicket[-/]` so the anchor spares the legitimate PascalCase `CodeWicket`
identity.

Two alternatives were considered and rejected once the project settings directory (`.code-wicket`)
put the whole family up for decision:

- **`CodeWicket` for the user storage dir**, matching the Windows `%APPDATA%` convention and the
  assembly names. Rejected: the precedent reached for is VS Code's `%APPDATA%\Code` + `.vscode`, and
  that is not a case rule at all -- those are two different names, neither derived from the other.
  Adopting a case split means remembering which surface takes which form, and it buys tidiness on a
  folder the user sees about once, via Open Logs.
- **`codewicket` concatenated everywhere**, matching `.claude` / `.kiro` / `.cursor`. Rejected: every
  member of that peer set is a *single-word* product, so none of them is evidence about what a
  two-word name should do. Kebab is also the convention for multi-word MCP servers in the reference
  set (`brave-search`, `sequential-thinking`), which makes `code-wicket` the idiomatic form
  already, and `code-wicket` parses in one glance where `codewicket` needs a beat.

## Name

**Code Wicket.** Dual metaphor:

1. The cricket wicket — three stumps, two bails.
2. The wicket gate — the small, controlled door within a larger gate. Agents don't
   get the castle; they get a deliberate opening with specific capabilities.

Informally shortens to "Wicket" in docs and conversation; "Code" does the
category/search work in formal contexts. Note: Apache Wicket (Java) owns the bare
word in dev tooling — always brand as the full phrase in titles and listings.

## Messaging (layered — each line has one job)

| Slot | Line |
|---|---|
| Lockup tagline (human) | The way in for agents. |
| Marketplace title (category claim / SEO) | Code Wicket — the agent gateway for Visual Studio |
| Description opener | Connect Claude Code, Kiro, or any ACP-enabled agent to Visual Studio 2022 and 2026 — with deep IDE integration: diffs, builds, tests, Roslyn code fixes, and the live debugger. |
| Drumbeat (README header, hero, release notes) | One gateway. Every agent. |

### Two different pages, and the good one is not in the .vsix

The Manage Extensions details page renders the **Marketplace Overview** — full markdown, headings,
images, video — for any extension VS resolves to a listing (the header says *From Visual Studio
Marketplace*, with *View In Browser* beside it). The manifest's `<Description>` is the **fallback**,
shown only where there is no listing behind the install: a local .vsix, or an extension deployed
into an experimental instance. Measured on an installed extension that does have a listing: its
details page renders a full README while its manifest `<Description>` is **15 words of plain text**.
So the Overview (`marketplace/overview.md`) is where the real listing copy goes, and `<Description>`
is a one-line blurb, nothing more.

**The `<Description>` fallback is a ONE-PARAGRAPH slot** — the pane collapses the manifest's blank
lines and offers no heading, list or link, so its only lever is length. Across the 78 manifests VS
18.9 ships the median is **8 words** and the longest 69; ours was 155 and read as a wall. It is now
the opener, one sentence of substance, and the prerequisite. The drumbeat stays out of it — that slot
is the README header, the hero and release notes — and the search terms live in `<Tags>`.

## Icon

Chosen direction: **Concept 4e — "Hinged at the Cursor"**.

- A wicket-gate frame where the crimson I-beam **cursor forms the left side of the
  frame**, with cream lintel and right gatepost, and a cream door centred in the
  opening, skewed so it reads as hinged toward the cursor.
- Story: the door hangs from the editor — access to the codebase is mounted on the
  IDE; the extension is the hinge.
- Bails/frame stay **intact** — in cricket, bails off = out. Never draw a broken
  wicket.
- Drawn on a 128 grid, strokes >= 8 units so it survives 16px. Needed at 128/90/32/16
  (VSIX manifest + Marketplace).

### Final icon SVG (shipping version)

Medium cursor (stem 12), strong skew (10), optically centred (+3.5), solid door. **The SVG below is
the master**; the shipped rasters are `src/CodeWicket.VSExtension/Resources/code-wicket-128.png` (the
VSIX manifest icon) and `code-wicket-200.png` (the Marketplace preview) beside it.

```svg
<svg viewBox="0 0 128 128" xmlns="http://www.w3.org/2000/svg">
  <rect width="128" height="128" fill="#1F3D2B"/>
  <g transform="translate(3.5 0)">
    <g fill="#A6251F">
      <rect x="15" y="22" width="30" height="9" rx="4"/>
      <rect x="24" y="27" width="12" height="80"/>
      <rect x="15" y="99" width="30" height="9" rx="4"/>
    </g>
    <rect x="44" y="24" width="62" height="9" rx="4.5" fill="#F5F0E4"/>
    <rect x="92" y="24" width="14" height="82" rx="7" fill="#F5F0E4"/>
    <g transform="translate(52 42)">
      <polygon points="0,0 24,10 24,62 0,64" fill="#E9E2D0"/>
    </g>
  </g>
</svg>
```

Contrast notes: cream on pitch 10.5:1, on VS dark 12.1:1 (structure carrier);
crimson ~1.7-1.9:1 on both (decorative accent -- acceptable; if the cursor gets
lost on VS dark chrome in practice, brighten to #B8332A for ~2.3:1).

## Palette

| Token | Hex | Use |
|---|---|---|
| Pitch | `#1F3D2B` | Icon ground, dark surfaces |
| Outfield | `#2E5E3F` | Secondary green |
| Whites | `#F5F0E4` | The mark, light backgrounds |
| Leather | `#A6251F` | Single accent — the cursor, links, CTAs |
| Willow | `#3A2E24` | Text on light surfaces |

No gradients, no tech-blue. Check Leather's contrast on VS dark (`#2D2D30`) for in-IDE uses.

## Wordmark & type

- Serif wordmark, a Georgia-class placeholder in a scoreboard-meets-letterpress register. "Code" set
  light, "Wicket" set bold.
- UI/utility text: Segoe UI (native to the VS ecosystem).
