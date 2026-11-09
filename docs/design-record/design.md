# HoardPad — User-Facing Design (Phase 6)

> **Note:** This is the internal design record written during development, published as-is for transparency. It cites internal working files (the original project brief, brand guide, review reports, spike results and project rules files) that are not included in this repository; those citations are kept for traceability.

| Field | Value |
|---|---|
| **Project** | HoardPad |
| **Document** | `docs/design.md` — Phase-6 user-facing UX design (screens, flows, interaction states) the Phase-8 `/code` build implements against |
| **Status** | Draft v3 — authored 2026-06-20; v2 folded the completed Phase-6 four-specialist review (reports at `docs/review-phase6/`, all 5 blockers + all should-fix resolved). **v3 (2026-06-20): ODQ-11 RESOLVED — nested `.hoard` checklists (completion-lock model) authored into §5 (anatomy/operations/model/display-modes/first-run) + §8.1/§8.3 (menu + keymap) + §9.3 (states) + §10 (tree ARIA),** per the decided plan `docs/odq11-nested-checklists-plan.md` (D1–D11) and the PRD v2 schema (FR-41/FR-42, §9.1 schemaVersion 2). Palette-agnostic throughout (Phase 7 `brand.md` owns the values). |
| **Owner** | The maintainer |
| **Realizes** | [`docs/prd.md`](prd.md) (the signed-off contract — every FR with a UI surface traces to a screen/flow here) and [`docs/architecture.md`](architecture.md) (the M18 custom-control inventory this design composes). It does **not** re-decide the stack, the renderer, the caps, the agency model, or the control inventory. |
| **Related docs** | `brief.md`, `CLAUDE.md`, [`docs/tech-design.md`](tech-design.md), `notepad-plusplus-features.md`, future `brand.md` (Phase 7 — supplies the concrete palette + typography the §13 semantic tokens stand in for) |
| **SDLC phase** | Phase 6 — user-facing design (feeds Phase 7 `brand.md`; the Phase-8 `/code` build implements screens against this doc) |

This document specifies **what the user sees and does** in HoardPad v1 — the window shell, every screen, every flow, and every interaction state — for a **native cross-platform Avalonia desktop application** (Windows 10+ / linux-x64). It is read top-to-bottom and references the PRD and architecture by stable id (`FR-26`, `SC-1`, `§9.2`, `arch §6`). It does **not** restate the PRD's normative requirements or re-derive the architecture's pinned decisions; it gives them a concrete, traceable user surface.

**Three binding constraints on this document (stated up front so no downstream agent mis-reads it):**

1. **Native Avalonia, not web.** HoardPad is a C#-native Avalonia + AvaloniaEdit app. A browser-runtime editor (Electron / Tauri / Monaco / web-stack) is a hard CLAUDE.md §2 non-starter. **All layouts in this document are ASCII wireframes** — there are no HTML/CSS mockups, because an HTML mockup would imply the rejected path. The Phase-8 build implements these as Avalonia XAML views composing the arch §6 custom controls.
2. **Palette-agnostic — semantic role tokens only.** This document names colors and emphasis by **semantic role token** (e.g. `surface`, `on-surface`, `primary`, `danger`, `selection`, `gutter`) — never a hex value, never a specific font name. The HoardPad brand palette (amethyst / sapphire / gold-fire, FR-37) and the type scale arrive in Phase 7 `brand.md`. §13 defines the token vocabulary; `brand.md` binds each token to a concrete value.
3. **Compose the architecture, do not expand it.** Every screen composes a control or service the architecture already pinned (arch §6 M18 inventory; the Candidate-A renderer; the two-archetype AI model; the four per-turn caps; the threading seams). Where a UX need has no architectural home, it is flagged in §14 Open Design Questions — never silently invented as new scope.

## How to read this document

- **§1 — Design principles + the design reference.** The UX north star and the design-reference protocol resolution.
- **§2 — Window shell & global layout.** Title bar, menu/toolbar, tab strip, editor area, folder tree, panels, status bar. One default-window wireframe.
- **§3 — Editor surface UX.** Line numbers, folding gutter, highlighting, selection, line ops, find/replace, large-file behavior.
- **§4 — Markdown experience.** The colorized-source ↔ rendered-preview toggle.
- **§5 — The `.hoard` checklist UX.** Add/edit/check/reorder, **nesting up to three tiers with completion-lock (ODQ-11 RESOLVED — FR-41/FR-42)**, indent/outdent, collapse/expand; the durability promise made visible; first-run state.
- **§6 — The AI panel** (the signature surface — the most UX detail): agency selector, conversation UI, instant-stop, cap indicators, approve-each, activity log, undo, sandbox-scope display, error/killed states.
- **§7 — Settings UX.**
- **§8 — Navigation, commands & keyboard map.**
- **§9 — Interaction states** (explicit per surface).
- **§10 — Accessibility requirements.**
- **§11 — Notepad++ parity matrix.**
- **§12 — Traceability** (each screen → FR / arch §).
- **§13 — Semantic design-token vocabulary** (the palette-agnostic role tokens Phase 7 binds).
- **§14 — Open design questions** (each with a proposed default).
- **§15 — Reviewers required.**

Terms are the PRD's §1 Definitions (document, buffer, open folder, open-folder root, AI panel, agency mode, activity log, `.hoard`, session). This doc does not redefine them.

---

## v2 — review resolution log

v2 folds the Phase-6 four-specialist review (all APPROVE-WITH-CHANGES, zero hard BLOCK; reports at `docs/review-phase6/`). Every blocker and every should-fix is recorded below with how it was resolved (fixed / deferred-with-reason). No PRD or architecture decision was re-decided. Reviewer tags: `desktop` = `senior-csharp-desktop-developer`, `llm` = `llm-integration-engineer`, `web` = `web-designer-ux`, `pm` = `project-manager`.

**Blockers (5) — all fixed:**

- **B1 (llm F1) — cap-confirm `[Continue]` label conveyed no no-re-arm semantics.** FIXED — §6.5 wireframe relabeled `[ Allow next file (will ask again) ]` (and per-cap variants); added a binding note that the label must convey ongoing-confirm semantics and the cap does not re-arm (conversation-keyed, FR-40 MA12 / arch §4.4).
- **B2 (llm F2) — "four caps" vs three meters mismatch.** FIXED — §6 intro, §6.5, and §12 reconciled: **three live count meters** (FR-11/25, FR-38/10, FR-40/20) plus the **FR-38 X% magnitude firing-threshold (80%)** as a fourth named *parameter* (not a meter). §6.5 prose now names all four parameters explicitly.
- **B3 (web F1) — missing AI-panel "recovered after crash (mid-turn)" state.** FIXED — added to §9.4 + a §6.4 statement: on relaunch the transcript shows partial content + `— (turn interrupted) —` + the same `N file operations were applied and can be undone.` message as the user-Stop path (data already durable).
- **B4 (web F2) — checklist reorder had no keyboard path.** FIXED — added `Alt+Up` / `Alt+Down` keyboard reorder to §5.2, §10.1 (focus order), and §8.3 (keymap).
- **B5 (web F3) — `focus-ring` token conflated visual treatment with color role.** FIXED — renamed to `focus-ring-color` in §13; added a Contrast-requirement column with `≥ 3:1 against both surface and surface-alt` (WCAG 2.1 SC 1.4.11). All §13 rows now carry a contrast-requirement entry.

**Should-fix — all fixed:**

- **desktop F1 — overlay pointer-event trap.** FIXED — §6.1 specifies a `Grid`-column overlay with `IsHitTestVisible="False"` on non-interactive regions; a full-area `AdornerLayer` child is explicitly ruled out.
- **desktop F2 — find/replace is custom, not stock `SearchPanel`.** FIXED — §3.5 notes the panel is a custom `UserControl` that supersedes AvaloniaEdit's `SearchPanel.Install()` (which has no scope/live-count/review surface); the stock panel is not installed.
- **desktop F3 — `⠿` drag-handle glyph is font-coverage-dependent.** FIXED — §5.1 notes the handle must be a `PathIcon`/vendor glyph (brand icon set, Phase 7), not a raw Braille Unicode char.
- **desktop F4 — approve-each diff utility has no M18 home.** FIXED — §6.6.1 records the diff utility (pure LCS/Myers line-diff in `HoardPad.Core.Ai`) + a diff `UserControl` in `HoardPad.App` as bounded additive Phase-8 scope (~0.5–1 d), not in the arch §6 inventory.
- **llm F3 — cap-confirm heading per-cap variants.** FIXED — §6.5 adds the three heading variants (created / destructive / touched).
- **llm F4 — over-cap approve-each confirm variant missing.** FIXED — §6.6.1 adds the over-cap (undo-unavailable) confirm wireframe; §9.4 adds the "Approve-each pending (undo-unavailable op)" state.
- **llm F5 — Pending replay cap rule + no "Apply All".** FIXED — §6.6.4 states there is deliberately no batch "Apply All" and why (each apply is one user-authorized Archetype-B op, not a per-turn cap re-entry).
- **llm F6 — mid-turn degrade transcript announcement.** FIXED — §6.7 + §9.4 add the mid-turn-degrade transcript chip and the cap-meter-freeze behavior.
- **llm F7 — screen-reader labeling for safety-critical confirms.** FIXED — §10.2 specifies the assertive announcement of the cap-reached confirm, the approve-each diff summary announcement, and full semantic button labels.
- **llm F11 — per-archetype cap-enforcement source.** FIXED — §6.5 + §6.3 note the single conversation-keyed cap-state is incremented by the hook for Archetype A and by HoardPad's own execution loop for Archetype B, and that a backend switch within a conversation preserves the cap-state.
- **web F4 — default focus on safety confirms.** FIXED — §6.6.1 (`[Deny]`) and §6.5 (`[Stop]`) specify the safe-action initial focus; §10.1 reflects it.
- **web F5 — live-region announcement policy.** FIXED — §10.2 gives the four concrete rules (transcript polite at turn-complete; op chips assertive individually; activity-log rows polite on append; cap/sandbox events assertive).
- **pm F1 — delete-line shortcut inconsistency.** FIXED — §8.3 keeps `Ctrl+Shift+K` for delete-line with a stated reason for the Notepad++/VS-Code split (`Ctrl+L` reserved), folded into ODQ-2.
- **pm F2 — FR-21 "exactly one recoverable state" guarantee.** FIXED — §9.6 states recovery offers at most one recovered state per buffer and a clean save+close leaves no recovery artifact.
- **pm F4 — FR-25 context indicator promoted from optional.** FIXED — §6.4 promotes the context indicator from "may show" to an always-available (compact/collapsible) safety-legibility surface, consistent with DP-2.

**Nice-to-have — applied where cheap (rest deferred-with-reason):**

- Applied: save-error message specificity (§9.1, web F6); overflow-tab chevron keyboard path (§2.1, web F7); `warning` non-color-cue note (§10.3, web F8); Markdown preview render-latency loading state (§9.2 / ODQ-6, web F9); status-bar sandbox-segment first-run tooltip (§2.1 / §6.7, web F10); ODQ-10 FR-31 routing note (pm F5); `overlay-surface`/`surface-alt` panel-background note (§13, web token gap); cap-meter danger/warning threshold percentages (§6.5, llm F10); transcript `[applied]` vs `[approved]` vocabulary (§6.4/§6.6.2, llm F8); no-reply countdown expiry-during-streaming transcript text (§6.6.1, llm F9).
- Deferred-with-reason: status-bar segment-priority/overflow rule (pm F3) — recorded as a §2.1 note pointing to Phase-8 visual QA, since the exact collapse order is a visual-density decision better made against the real Phase-7 type scale. Mid-turn mode-switch + folder-close-mid-turn + Pending-apply-during-turn + queue-cleared-on-Stop interaction edges (llm bypass table 1/2/5/6) — addressed with one consolidated §6.4 "turn-in-flight interaction rules" note; the per-edge enforcement is the architecture's per-op hook + process-tree kill (no UX safety hole), so the UX states the rules rather than adding new affordances.

**ODQ-11 (checklist nesting) — RESOLVED 2026-06-20 (v3 fold; was REOPENED, was RESOLVED-flat).** During the Phase-6 review the `pm` adjudicated flat-checklist-for-v1 as correct because nesting/indent-outdent was never a PRD requirement. **The maintainer overruled that scope call: checklist nesting IS wanted for v1.** The design was decided in `docs/odq11-nested-checklists-plan.md` (D1–D11, completion-lock model) and folded across all four docs: PRD (FR-41/FR-42 appended, FR-30/31/32/33 + SC-6 + §9.1→schemaVersion 2 amended, re-signed off, no id renumbered, FR-31 durability preserved), architecture (§5.1 store + §6.5 control + zero-debounce write-queue), tech-design (§3.4 fixed ordering + §3.5 corpus + §5.7 +3–5 d re-estimate), and **this design — v3 re-authored §5.1 (anatomy: indent guides, twisty, greyed locked children showing real marks), §5.2 (indent/outdent, delete-parent prompt, completion-lock), §5.3 (the model), §5.5 (hierarchy-aware display modes + worked move/hide examples + Gap-B collapsed-row rule), §5.6 (leaf-unchanged note), §8.1 (menu), §8.3 (keymap), §9.3 (locked/collapsed states), §10 (tree ARIA + fan-out announcement), §14 (this ODQ flipped to RESOLVED).** The senior-csharp-multi-platform validator returned GO-WITH-GAPS on the contracts; Gap B (collapsed-parent visible-row reading) and Gap D (depth-cap clamp worked example) were closed in the PRD §9.1 / arch §6.5 / tech-design §3.5 and reflected here (§5.5). Palette-agnostic throughout.

---

## 1. Design principles & design reference

### 1.1 Design principles (the UX north star)

These principles resolve ambiguity when a specific flow is under-specified; a coding agent applies them as tie-breakers.

- **DP-1 — Notepad++ speed, never Notepad++ clutter.** The chrome is minimal and gets out of the way of the editor (the brief's wedge: Notepad++-class lightness, SC-1 ≤ 1000 ms cold start). Nothing in the default window competes with the text. The folder tree and the AI panel are summonable, not permanently occupying.
- **DP-2 — Safety is legible, never hidden.** The AI's agency mode and its sandbox root are **always visible** while the AI panel is open (FR-26, FR-27, NFR-3). The user never has to remember which mode is active or guess where the AI can write. A blocked/capped/killed state is shown plainly, not buried.
- **DP-3 — Durability is shown, not assumed.** The `.hoard` checklist and the auto-save store never *appear* to lose state (C-5, FR-31). The user gets a quiet, persistent "saved" signal — the product earns trust by visibly never losing a checkbox.
- **DP-4 — Keyboard-first, honoring Notepad++ muscle memory.** Every primary action has a keyboard shortcut (NFR-6); the default map honors Notepad++ conventions where they do not collide with platform norms (§8). Deliberate deviations are called out.
- **DP-5 — One window, summonable surfaces.** v1 is a single-window app (PRD §1: at most one open folder per window). The folder tree, find panel, AI panel, and activity log are dockable/overlay surfaces summoned over or beside the editor — never separate OS windows (FR-22 in-window overlay; avoids the Wayland always-on-top trap, SC-8).
- **DP-6 — Degrade visibly, never silently.** Large-file highlight suppression (NFR-9), degraded AI mode (FR-26), best-effort atomic write on a synced folder (NFR-4), an unarmed sandbox (FR-24 self-test fail) — each surfaces a quiet, non-blocking indicator. The user is told when a guarantee is reduced.

### 1.2 Design-reference protocol resolution

The Phase-6 design-reference check (read first): no separate `design.md` existed before this task — **this document IS the Phase-6 design reference being authored.** The concrete visual direction (palette, type scale, spacing, icon style, tone) is Phase 7 `brand.md`, which adapts the HoardPad brand palette's jewel-tone family (amethyst / sapphire / gold-fire, FR-37) sampled from the brand mascot art. Per project rules, brand assets are **never AI-regenerated** — Phase 7 composites the original brand mascot art.

This document therefore stays **palette-agnostic** (constraint 2 above): it specifies layout, behavior, states, and the **semantic role each color/emphasis plays** (§13), and Phase 7 binds those roles to concrete HoardPad brand-palette values. Hardcoding hex or fonts now would have to be redone against `brand.md`; the §13 token indirection is the seam that prevents that rework.

**Telemetry decision (recorded, not re-prompted).** The spec-session telemetry prompt is resolved by a standing project constraint: HoardPad has **no telemetry, no analytics, no network calls except the configured AI backend** (C-6, NFR-7, arch §8.3 / OQ-ARCH-6 — a hard privacy constraint, already pinned across the PRD and architecture). No `telemetry-analyst` engagement for v1; success is validated locally against the PRD §5 testable criteria and the spike-measured gates. This is a documented decline of a non-negotiable constraint, not an oversight — see §14 ODQ-9.

---

## 2. Window shell & global layout

Realizes FR-1 (tab strip + drag-reorder), FR-3/FR-4/FR-5 (open/save/dirty), FR-6 (status bar: encoding, EOL, line/col), FR-22 (AI overlay), FR-37 (theme), PRD §1 (single open folder per window). Composes the arch §1.4 Avalonia head and the arch §6 controls.

### 2.1 The regions

The window is one top-level Avalonia window with these regions, top to bottom:

1. **Title bar** — app name + active document name + dirty marker. On Linux/Windows this is the OS-native title bar by default (the Win10 custom-chrome title-bar follow-the-theme question is OQ-TD-4 / arch §3.7 — Deferred to win-x64 validation; v1 uses the native title bar).
2. **Menu bar** — `File  Edit  Search  View  Language  AI  Checklist  Settings  Help` (§8.1). Always present; the primary discoverability surface.
3. **Toolbar (optional, compact)** — a thin row of the highest-frequency actions (New, Open, Save, Find, Toggle Preview, Toggle AI panel, Toggle Folder tree). Collapsible via `View ▸ Toolbar` (default shown). Icons + tooltips; every toolbar action also exists in a menu and on a shortcut.
4. **Tab strip** — one tab per open document (FR-1). Drag-to-reorder (arch §6 FR-1 custom behavior). Each tab: document name, dirty marker, close affordance. A "+" affordance opens a new scratch buffer (FR-2). Overflow when tabs exceed width: a horizontal scroll + an **overflow chevron** that is a **focusable button** (web F7) opening a list of hidden tabs — so a keyboard-only user can both reach every tab via `Ctrl+Tab`/`Ctrl+Shift+Tab` (§8.3) **and** see the hidden-tab list for recognition over recall (Nielsen H6).
5. **Main content row** — left to right:
   - **Folder-tree pane** (collapsible, left) — the open folder shown as a tree; **its root is the AI sandbox boundary** (FR-26), made visible (DP-2). Hidden when no folder is open and the user has not summoned it.
   - **Editor area** (center, always present) — the active document's editor surface (§3) or its `.hoard` checklist control (§5) or its Markdown preview (§4), depending on document type and toggle state.
   - **AI panel overlay** (right, summonable) — the in-window overlay (§6, FR-22). Layered over the editor's right portion; does **not** permanently reflow the editor (FR-22 Done-when).
6. **Status bar** (bottom) — left-to-right segments: language mode (FR-8, clickable to override), encoding (FR-6), line-ending style (FR-6), cursor line:col + selection length (FR-6), the **AI document-folder indicator** when a folder is open (§6.7, clickable + tooltip per web F10 — FS-3 Decision B, FINAL, 2026-07-10: relabeled from "sandbox"), the **save/dirty indicator** for the active doc, and the active theme toggle (FR-37). **Segment-overflow priority on a narrow window (pm F3 — Phase-8 visual-QA detail):** when all segments cannot fit, the **document-folder indicator and the save/dirty indicator are highest priority** (safety/durability legibility, DP-2/DP-3) and collapse last; line:col and the theme toggle collapse to icons first. The exact collapse order is a visual-density decision deferred to Phase 8 against the real Phase-7 type scale.

### 2.2 Default-window wireframe (no folder open, one scratch buffer)

```
+--------------------------------------------------------------------------------------+
| HoardPad — untitled •                                            [ _ ] [ □ ] [ x ]   |  title bar
+--------------------------------------------------------------------------------------+
|  File   Edit   Search   View   Language   AI   Checklist   Settings   Help            |  menu bar
+--------------------------------------------------------------------------------------+
| [New] [Open] [Save] | [Find] | [MD Preview] | [Folder] [AI ▸]                         |  toolbar
+--------------------------------------------------------------------------------------+
| ( untitled • )  ( notes.md )  ( app.py )                                      [ + ]   |  tab strip
+--------------------------------------------------------------------------------------+
|   1 |                                                                                 |
|   2 |  the quick brown fox|                                                           |  editor area
|   3 |                                                                                 |  (gutter | text)
|   4 |                                                                                 |
|     |                                                                                 |
|     |                                                                                 |
|     |                                                                                 |
+--------------------------------------------------------------------------------------+
| Plain Text ▾ | UTF-8 | LF | Ln 2, Col 20  (0 sel) |               •unsaved  | ☾ Dark |  status bar
+--------------------------------------------------------------------------------------+
```

Notes on the wireframe:
- `•` after `untitled` (title bar + tab + status bar) is the dirty marker (FR-5).
- The folder-tree pane is absent because no folder is open; `[Folder]` in the toolbar summons "Open Folder".
- `[AI ▸]` summons the AI overlay (FR-22); the chevron hints it slides in from the right.
- The status bar's `Plain Text ▾` is the language-override control (FR-8); `UTF-8` / `LF` are the encoding + EOL display (FR-6); `☾ Dark` is the theme toggle (FR-37).
- The blinking caret is shown as `|` in line 2.

### 2.3 Default-window wireframe (folder open, AI panel open)

```
+--------------------------------------------------------------------------------------+
| HoardPad — app.py •                                              [ _ ] [ □ ] [ x ]   |
+--------------------------------------------------------------------------------------+
|  File   Edit   Search   View   Language   AI   Checklist   Settings   Help            |
+--------------------------------------------------------------------------------------+
| [New] [Open] [Save] | [Find] | [MD Preview] | [Folder] [AI ▾]                         |
+--------------------------------------------------------------------------------------+
| ( app.py • )  ( notes.md )  ( todo.hoard )                                    [ + ]   |
+--------------------------------------------------------------------------------------+
| FOLDER            |  1 | import sys              |  AI  · claude -p · auto-apply  ▾  |
|  ~/proj  (root)   |  2 |                         | ----------------------------------|
|  ├ src/           |  3 | def main():             |  Document folder: ~/proj [scoped ✓]|
|  │  ├ app.py  ←   |  4 |     print("hi")|        |                                   |
|  │  └ util.py     |  5 |                         |  You: refactor main() to take     |
|  ├ docs/          |  6 |                         |       argv                        |
|  │  └ notes.md    |    |                         |                                   |
|  └ todo.hoard     |    |                         |  AI: I'll update app.py …         |
|                   |    |                         |  ▣ edit src/app.py  [approved]    |
|                   |    |                         |                                   |
|                   |    |                         |  caps  created 0/25 · touched 1/20|
|                   |    |                         | ----------------------------------|
|                   |    |                         |  > _                       [Stop] |
+-------------------+----+-------------------------+-----------------------------------+
| Python ▾ | UTF-8 | LF | Ln 4, Col 17 | Document folder ~/proj ✓ | saved | ☾ Dark    |
+--------------------------------------------------------------------------------------+
```

Notes:
- The **folder-tree pane** (left) shows the open folder; `(root)` labels the AI sandbox boundary, and the active file `app.py` is marked with `←`.
- The **AI overlay** (right) shows, top to bottom: the agency/backend header bar (`claude -p · auto-apply`, §6.2/§6.3), the always-visible document-folder scope line (`Document folder: ~/proj [scoped ✓]`, §6.7 — FS-3 Decision B, FINAL, 2026-07-10: relabeled from "Sandbox:", the boundary itself unchanged), the conversation, the cap indicators (§6.5), and the prompt input with the persistent `[Stop]` control (§6.4).
- The status bar repeats the **document-folder indicator** (`Document folder ~/proj ✓`) so the boundary is visible even when the AI panel is collapsed (DP-2).

### 2.4 Layout behavior

- **Folder tree** toggles via `View ▸ Folder Tree` / toolbar / `Ctrl+K Ctrl+B`-class shortcut (§8). It is a resizable left pane (drag the splitter); width persists in settings (FR-36).
- **AI panel** toggles via the app-scoped hotkey (FR-22, configurable, default `Ctrl+Shift+A` — §8 / ODQ-3). It is an **overlay**, not a reflow: opening it layers over the editor's right region (FR-22 Done-when "without permanently reflowing tab/editor layout"). The user may pin it to a docked right pane (resizable) via a pin affordance — ODQ-4 covers overlay-vs-dock default.
- **Theme switch** (`☾ Dark` / `☀ Light` in the status bar, or `Settings ▸ Theme`) restyles editor + chrome + AI panel + checklist in lockstep (FR-37, arch §3.7). Known cosmetic residual: the Fluent chrome may lag the editor/preview by **one frame** on heavy resource trees (arch §3.7) — this is acknowledged as a known cosmetic, not a bug the design works around (§9.7).
- **Single window.** v1 has at most one open folder per window (PRD §1). Opening a second folder replaces the current one (with a confirm if AI write agency is armed against the current root — §6.7). Multi-window is not a v1 surface (ODQ-7).

---

## 3. Editor surface UX

Realizes FR-1…FR-9, FR-14, FR-17/FR-18, NFR-9. Composes AvaloniaEdit + TextMate (arch §1.4) and the arch §6 controls (FR-6 line ops + encoding, FR-7 grammars, FR-9 folding).

### 3.1 The editor surface anatomy

```
 line  fold                                                       text
+----+----+----------------------------------------------------------------+
|  1 |    | import sys                                                      |
|  2 |    |                                                                 |
|  3 | ▾  | def main(argv):                                                 |  ▾ = expanded fold
|  4 | |  |     for arg in argv:                                            |  | = fold body marker
|  5 | |  |         print(arg)                                              |
|  6 |    |                                                                 |
|  7 | ▸  | def helper():  ⋯                                                |  ▸ = collapsed fold (⋯ = hidden body)
|  9 |    |                                                                 |
+----+----+----------------------------------------------------------------+
```

- **Line-number gutter** (left) — `gutter` token foreground on `surface`. The current line's number uses `on-surface` emphasis (FR-6 cursor visibility).
- **Folding gutter** (the `fold` column) — fold affordances appear only on foldable region start lines (FR-9): `▾` expanded, `▸` collapsed; a `|` marks the body of an expanded fold. Hovering a fold marker highlights the region it controls. Clicking `▾`/`▸` collapses/expands (FR-9 Done-when). A collapsed region shows a `⋯` glyph at the fold header line's end to signal hidden content. Folding never corrupts buffer content (FR-9).
- **Text area** — syntax-highlighted via TextMate grammars (FR-7). The caret is a blinking `|`; selections use the `selection` token background.

### 3.2 Syntax highlighting display (FR-7, FR-8)

- Language is auto-detected by file extension on open (FR-8). The detected language shows in the status-bar `Language ▾` control and in `Language` menu (checkmarked).
- **Manual override:** clicking `Language ▾` in the status bar (or `Language` menu) opens a searchable language list; selecting one re-highlights immediately (FR-8 Done-when). The 20 v1 languages (FR-7) are listed; "Plain Text" is always available.
- A scratch buffer defaults to **Plain Text** until a language is chosen or the buffer is saved with a recognized extension (FR-8 Done-when).
- **Grammar registers lazily** (arch §3.3 / NFR-1) — the first file of a language pays a one-time first-colorize cost (the SC-2 first-grammar-load delta, ~150 ms). The user sees no special UI for this; highlighting simply appears on first open (it must not show a flash of un-highlighted text long enough to read as a glitch — ODQ-6 tracks the acceptable first-colorize latency feel).

### 3.3 Standard editing operations & discoverability (FR-6)

All standard ops work via keyboard + menu (FR-6 Done-when). The two **custom** line ops (arch §6 FR-6) need deliberate discoverability since they are not stock:

| Operation | Default shortcut | Menu home | Notes |
|---|---|---|---|
| Move line up / down | `Ctrl+Shift+Up` / `Ctrl+Shift+Down` | `Edit ▸ Line` | Custom `TextDocument` command (arch §6 FR-6) |
| Duplicate line / selection | `Ctrl+D` (line) | `Edit ▸ Line` | Custom command |
| Delete line | `Ctrl+Shift+K` | `Edit ▸ Line` | |
| Cut / Copy / Paste | `Ctrl+X` / `Ctrl+C` / `Ctrl+V` | `Edit` | Stock |
| Undo / Redo | `Ctrl+Z` / `Ctrl+Y` | `Edit` | Editor edit history (distinct from AI file-op undo, §6.6) |

`Ctrl+D`'s assignment (duplicate-line vs other editors' "select next occurrence") is a deliberate Notepad++/classic choice — called out in §8.3 and ODQ-2.

### 3.4 Encoding & line-ending display (FR-6)

- The status bar shows the active buffer's **encoding** (`UTF-8`, `UTF-16 LE`, etc.) and **line-ending style** (`LF` / `CRLF` / `CR`). UTF-8 is the default for new buffers (NFR-9).
- Existing files' encodings are **detected on open and preserved on save** (NFR-9; the `IEncodingDetector` seam, arch §1.2). The detected encoding shows immediately in the status bar.
- Clicking the encoding segment opens a menu to **reinterpret** (re-decode the bytes as a chosen encoding — for a mis-detected file) and **convert** (change the encoding the next Save writes). Clicking the EOL segment offers `LF` / `CRLF` / `CR` conversion. (Encoding-change UI granularity is ODQ-5 — minimum is display + a documented convert/reinterpret action.)

### 3.5 Find / Replace (FR-17, FR-18)

A find/replace surface summoned by `Ctrl+F` (find) / `Ctrl+H` (replace). It is an **in-editor panel** docked at the top or bottom of the editor area (not a modal dialog — keeps the matched text visible), with a scope selector.

> **Custom control, not the stock AvaloniaEdit `SearchPanel` (desktop F2).** This panel is a custom Avalonia `UserControl` — it **supersedes** AvaloniaEdit's built-in `SearchPanel` (`SearchPanel.Install(textEditor)`), which is single-file-only with no scope selector, no live match-count in this format, and no in-folder review surface. The stock `SearchPanel` is **not installed**; all find/replace capability (the three FR-17 scopes + the FR-18 essentials + the in-folder review) is delivered by this custom panel. A Phase-8 builder must not try to compose the stock panel — it cannot satisfy the in-folder scope or the review surface.

```
+--------------------------------------------------------------------------------------+
| Find:    [ (\w+)\s+\1                    ]  [.*] [Aa] [\b]   ◂ ▸   12 of 37  matches  |
| Replace: [ $1                            ]  [ Replace ] [ Replace All ]               |
| Scope:   ( • Current file ) ( All open ) ( In folder: ~/proj )       [ Review… ]      |
+--------------------------------------------------------------------------------------+
```

- **Toggles** (FR-18): `[.*]` regex-vs-literal, `[Aa]` case-sensitivity, `[\b]` whole-word. Each changes results live; the match count (`12 of 37`) updates live.
- **PCRE** with capture-group backreferences in the pattern (`\1`) and group refs in the replacement (`$1`) (FR-17 Done-when).
- **Navigation:** `◂ ▸` (or `F3` / `Shift+F3`) cycle next/previous match (FR-18).
- **Scope** (FR-17): radio-style `Current file` / `All open` / `In folder`. `In folder` shows the open folder's root path and is recursive.
- **Replace All** reports a summary count (FR-18). For an **in-folder** replace, a `[Review…]` action opens a per-file match list the user can review **before write** (FR-18 Done-when "an in-folder operation can be reviewed before write"; default review-before-write per PRD OQ-2 — see ODQ-1). The review surface lists each file with its match/replace count.

In-folder replace result summary:

```
+--------------------------------------------------------------------------------------+
|  In-folder replace — review before applying                          [ Apply ] [ x ] |
| ------------------------------------------------------------------------------------ |
|  ☑ src/app.py      3 matches → 3 replace                                              |
|  ☑ src/util.py     1 match  → 1 replace                                               |
|  ☐ docs/notes.md   5 matches → (excluded)                                             |
| ------------------------------------------------------------------------------------ |
|  9 matches across 3 files · 4 will be replaced in 2 files                             |
+--------------------------------------------------------------------------------------+
```

The user can uncheck files to exclude them; `[Apply]` performs the replace and reports per-file counts (FR-17 Done-when "reports per-file match/replace counts").

### 3.6 Large-file behavior (NFR-9 — what the user sees)

The split thresholds (NFR-9, arch §3) drive two distinct degradations, each shown plainly (DP-6):

- **Editor highlight threshold (~500 KB).** Above this, AvaloniaEdit's live syntax highlighter is suppressed; the buffer stays fully editable and the gutter/line ops still work. The status bar shows a quiet indicator: `Plain (large file) ▾`, and a one-line dismissable info strip on first open of that buffer: `Syntax highlighting is off for large files — the file is still fully editable. [Enable anyway]`.
- **Markdown preview threshold (~100–200 KB).** Above this, the FR-15 rendered preview does **not** auto-render the full document. The preview surface shows: `Preview not auto-rendered for large Markdown (N KB). [Render anyway]`. The source view is unaffected.

Both thresholds are **directional/single-sample (NFR-9 / OQ-5)** — the exact numbers are Phase-8-confirmed; the design requirement is that the suppression is **visible and user-overridable**, not the precise byte boundary. The override (`[Enable anyway]` / `[Render anyway]`) is honored at the user's own responsiveness risk.

---

## 4. Markdown experience

Realizes FR-14, FR-15, FR-16. Composes the arch §6 Candidate-A render tree (Markdig AST → Avalonia control tree) + the FR-15 toggle. The preview is a **separate Avalonia visual tree built from the parse**, never an editor over the buffer (FR-15) — no WebView/CEF (CLAUDE.md §2).

### 4.1 The source ↔ preview toggle (FR-15)

- A Markdown document (`.md`) opens in **colorized source** view (FR-14 — headings, emphasis, links, code fences, lists colorized via the Markdown TextMate grammar).
- A single **toggle** switches between colorized source and rendered, read-only preview. The toggle is:
  - A toolbar button `[MD Preview]` (shown only for Markdown docs),
  - A `View ▸ Toggle Markdown Preview` menu item,
  - A keyboard shortcut (default `Ctrl+Shift+V` — §8).
- The toggle is **reversible with no content loss** (SC-4 — toggling back yields a byte-identical buffer). The preview never has a caret and never mutates the buffer (FR-15 — trivially true since it is a separate visual tree).
- **Only Markdown documents expose the toggle** (FR-15 Done-when). For non-Markdown documents the toggle control is absent/disabled.

### 4.2 Split vs swap (the preview placement decision)

**Decision (proposed default): swap-in-place is the v1 default; side-by-side split is an optional view.** Rationale: DP-1 (minimal chrome) and the PRD's explicit v1 deferral of multi-view split + synchronized scrolling (PRD §4.3 / §12). A swap reuses the editor area; a split is the deferred multi-view surface. v1 ships the **swap** (toggle replaces source with preview in the same tab region); a side-by-side split is **not** a v1 requirement and is recorded in ODQ-8. This honors the FR-15 single-toggle contract without pulling deferred multi-view scope forward.

Swap wireframe (preview state):

```
+--------------------------------------------------------------------------------------+
| ( notes.md )  [ Source | • Preview ]                                                  |  toggle pill in tab/strip
+--------------------------------------------------------------------------------------+
|                                                                                       |
|   Project Notes                          <- rendered H1 (on-surface, large)           |
|   ============                                                                        |
|                                                                                       |
|   A short paragraph with **bold** and a [link].                                       |
|                                                                                       |
|   +----------+----------+                <- rendered table (FR-16)                     |
|   | Name     | Status   |                                                             |
|   +----------+----------+                                                             |
|   | app.py   | done     |                                                             |
|   +----------+----------+                                                             |
|                                                                                       |
|   [x] task one   [ ] task two            <- non-interactive task checkboxes (FR-16)   |
|                                                                                       |
|   +------------------------------+                                                    |
|   | python            (label)    |       <- fenced code block w/ language label       |
|   | def main(): ...              |          (plain mono; coloring not an FR-16 req)    |
|   +------------------------------+                                                    |
+--------------------------------------------------------------------------------------+
```

### 4.3 "VS Code-grade" — what the user actually sees (FR-16)

"VS Code-grade" is the aspiration; the **acceptance basis is the FR-16 feature set** (feature presence + correct structure, not pixel-parity — PRD §6.3 / arch §6). The preview visibly renders:

- **Tables** — a grid with the expected row/column count (FR-16 (1)).
- **Fenced code blocks** — each shown as a code block displaying its **language label**; the code is plain monospace (preview code-block syntax coloring is **not** an FR-16 requirement — it is an optional +2–4 d enhancement, arch §6 / tech-design §5.1) (FR-16 (2)).
- **Task-list checkboxes** — **non-interactive** glyphs (`[x]` checked, `[ ]` unchecked); they do not toggle on click (the preview is read-only) (FR-16 (3)).
- **Strikethrough** — struck-through inline runs (FR-16 (4)).
- **Autolinks** — bare URLs shown as link-styled runs using the `accent` token; **navigation is out of scope** (NFR-7 — links are styled, not clickable-to-browser) (FR-16 (5)).
- Footnotes render as footnote markers + a footnotes section (Candidate A passes the footnote assertion — PRD §6.3 SPIKE-2 20/20).

### 4.4 Scroll behavior

On swap, the preview opens scrolled to the top of the document. **Synchronized source↔preview scroll is a deferred multi-view concern** (PRD §4.3) and not a v1 requirement — toggling back to source returns to the source's prior scroll position (the source view's scroll state is retained across a toggle, since the buffer is untouched). ODQ-8 records the split + sync-scroll deferral.

---

## 5. The `.hoard` checklist document UX

Realizes FR-30…FR-34, **FR-41 (nesting + completion-lock), FR-42 (durable collapse)**, FR-31 (durability), §9.1 (v2 schema), §9.2 (recovery), SC-6 (incl. SC-6c nesting durability). Composes the arch §6.5 FR-30/FR-32/FR-41/FR-42 checklist control (`ItemsControl` + drag-reorder within a sibling group + `Alt+Shift+←/→` indent/outdent + the three hierarchy-aware display modes + the collapse twisty + `effectiveEnabled` greying, extended with tree ARIA per D11) — **not** AvaloniaEdit and **not** a stock `TreeView` (FR-30 build note). This is a load-bearing native document type; the durability promise must be visible (DP-3).

### 5.1 The checklist surface anatomy

A `.hoard` file opens as an **interactive, nestable checklist** (up to three tiers), not raw JSON (FR-30, FR-41):

```
+--------------------------------------------------------------------------------------+
| ( Trip.hoard )                                                               [ + ]   |
+--------------------------------------------------------------------------------------+
|  Trip                                                   mode: Strikethrough ▾  ✓saved |  title + mode + saved
| ------------------------------------------------------------------------------------ |
|  ⠿ ▾ [ ] Flights                                                                [x]   |  ▾ = expand twisty (parent)
|  ⠿ │   [ ] Outbound                                                             [x]   |  │ = indent guide (tier 1)
|  ⠿ │   [ ] Return                                                               [x]   |
|  ⠿ ▾ [✓] ~~Hotel~~                                          ( section complete )  [x]   |  checked parent = completed + locks subtree; TEXT badge
|  ⠿ │   [ ] Book room          (locked)                                          [x]   |  greyed/disabled, shows its REAL mark (unchecked)
|  ⠿ │   [✓] ~~Confirm dates~~  (locked)                                          [x]   |  greyed/disabled, shows its REAL mark (checked)
|  ⠿ ▸ [ ] Packing                                                                [x]   |  ▸ = collapsed twisty (subtree hidden)
|  ⠿   [ ] Buy travel insurance                                                   [x]   |  tier-0 leaf (no twisty)
| ------------------------------------------------------------------------------------ |
|  + Add item…                                                                          |  inline add row
+--------------------------------------------------------------------------------------+
```

- **Title** — editable inline (maps to `title`, §9.1).
- **Mode selector** (`mode: Strikethrough ▾`) — the per-list checked-item display mode (FR-33): `Strikethrough` (default), `Move to bottom`, `Hide when checked`.
- **`✓saved`** — the durability signal (§5.4, DP-3).
- **Each item row, left to right:** a drag handle (`⠿`), the **expand/collapse twisty** (`▾` expanded / `▸` collapsed — present only on a parent, i.e. an item with ≥1 child, FR-42), the **indent guide** (a vertical guide rendered once per tier of depth, showing the item's nesting level — `parentId`/`depth`, §9.1), a checkbox (`[ ]` / `[✓]`), the item text (struck through when checked in `strikethrough-in-place` mode), and a delete affordance (`[x]`).
- **Indent-guide rendering (discoverability — plan §7 risk 5).** Because pointer drag-to-nest is **deferred to v1.1** (§5.2), nesting must be discoverable **without** the drag gesture. The indent guide (the per-tier vertical guide) makes the existing structure legible at a glance, and the indent/outdent affordances are surfaced redundantly: the `Alt+Shift+→`/`Alt+Shift+←` shortcuts (§8.3), the **`Checklist ▸ Indent / Outdent`** menu items (§8.1), and the item-row context menu. A first-tier child is visibly inset under its parent so the hierarchy reads immediately.
- **Disabled/greyed locked-child styling (completion-lock, FR-41) — honest display, plan §7 risk 1.** When a parent is **checked**, its entire subtree is **completed and locked**: every descendant row renders **greyed/disabled** (the `muted` token, inert to pointer + keyboard) **but still shows its own real, stored check mark** — an unchecked child under a checked parent renders an **unchecked** box (greyed), a checked child renders a **checked** box (greyed). The rows are **not** faked as checked. The "this section is done" reading comes from the **parent's** checked/struck treatment plus the greying of the block, never from overwriting the children's marks. This makes the **completed-but-not-all-finished** state visually unambiguous (a user from a classic nested to-do app, who might expect checking a parent to tick its children, sees instead a struck/checked parent over greyed children that still show their actual — possibly unchecked — state). The lock is reversible: unchecking the parent un-greys the subtree and the children resume at exactly their retained marks (§5.2, FR-41).
- **Self-teaching text badge on the completed parent (C1, web-designer-ux — implements the plan §7-risk-1 "visually unambiguous" mandate, not a new decision).** A checked parent carries a short **palette-agnostic text label** on its row — `( section complete )` (or equivalent wording supplied by `brand.md` tone in Phase 7) — **in addition to** the struck/checked treatment and the greyed subtree. The badge is **text, not color** (it satisfies the §10.3 non-color-cue rule and teaches the model directly): a first-time user reads "section complete" and understands the block is closed as a unit, so they are not surprised that the children were not auto-ticked. The badge appears only on a checked parent (an item that is both `checked` and has children); a checked leaf does not carry it.
- **Add row** — an always-present `+ Add item…` inline input at the bottom; typing + `Enter` appends an item and re-focuses the input for fast entry. A new item is added at the **tier of the currently-focused item's sibling group** (or tier 0 when nothing is focused).

> **Glyph note (desktop F3) — extended to the twisty + indent guide.** The `⠿` drag handle, the `▾`/`▸` twisty, and the indent guide shown in these wireframes are **placeholders**. The Phase-8 implementation must render each as a `PathIcon` or a vendor-supplied icon glyph / drawn shape (from the brand icon set specified in Phase 7 `brand.md`), **not** raw Unicode characters (e.g. the Braille drag handle U+283F, or raw triangle code points for the twisty) — those render as missing-glyph boxes on systems whose UI font lacks coverage. The same applies to the op-chip glyphs (`▣ ＋ ⌦ ⇄`) in the AI transcript (§6.4) — icon assets, not font-coverage-dependent code points. The palette stays semantic (`muted` for the locked/greyed rows, `on-surface` for active rows); Phase 7 `brand.md` binds the values.

### 5.2 Item operations (FR-30, FR-32)

- **Add** — type in the `+ Add item…` row and `Enter` (appends at the next `order` within the active tier's sibling group). `Shift+Enter` or a menu also adds.
- **Edit text** — click the item text to edit inline; `Enter` or blur commits (item-text keystrokes coalesce ≤ 150 ms before persist, arch §3.6 — invisible to the user except the `✓saved` indicator). A **locked** item (a descendant of a checked parent, FR-41) cannot be edited — its text is inert/greyed until the parent is unchecked.
- **Check / uncheck** — click the checkbox. This is a **terminal mutation** (zero-debounce persist, arch §3.6) — the durability signal must confirm it immediately (§5.4). A **locked** item rejects the click (it is a descendant of a checked parent — `effectiveEnabled(item)` is false, §9.1/FR-41); only an item whose ancestors are all unchecked is interactive. The item itself always remains toggleable regardless of its own check state, so a **completed parent can always be unchecked** to reopen and reactivate its block.
- **Delete** — the `[x]` affordance; a deleted **leaf** is removed and its sibling group's `order` renormalized contiguously (§9.1). **Deleting a non-empty parent prompts (FR-30, D8):** the user chooses **Delete whole section (subtree)** — removes the parent and all descendants, undoable — **or Delete item, un-nest its children one tier** — removes the parent and reparents its children to the parent's own parent (the children move **out** one nesting level, depth-cap-safe; this is a tier change, not a vertical reorder). Both branches are undoable and re-persist through the FR-31 write queue (zero-debounce terminal mutation). The prompt is a **modal `ShowDialog(owner)`** (C-3) — not a modeless window — so no other checklist mutation can race the pending delete while the choice is open. Prompt wireframe:

```
+--------------------------------------------------------------+
|  Delete "Hotel"?  It has 2 items under it.                   |
| ------------------------------------------------------------ |
|  [ Delete whole section ]  [ Un-nest the 2 items (move out) ]|
|  [ Cancel ]                                                  |  <- Cancel has initial focus
+--------------------------------------------------------------+
```

  `Cancel` takes initial focus (a destructive prompt should not delete a subtree on an accidental `Enter`); the prompt is modal. (The in-list undo of any of these is ODQ-10 — covers all checklist mutations including indent/outdent and delete-un-nest, each itself a terminal mutation re-persisting through FR-31.)
- **Reorder (pointer) — within the sibling group (FR-32).** Drag the `⠿` handle to a new position **within the item's current sibling group** (same parent). The drag operates in the **displayed (transformed) order**; the hierarchy-aware display-index → stored-`order` mapping is the pure function defined in §9.1 (arch §5.1 / FR-33) — the control calls it, never embeds the arithmetic. **Pointer drag does NOT change nesting** in v1: drag-to-nest (dragging an item under another to reparent it) is **deferred to v1.1** (D5) — v1 pointer drag reorders within the current parent only; re-parenting is done via indent/outdent below.
- **Reorder (keyboard) (B4, web F2 — NFR-6 / WCAG 2.1 SC 2.1.1).** With focus on an item row, `Alt+Up` / `Alt+Down` moves the item up/down one position within its sibling group. This is the keyboard equivalent of the drag handle. The move routes through the same hierarchy-aware mapping and the FR-31 write queue (a terminal mutation, persisted zero-debounce). A "Move up / Move down" pair also appears in the item's context menu and in `Checklist ▸` (§8.1).
- **Indent / outdent (FR-32, FR-41 — `Alt+Shift+→` / `Alt+Shift+←`, subtree travels).** With a row focused, `Alt+Shift+→` **demotes** the item one tier (it becomes a child of its immediately-preceding sibling), and `Alt+Shift+←` **promotes** it one tier (it becomes a sibling of its current parent). The item carries its **entire subtree** — relative structure is preserved (D9). Both are terminal mutations (zero-debounce persist). The **depth cap is enforced**: an `Alt+Shift+→` that would push the item or any descendant past tier 2 is a **no-op** (a quiet signal, no error). `Checklist ▸ Indent / Outdent` (§8.1) and the row context menu carry the same actions, so nesting is reachable without the keyboard too.
- **Completion-lock (checking/unchecking a parent — FR-41, plan §7 risk 1).** Checking a parent marks the whole block **completed** and **locks its subtree**: every descendant becomes inert/greyed (§5.1) **while still showing its own real mark** (children are never silently ticked). The check is **manual and allowed at any time** regardless of child states — it is the user's "this section is done" switch (D2a), with no cascade to child marks. **Unchecking the parent reactivates the subtree at exactly the retained child state** — no child mark, text, or order is altered by the lock/unlock cycle (D3). An **unchecked** parent leaves its children fully active. (A node is interactive iff all its ancestors are unchecked — `effectiveEnabled`, §9.1.)

### 5.3 Checklist nesting — the model (ODQ-11 RESOLVED, completion-lock)

Nesting is a v1 feature (ODQ-11, FR-41/FR-42). The model is **completion-lock**, decided in `docs/odq11-nested-checklists-plan.md` (D1–D11):

- **Up to three tiers** (depth 0/1/2). A fourth tier is disallowed; `Alt+Shift+→` past tier 2 is a no-op (§5.2, D4).
- **`checked` means "completed" for every item, leaf or parent** (one uniform meaning, D2). On a parent it *also* locks the subtree.
- **Checking a parent completes + locks its block.** A checked parent's entire subtree is inert/greyed, but each descendant keeps its **own real mark** (D3a — honest display, never faked-as-checked). Checking is **manual, anytime** (D2a). An **unchecked** parent leaves its children active.
- **Locking never mutates child state.** Child `checked`/`text`/`order` is retained and restored **exactly** when the parent is unchecked (D3).
- **Interactive iff all ancestors are unchecked** — `effectiveEnabled(item)` = every strict ancestor has `checked == false` (§9.1, FR-41). The item itself is always toggleable, so a completed parent can always be reopened.
- **Indent/outdent (`Alt+Shift+←/→`) carries the whole subtree** (D9). Pointer drag reorders within the current sibling group only; **drag-to-nest is deferred to v1.1** (D5).
- **Collapse/expand** via the `▾`/`▸` twisty; the `collapsed` state is durable (FR-42, §5.4).
- **Deleting a non-empty parent prompts** delete-subtree vs promote-children-up-one-tier (D8, §5.2).

The schema stays a **flat `items[]` array** with `parentId` (string|null) + per-sibling-group `order` + `collapsed` (bool), `schemaVersion: 2` (PRD §9.1). The display-index → stored-`order` mapping (§5.5) is now **hierarchy-aware** — a completed parent's block moves/hides as one contiguous unit within its sibling group. The §5.1 anatomy, §5.2 operations, §5.5 display modes, and §9.3 states below are all authored against this model.

> **Display-the-completed-block honestly (plan §7 risk 1, flagged at design time).** Under completion-lock, checking a parent does **not** tick its children. The visual must make the **completed-but-not-all-finished** state unambiguous so a user conditioned by classic nested to-do apps is not confused: the parent renders checked/struck, its subtree renders greyed/disabled, and each child still shows its actual mark (including unchecked). §5.1 (anatomy) and §5.5 (display modes) carry this requirement. The alternative — rendering the whole block as if every child were checked — was explicitly offered and **declined** in favor of honest display.

### 5.4 The durability promise, made visible (FR-31, C-5, DP-3)

The user must never fear losing list state (C-5). The design **shows** durability:

- A persistent **`✓saved`** indicator in the checklist header. Its states:
  - `✓ saved` (steady, `success`/`muted` token) — on-disk state matches the in-memory state (the normal resting state).
  - `… saving` (transient, ≤ a frame for terminal mutations; up to ~150 ms for item-text) — a write is in flight.
  - `⚠ save degraded` (`danger`/`muted` token) — the `.hoard` is on a synced/network/cross-device folder where atomic zero-loss is best-effort (NFR-4); a tooltip explains "Zero-loss save is not guaranteed on this location (network/synced folder)." This is the DP-6 visible-degrade for the SC-6a local-only guarantee.
- **No explicit Save needed for a `.hoard`** — every mutation auto-persists (FR-31). The `✓saved` indicator is the user's proof; there is no dirty marker on a `.hoard` tab in the normal case (it is always saved). A `.hoard` tab shows the dirty marker only in the `⚠ save degraded` case.
- Terminal mutations (check/reorder/delete/mode change) persist with **zero debounce** (arch §3.6) — the `✓saved` confirm is effectively instant, which is the visible proof that a kill right after a checkbox click cannot lose it (SC-6a).

### 5.5 Display modes (FR-33) — what each looks like

```
 Strikethrough (default)        Move to bottom                 Hide when checked
+------------------------+      +------------------------+      +------------------------+
| [ ] Milk               |      | [ ] Milk               |      | [ ] Milk               |
| [ ] Eggs               |      | [ ] Coffee             |      | [ ] Coffee             |
| [✓] ~~Bread~~          |      | ---------------------- |      |                        |
| [ ] Coffee             |      | [✓] Bread              |      |  ⊞ 1 checked item hidden|  <- reveal control
+------------------------+      +------------------------+      +------------------------+
```

- **Strikethrough-in-place** (default) — checked item stays in position, struck through.
- **Move-to-bottom** — checked items collected below a divider, in stored order; unchecking returns the item to its stored `order` position (§9.1).
- **Hide-when-checked** — checked items hidden from view but retained in the file; a `⊞ N checked item(s) hidden` reveal control expands them (FR-33 "a control reveals hidden/checked items").
- The selected mode persists in the file (`settings.checkedItemMode`, §9.1) and reloads with it.

**Under nesting, every checked item is "done," and the modes are hierarchy-aware (FR-33, §9.1).** The transform is applied **per sibling group**; a **checked parent moves/hides/strikes as a whole block** — its subtree travels with it as one contiguous unit (never splitting the subtree from its parent). A checked **leaf** moves/hides/strikes within its sibling group as above.

Worked `move-to-bottom` example — a checked parent's whole block sinks below its unchecked siblings within its sibling group (here the tier-0 group `Flights` / `Hotel` / `Packing`):

```
 before (Hotel just checked)            after (move-to-bottom)
+------------------------------+        +------------------------------+
| ▾ [ ] Flights                |        | ▾ [ ] Flights                |
|   │ [ ] Outbound             |        |   │ [ ] Outbound             |
|   │ [ ] Return               |        |   │ [ ] Return               |
| ▾ [✓] ~~Hotel~~              |        | ▸ [ ] Packing                |   <- unchecked siblings rise
|   │ [ ] Book room  (locked)  |        | ---------------------------- |   <- divider
|   │ [✓] Confirm    (locked)  |        | ▾ [✓] ~~Hotel~~              |   <- whole Hotel BLOCK sinks
| ▸ [ ] Packing                |        |   │ [ ] Book room  (locked)  |      (subtree travels, stays greyed)
+------------------------------+        |   │ [✓] Confirm    (locked)  |
                                        +------------------------------+
```

The `Hotel` block — parent + its two locked children — sinks **as one unit** below the unchecked `Flights`/`Packing` siblings; the children stay greyed and keep their real marks (`Book room` unchecked, `Confirm` checked). Unchecking `Hotel` returns the whole block to its stored sibling-group position and reactivates the children.

Worked `hide-when-checked` example — checking a parent hides its **whole block** (retained in the file):

```
 before (Hotel unchecked)               after (Hotel checked, hide-when-checked)
+------------------------------+        +------------------------------+
| ▾ [ ] Flights                |        | ▾ [ ] Flights                |
|   │ [ ] Outbound             |        |   │ [ ] Outbound             |
|   │ [ ] Return               |        |   │ [ ] Return               |
| ▾ [ ] Hotel                  |        | ▸ [ ] Packing                |
|   │ [ ] Book room            |        |  ⊞ 1 checked section hidden  |   <- whole Hotel block hidden, in file
| ▸ [ ] Packing                |        +------------------------------+
+------------------------------+
```

Checking `Hotel` hides the entire `Hotel` block (parent + subtree) from view; the items remain in the file (`parentId`/`order`/`checked` intact) and the `⊞ … hidden` reveal control expands them.

**Reveal-control wording when multiple blocks and leaves are hidden at once (C2 — pin the aggregate, don't let a builder invent it).** The reveal control reports a **single aggregate of hidden top-level units within the scope being revealed**, where a hidden **checked-parent block counts as one unit** (not as its descendant count) and a hidden **checked leaf counts as one unit** — e.g. `⊞ 3 hidden (2 sections, 1 item) — show`. Activating it reveals **all** hidden checked units in that sibling group at once (it is not a per-unit toggle); revealed items return to their stored positions and a checked parent's revealed block stays greyed/locked (it is still checked). The count phrasing distinguishes "sections" (checked parents) from "items" (checked leaves) so the user knows a hidden section expands to a multi-row block, not a single row. (Hidden-by-collapse rows are **not** counted here — collapse is a separate, per-parent twisty, Gap B; only `hide-when-checked` rows feed this reveal control.)

**Collapse is orthogonal to the checked-item mode (Gap B, §9.1).** A **collapsed** parent's descendants are **absent from the visible-row sequence** — the same idiom as hidden-checked items — **regardless of the parent's checked state**. This includes a **collapsed-but-unchecked** parent: its children are present in the file and fully active, just not rendered while collapsed; the collapsed parent's own row stays visible and participates in its sibling group's drag/drop-gap arithmetic. Expanding restores the descendants to the pre-order walk at their stored positions (collapse mutates only `collapsed`, never `parentId`/`order`). So `▸ Packing` above is collapsed (its children hidden by collapse) while `Hotel` is hidden by being checked — distinct, composable behaviors over the one visible-row sequence.

### 5.6 First-run / empty state of a new `.hoard` (FR-34)

`Checklist ▸ New Checklist` (or `[ + ]` ▸ New Checklist) opens an empty checklist tab:

```
+--------------------------------------------------------------------------------------+
| ( untitled.hoard )                                                                   |
+--------------------------------------------------------------------------------------+
|  Untitled checklist                                    mode: Strikethrough ▾  ✓saved |
| ------------------------------------------------------------------------------------ |
|                                                                                       |
|        Your checklist is empty.                                                       |
|        Type below to add your first item — it saves automatically.                   |
|                                                                                       |
| ------------------------------------------------------------------------------------ |
|  + Add item…  |_                                                                      |  focus starts here
+--------------------------------------------------------------------------------------+
```

- The empty state reassures with the durability promise ("it saves automatically", DP-3).
- The default mode is `strikethrough-in-place` (FR-33 Done-when).
- Focus starts in the add-item input for immediate entry.
- Saving writes a valid §9.1 v2-schema file (FR-34); the new checklist participates in session restore (FR-19/FR-34).
- **The empty state and single-item behavior are unchanged by nesting (ODQ-11).** A new checklist starts flat; a **single tier-0 item with no children behaves exactly as before** — it is a leaf, with no twisty, no indent guide, and no completion-lock interaction. Nesting affordances (the twisty, indent guides, indent/outdent) appear only once the user creates structure; the first-run experience is identical to the prior flat baseline.

---

## 6. The AI panel (signature surface)

Realizes FR-22…FR-29, FR-10, FR-11, FR-38, FR-39, FR-40, SC-1/SC-2 (instant-stop), NFR-3, NFR-8, NFR-10. Composes the arch §4 two-archetype provider, the conversation-keyed cap-state (arch §4.4), the shared file-ops service (arch §4.5), the FR-22 in-window overlay (arch §1.4). This is the product's differentiating wedge and gets the most UX detail.

**The four pinned per-turn cap parameters (the exact count, so a Phase-8 builder is not off-by-one).** The PRD/architecture pin **four** parameters, surfaced as **three live count meters + one threshold parameter** (the distinction matters — §6.5): FR-11 files-created (25), FR-38 destructive-ops (10), FR-40 distinct-files-touched (20), and the **FR-38 X% magnitude firing-threshold (80%)** that determines *when a write counts as destructive*. The X% is a parameter, not a fourth budget meter; it is shown as a context label in the cap region and is tunable in Settings (§7.5, FR-35).

### 6.1 The panel shell (FR-22)

The AI panel is an **in-window overlay** (FR-22 — over the editor, never a separate OS window). Toggled by an app-scoped hotkey (default `Ctrl+Shift+A`, configurable — FR-22, §8). It works identically on Windows and Linux/Wayland (FR-22, SC-8 — app-scoped hotkey, no global-hotkey/always-on-top trap).

**Overlay implementation (desktop F1 — pointer-event correctness).** The overlay is implemented as a `Panel` floating in a **`Grid`-column overlay** over the editor's right portion, with `IsHitTestVisible="False"` on any non-interactive regions, so pointer events pass through to the editor when the panel is narrower than the editor column. A full-area `AdornerLayer` child must **not** be used — an adorner spanning the editor area would intercept all `PointerPressed`/`PointerMoved` events destined for the `TextArea` underneath, making the editor unreachable to the mouse while the panel is visible. The overlay floats over the editor without permanently reflowing tab/editor layout (FR-22). The overlay background uses the `surface-alt` token (not `surface`) so the panel is visually distinct from the editor behind it (§13).

Panel regions, top to bottom:

```
+-----------------------------------------------+
|  AI   · claude -p ▾  · auto-apply ▾    [—][×] |  (1) header: backend + agency + collapse/close
+-----------------------------------------------+
|  Document folder: ~/proj    [ scoped ✓ ]      |  (2) document-folder scope line (always visible)
+-----------------------------------------------+
|                                               |
|  You: refactor main() to take argv            |  (3) conversation transcript
|                                               |      (streaming, file-op chips, scrollback)
|  AI: I'll update app.py to accept argv …      |
|  ▣ edit  src/app.py            [ approved ]   |
|                                               |
+-----------------------------------------------+
|  caps  created 0/25 · destructive 0/10        |  (4) per-turn cap indicators
|        touched 1/20                           |
+-----------------------------------------------+
|  [ Activity log ]  [ Pending (0) ]            |  (5) activity-log + pending-proposals tabs
+-----------------------------------------------+
|  > _                                  [Stop]  |  (6) prompt input + instant-stop
+-----------------------------------------------+
```

### 6.2 The agency-mode selector (FR-27, FR-10 — load-bearing)

The agency mode is **always visible** in the panel header (`auto-apply ▾`) (DP-2). It is the single most safety-critical control.

- **Three modes** (FR-27): `auto-apply` (default — file ops apply immediately within the sandbox), `approve-each` (every op requires confirm before applying), `read-only` (AI reads/chats, performs no file ops).
- **Default:** `auto-apply` (FR-27, brief). Changeable from the header dropdown **and** from `Settings ▸ AI ▸ Agency mode` (FR-35) — both routes change the same setting, effective without restart.
- **Current mode is always shown** in the header; when an op is **downgraded** (FR-10 — home/large root, undo-unavailable/clobbering op, or degraded backend), the header shows the **effective** mode with a downgrade badge:

```
|  AI · claude -p ▾ · auto-apply (↓ approve-each: large root) ▾  [—][×] |
```

  The badge text names the reason (`large root` / `home root` / `clobbering op` / `undo-unavailable` / `degraded backend`) mirroring `downgradeReason` (§9.3). A one-time announcement banner appears when a root-level downgrade first arms (FR-10(a)): `N files are writable by the AI; auto-apply is downgraded to approve-each for this root.` *(Phase-8 note: the announcement banner is not yet built — the badge above and the per-op confirm dialog are the shipped legibility surface, docs/decisions.md DR-1.)*
- **FR-10(a) is now user-overridable, default ON (FS-5, docs/decisions.md DR-1).** Settings ▸ AI safety carries "Confirm each AI edit in large/home folders" (default ON = the `large root` / `home root` badge behavior above). Turning it OFF removes ONLY that one trigger — `auto-apply` then applies silently on a home/large root too — while the clobbering-op/undo-unavailable and degraded-backend downgrades (FR-10(b)/(c)), the per-turn caps, and the FR-26 boundary are unaffected in either state. The toggle is consulted once per session-arm (folder-open / first write-mode AI action), not per individual op — a folder that grows past the threshold mid-session is re-evaluated at the next arm, not immediately (a scoped-down predicate vs. the PRD's full op-time re-count, see DR-1).
- **read-only** mode visibly disables the file-op affordances: any AI attempt to write surfaces `Read-only mode — file changes are disabled.` in the transcript (FR-27 Done-when).

### 6.3 The backend selector (FR-23, FR-24)

- The header `claude -p ▾` shows the active backend (FR-23): `claude -p` (Claude Code headless, default Archetype A), `Anthropic API` (Archetype B), `Local LLM` (Ollama/LiteLLM, Archetype B). Switching routes the next request through that backend with no other behavioral change, no restart (FR-23 Done-when). Full per-backend config lives in Settings (§7.2).
- **Readiness** is reflected inline: a backend that is not configured/ready shows a state in the header (`claude -p ⚠`) and the conversation surfaces the actionable error (§6.9, FR-24).
- **Backend switch preserves the conversation-keyed cap-state (llm F11).** Switching backends *within the same conversation* does **not** reset the per-turn caps (§6.5) — the cap-state is keyed to the user-initiated conversation (FR-40 cap-state-lifetime / arch §4.4), not to the backend. The cap meters therefore stay accurate across a mid-conversation backend switch; only a new user-initiated conversation resets them.

### 6.4 The conversation UI + instant-stop (FR-25, SC-1/SC-2, FR-39, NFR-8)

- **Prompt input** (region 6) — a multi-line input; `Enter` sends, `Shift+Enter` newline.
- **Read-context indicator — always available (pm F4, promoted from optional).** The AI has read context on the current doc, all open docs, and the folder tree (FR-25). No UI action attaches context. The panel **always shows** a compact, collapsible read-scope indicator (e.g. `context: app.py + 2 open docs + folder tree ▸`) so the user can always see what the AI can read — consistent with DP-2 (safety is legible), since read context is the input to every file op. It may be collapsed to a single line but is never omitted.
- **Streaming response** — the AI's reply streams token-by-token into the transcript (FR-24 `--include-partial-messages`, arch §3.4). File operations the AI performs appear as inline **op chips** in the transcript stream (▣ edit / ＋ create / ⌦ delete / ⇄ move), each with its target path and an outcome badge. **Outcome vocabulary (llm F8):** `[applied]` for an auto-apply op, `[approved]` for a user-confirmed approve-each op, plus `[blocked]` / `[undone]` / `[queued]` — so the user can tell at a glance which ops they personally signed off on. The activity log (§6.6.2) aligns to the same vocabulary.
- **The instant-stop control (`[Stop]`) — must feel immediate (SC-1/SC-2 stop-instant ethos, NFR-8).** A persistent `[Stop]` button sits beside the prompt input whenever a turn is in flight. Pressing it:
  - Immediately kills the `claude -p` process tree (`KillTreeAsync`, arch §3.4 — Linux `kill(-pgid)`, Windows Job-Object kill) — this is the cancellation primitive (NFR-8).
  - The UI reflects the stop **without waiting** for the process to fully die: the streaming indicator stops, the transcript shows `— stopped —`, and the input re-enables. The kill happens off-UI (arch §3.4) so the editor never freezes.
  - Any ops already applied before the stop remain on disk and remain in the activity log + undo journal (the user can undo them, §6.6). The transcript makes this clear: `Stopped. 2 file operations were applied and can be undone.`
- **Queue behavior (FR-39).** While a turn is in flight, a new prompt is **queued** (the input shows `queued — will run after this turn`), not interleaved. Separately, an unanswered `approve-each` confirm that times out fails closed and parks the proposal in the **Pending** surface (§6.6.3) — that is the FR-39 pending-proposals queue, distinct from the prompt-send queue.
- **Recovered after crash (mid-turn) (web F1, B3).** If the app dies ungracefully while a turn is in flight, on relaunch the transcript shows the last turn's partial streamed content followed by a truncation marker `— (turn interrupted) —` and the same `N file operations were applied and can be undone.` message used for the user-Stop path. The data is already durable — the activity log + undo journal (PRD §9.3/§9.4) persist every applied op — so this is a design statement, not new architecture: the user never relaunches into an opaque transcript that hides what was applied before the crash (DP-2, Nielsen H1). The applied ops are in the activity log and undoable (§6.6.3). See §9.4.

**Turn-in-flight interaction rules (consolidates the llm bypass-table edges 1/2/5/6 — stated, not new affordances).** The architecture's per-op hook + process-tree kill are the enforcement points (no UX safety hole); the UX states these rules so a Phase-8 builder does not introduce a mid-flight gap:
- **Agency-mode change mid-turn** takes effect at the *next* turn, not mid-turn — the header dropdown and Settings selector are visually disabled (or marked "applies next turn") while a turn is in flight, so a user cannot turn a queued approve-each prompt into an auto-apply op mid-stream. (The per-op hook gates each op at hook-time regardless; this rule only removes UI confusion.)
- **Closing/replacing the open folder mid-turn** prompts a confirm and stops the turn (process-tree kill, arch §3.4) before the sandbox root changes — write agency is never disarmed under a live turn.
- **Applying a Pending proposal** is disabled while a turn is in flight (the `[Apply]` buttons are inactive during a live turn) so a replay and a live hook-gated write cannot race the same file; the shared service's atomic writes are the backstop, but the UX removes the race outright.
- **Pressing `[Stop]` clears the prompt-send queue** — a queued prompt is discarded on Stop, not silently dequeued to start a fresh turn against a dying process.

### 6.5 The per-turn cap indicators (the four pinned parameters: FR-11, FR-38 + X%, FR-40)

The per-turn caps are the visible injection backstop (NFR-10). **Four pinned parameters govern the caps, surfaced as three live count meters plus one threshold parameter.** The three meters are shown in region 4 (always visible while the AI panel is open, DP-2):

```
|  caps  created 3/25 · destructive 1/10 · touched 4/20    (X% threshold 80%)          |
```

The four parameters:

- **created N/25** (meter) — files created this turn (FR-11, cap 25). A create is a `Write`/`Edit` to a non-existent path.
- **destructive N/10** (meter) — destructive ops this turn (FR-38, cap 10): whole-file overwrites / ≥ X% replacements / deletes.
- **touched N/20** (meter) — distinct files touched this turn (FR-40, cap 20): the aggregate of every create + every overwrite, regardless of magnitude.
- **X% magnitude firing-threshold = 80%** (parameter, **not** a meter) — the FR-38 threshold that determines *what qualifies as a destructive op*: a write replacing/shrinking ≥ 80% of a file's prior bytes counts toward the destructive meter; below 80% it is an excluded partial edit (it still increments the *touched* meter, FR-40). Shown as a fixed context label `(X% threshold 80%)` in the cap region; tunable in Settings (§7.5, FR-35). It is the fourth pinned parameter — a firing threshold, not a count budget — which is why region 4 shows three meters, not four.

**Cap-enforcement source differs by archetype (llm F11) — but the meters reflect one shared state.** The cap meters reflect a single conversation-keyed cap-state managed by HoardPad (arch §4.4). For **Archetype A** (`claude -p`), the hook process increments this state and the per-op hook `deny` is the enforcement point. For **Archetype B** (Anthropic API / local LLM), HoardPad's own execution loop in the shared file-ops service increments the *same* cap-state and enforces the same deny-on-trip behavior. A Phase-8 builder must not wire the cap UI to the hook-state file alone — Archetype B has no hook, and its caps are enforced in HoardPad's execution path. The meters stay accurate across a backend switch within a conversation (§6.3).

- **Meter emphasis thresholds (llm F10 — proposed defaults, do not leave unspecified).** A meter shifts from normal emphasis to the `warning` token at **60%** of its cap (e.g. 15/25, 6/10, 12/20) and to the `danger` token at **80%** of its cap (e.g. 20/25, 8/10, 16/20), so the user sees an injection-shaped run building **before** it trips. The count itself (e.g. `touched 18/20`) is the non-color cue (§10.3). These percentages may be adjusted in visual QA but must not be left unspecified.

**Cap-reached blocked state.** When a cap is hit, the turn **pauses** and a confirm appears in the transcript (for Archetype A the hook denied past the cap and "continue" is a HoardPad-initiated continuation):

```
+--------------------------------------------------+
|  ⚠ Per-turn cap reached — files touched: 20/20   |
|  This turn has touched 20 distinct files          |
|  (the per-turn limit). This can be a sign of      |
|  a runaway or injected instruction.               |
|                                                   |
|  [ Allow next file (will ask again) ]   [ Stop ]  |  <- [Stop] has initial focus
+--------------------------------------------------+
```

- **The confirm heading names the specific cap that tripped + the count** (llm F3). The three variants:

| Cap that tripped | Wireframe heading | Action button |
|---|---|---|
| FR-11 (created) | `⚠ Per-turn cap reached — files created: 25/25` | `[ Allow next file (will ask again) ]` |
| FR-38 (destructive) | `⚠ Per-turn cap reached — destructive ops: 10/10` | `[ Allow next op (will ask again) ]` |
| FR-40 (touched) | `⚠ Per-turn cap reached — files touched: 20/20` | `[ Allow next file (will ask again) ]` |

- **The action-button label MUST convey ongoing-confirm semantics (B1, binding).** The label is `[ Allow next file (will ask again) ]` (or `Allow next op` for the destructive cap) — **not** a bare `[Continue]`. **The cap does NOT re-arm:** allowing one more op does not grant a fresh budget; every subsequent op of that class confirms again individually (FR-11 MA12 / FR-38 / FR-40 no-re-arm; conversation-keyed cap-state, arch §4.4). A Phase-8 builder must implement this as a per-op repeating confirm, never a one-time re-arm — a one-time re-arm would defeat the confirm-fatigue injection backstop (NFR-10). The label's "(will ask again)" is the user-facing expression of this contract and must not be dropped.
- **`[Stop]` has initial keyboard focus (web F4).** For this safety gate, the conservative action (`Stop`) receives initial focus so an accidental `Enter` does not authorize another op. `[Stop]` ends the turn; already-applied ops stay, logged + undoable.
- The cap firing is recorded in the activity log with `downgradeReason` (`destructive-cap` / `files-touched-cap` / etc., §9.3) so the safety net's engagement is auditable.

### 6.6 The approve-each file-op UI, activity log, undo, pending

#### 6.6.1 Approve-each per-write confirm (FR-27, FR-24, FR-10)

In `approve-each` (or a downgraded `auto-apply` op, FR-10), each file write shows a **blocking confirm with a diff/preview** before it applies (FR-27 Done-when; arch §4.2 — the synchronously-blocking hook). It appears inline in the transcript:

```
+-----------------------------------------------+
|  AI wants to edit  src/app.py                 |
| --------------------------------------------- |
|    1   def main():                            |
|  - 2       print("hi")          (removed)     |  - red/danger gutter
|  + 2   def main(argv):          (added)       |  + green/success gutter
|  + 3       for a in argv:                     |
|  + 4           print(a)                       |
| --------------------------------------------- |
|  in the document folder ~/proj · undoable     |
|  [ Approve ]   [ Deny ]        ⏱ 58s          |  countdown to no-reply fail-closed
+-----------------------------------------------+
```

- The diff shows added/removed lines (`+`/`-` with `success`/`danger` gutter tokens). For a **create**, it shows the new file path + content preview; for a **move/clobber**, it shows source → dest and **the clobbered destination's prior content** that will be captured for undo (FR-28 clobber capture).
- A line confirms the op is **in the document folder** and whether it is **undoable** (FS-3 Decision B, FINAL, 2026-07-10: relabeled from "in sandbox").
- **`[Deny]` has initial keyboard focus (web F4).** This is a safety-critical confirm — pressing `Enter` on the wrong button applies or denies an AI file op. Initial focus is on `[Deny]` (the conservative action); applying requires a deliberate `Tab`+`Enter` or a mouse click on `[Approve]`. **`[Approve]`** applies the op (the hook returns `allow`, arch §4.2); **`[Deny]`** rejects it (`deny`).
- **No-reply countdown (`⏱ 58s`).** If the confirm is unanswered past the no-reply timeout (FR-24, default ~60 s), it **fails closed**: the op is denied, the `claude` process tree is killed, and the proposal is **parked in the Pending surface** (§6.6.3, FR-39). The countdown makes the fail-closed deadline visible so it is not a surprise. **If the countdown expires while the turn is still streaming (llm F9),** the transcript shows `— timed out, turn stopped — the pending op was queued` immediately after the last streamed token; the turn does not resume and the activity log records the kill.

**Over-cap (undo-unavailable) confirm variant (llm F4).** When the op is over the snapshot-size cap (FR-28(iii)/FR-10(b)) — undo would be unavailable — the confirm cannot say "undoable" and instead warns explicitly before applying:

```
+-----------------------------------------------+
|  AI wants to overwrite  src/largefile.bin     |
| --------------------------------------------- |
|    (binary file, 45 MB)                       |
| --------------------------------------------- |
|  in the document folder ~/proj                |
|  ⚠ undo unavailable — this file exceeds the   |
|    snapshot size cap. This op cannot be        |
|    reversed once applied.                      |
|  [ Approve (irreversible) ]   [ Deny ]        |  <- [Deny] has initial focus
+-----------------------------------------------+
```

This is a distinct state — `Approve-each pending (undo-unavailable op)` — listed in the §9.4 state table. `[Deny]` again has initial focus; `[Approve (irreversible)]` makes the unrecoverable consequence explicit in the label.

**The diff display is bounded additive Phase-8 scope (desktop F4 — not in the arch §6 M18 inventory).** Rendering the `+`/`-` diff requires a **line-diff utility** (before-content vs proposed-content) which is **not** listed in the arch §6 custom-control inventory. It is pure domain logic in `HoardPad.Core.Ai` (a small LCS / Myers line-diff over two strings — **no third-party diff package needed**, ~100 lines of C#) plus a diff-rendering `UserControl` in `HoardPad.App`. Phase 8 treats both as bounded additive scope (~0.5–1 d total), recorded here so it is not discovered as a surprise during the build.

#### 6.6.2 Activity log (FR-29) — every op, viewable, undoable

The `[Activity log]` tab (region 5) shows the append-only record of **every** AI file op — applied, blocked, undone, queued (FR-29):

```
+-----------------------------------------------------------------------------+
|  Activity log                                       turn #a1b2 · claude -p   |
| --------------------------------------------------------------------------- |
|  10:32:04  edit    src/app.py        applied   ~/proj   +3/-1 B   [ Undo ]   |
|  10:32:01  create  src/argv.py       applied   ~/proj   +120 B    [ Undo ]   |
|  10:31:58  edit    /etc/passwd       BLOCKED   outside document folder  —    |  danger token
|  10:31:55  read    src/app.py        ok        ~/proj            —          |
| --------------------------------------------------------------------------- |
|  Undo:  [ Undo last AI file op ]   [ Undo this whole turn ]                  |
+-----------------------------------------------------------------------------+
```

- Each row: timestamp, op type, target path, outcome (`applied` for auto-apply / `approved` for user-confirmed / `blocked` / `undone` / `failed` / `queued` — aligned with the transcript chip vocabulary, §6.4, llm F8), the sandbox root it was scoped to, a size delta (bytes, **not** content, NFR-7), and a per-op `[Undo]` (FR-28).
- **Blocked out-of-document-folder attempts** (FR-26) appear with outcome `BLOCKED` and the reason (`outside document folder`), in a `danger` emphasis — the visible proof the boundary held (SC-5(d)). (FS-3 Decision B, FINAL, 2026-07-10: relabeled from "out of sandbox"; enforcement unchanged.)
- The log **never shows API keys or file contents** (NFR-7) — only sizes and paths.
- A `requestId`/turn id groups all ops of one turn (shown as `turn #…`), so the user can scope undo to a whole turn.

#### 6.6.3 Undo affordances (FR-28)

- **Per-op undo** — the `[Undo]` on each activity-log row reverses that single op (restore overwritten content, delete a created file, restore a moved/clobbered file, FR-28).
- **Per-turn undo** — `[Undo this whole turn]` reverses all of a turn's ops in reverse activity-log order (FR-28 "operates in reverse order of the activity log").
- **Ctrl+Z** — for an AI **buffer edit** in an open document, the standard editor undo reverts it (FR-28). For a **filesystem** op (create/move/delete/overwrite of a closed file), the explicit `[Undo last AI file op]` action is the path (FR-28 — distinct from buffer Ctrl+Z). The transcript and activity log both expose this.
- **Undo unavailable / refused states** (FR-28):
  - An over-cap op shows `undo unavailable` in its log row (it was confirmed before applying, FR-28(iii)).
  - An undo attempted against an **externally-modified file** (the on-disk content diverged since the op, detected by content hash, FR-28(ii)) shows a refuse/warn dialog: `This file changed on disk since the AI edited it. Undo would overwrite newer changes. [Show diff] [Undo anyway] [Cancel].`

#### 6.6.4 Pending AI proposals surface (FR-39)

The `[Pending (N)]` tab (region 5) holds proposals that **failed closed** (an unanswered approve-each confirm past the no-reply timeout, §6.6.1):

```
+-----------------------------------------------------------------------------+
|  Pending proposals (failed-closed, recoverable)                             |
| --------------------------------------------------------------------------- |
|  edit    src/app.py     (proposed 10:30, timed out)   [ Apply ] [ Discard ] |
|  create  src/new.py     (proposed 10:30, timed out)   [ Apply ] [ Discard ] |
+-----------------------------------------------------------------------------+
```

- Each parked proposal shows its op type, target path(s), and `[Apply]` / `[Discard]` (FR-39).
- `[Apply]` runs the captured op through the shared file-ops service (the Archetype-B execution path, arch §4.6) with the full sandbox + undo + log path; it is governed by the normal per-op confirm path, **not** the per-turn injection caps (arch §4.6 replay accounting).
- `[Discard]` removes it with a log entry.
- Every queued proposal is in the activity log with the `queued` outcome (FR-39), so the Pending surface is auditable.
- The tab badge `(N)` shows the count so the user notices parked work.
- **There is deliberately no "Apply All N" batch action (llm F5).** Each parked proposal requires its own individual `[Apply]`, keeping every apply a single user-authorized action. A batch apply is not provided because it would raise ambiguity about cap accounting — each `[Apply]` is a separate Archetype-B per-op path (sandbox + before-image + log), governed by the normal per-op confirm and **not** re-entering a per-turn injection cap (arch §4.6 replay accounting). A Phase-8 builder must not add an "Apply All" convenience.
- **`[Apply]` is disabled while a live turn is in flight** (turn-in-flight rule, §6.4) so a replay cannot race a live hook-gated write on the same file.
- Apply-time sandbox check: a parked proposal whose path is no longer under the *current* open-folder root (e.g. the user switched folders) fails the sandbox check at apply-time and is shown blocked — replay cannot escape the current sandbox (arch §4.5/§4.6).

### 6.7 The sandbox-scope display (FR-26, NFR-3 — load-bearing safety legibility)

The AI's write sandbox (the resolved open-folder root) is **always visible** while the AI panel is open (DP-2, NFR-3). **FS-3 Decision B (FINAL, 2026-07-10):** the user-facing label drops "Sandbox" wording in favor of "Document folder:" — the boundary itself (FR-26) is unchanged, only its display wording:

- **In the AI panel header** (region 2): `Document folder: ~/proj  [ scoped ✓ ]` — the resolved root path + a confirmed-armed badge.
- **In the status bar** (so it is visible even with the AI panel collapsed): `Document folder ~/proj ✓`. The status-bar segment is **clickable with a hover tooltip** (web F10, consistent with the interactive `Language ▾`/encoding segments, §3.4): the tooltip reads `AI edits are limited to the document folder. Click to open the AI panel.` so a first-run user understands what "Document folder" means without reading docs (Nielsen H6).
- **In the folder tree:** the root node is labeled `(root)` and visually distinguished (the `accent` token) as the sandbox boundary.

States of the document-folder indicator:

| State | Indicator | Meaning |
|---|---|---|
| Armed | `~/proj [scoped ✓]` (`success`) | Folder open, self-test passed (FR-24), write agency armed |
| No folder | `No folder — AI write disabled` (`muted`) | No open folder → AI file-write agency disabled; read/chat allowed (FR-26 Done-when) |
| Self-test failed | `Document folder could not be armed ⚠` (`danger`) | FR-24 self-test failed → write agency stays disabled; read/chat allowed (FR-24) |
| Degraded | `Degraded — approve-each forced` (`danger`) | No per-op interception (FR-26) → session forced to approve-each (FR-10(c)) |
| Root moved | `Document folder root changed — re-confirm ⚠` (`danger`) | Open folder moved/renamed/deleted at restore → re-prompt before write agency re-arms (§9.2 MA5) |

**Boundary-violation-blocked state (FR-26, SC-5(d)).** When the AI attempts a write **outside** the resolved root (via `..`, an absolute path, or a symlink resolving outside), the write is **refused and logged as blocked** (FR-26). The user sees:

- An inline transcript chip: `⛔ blocked — write outside the document folder: /etc/passwd (refused)` in the `danger` token.
- An activity-log row with outcome `BLOCKED` and reason `outside document folder` (§6.6.2).
- The turn continues (the block is per-op; it does not kill the turn) — but the visible block is the proof the boundary held (SC-5(d)). The document-folder boundary itself never moves; only the AI's failed attempt is shown. There is no "allow anyway" escape hatch for a write outside it.

**Mid-turn degrade announcement (llm F6, FR-10(c) / arch §4.2 step 6).** The `Degraded — approve-each forced` indicator above covers the session-open state. If degrade is detected **after a turn is already in flight** (a hook invocation fails/times out, or the startup-armed hook stops being honored), the transcript shows a chip and the agency state changes immediately:

```
|  ⚠ Backend degrade detected — ops 1–2 applied and logged. Remaining ops |
|     halted. Agency set to approve-each for this session.                 |
```

The **cap meters freeze** at their current state; already-applied ops remain in the activity log and are undoable (§6.6.3); the header immediately updates to the forced approve-each state (§6.2). The remaining ops of the turn are blocked, not silently auto-applied — so a mid-turn degrade never lets an op slip the per-op safety gate.

### 6.8 The no-folder state (FR-26)

With **no open folder**, AI file-write agency is **disabled entirely** (FR-26 Done-when); read/chat still work. The panel shows:

```
+-----------------------------------------------+
|  AI · claude -p ▾ · (write disabled)   [—][×] |
+-----------------------------------------------+
|  No folder open — AI write actions are off.   |
|  [ Open Folder ]  to enable AI file actions.  |
+-----------------------------------------------+
|  You can still ask questions about open docs. |
+-----------------------------------------------+
|  > _                                  [Stop]  |
+-----------------------------------------------+
```

### 6.9 AI-backend unavailable / error / killed states (FR-24, NFR-8)

Every AI failure is **actionable, never silent** (FR-24, arch §8.4). Shown inline in the conversation:

| Condition | What the user sees |
|---|---|
| `claude` CLI not found | `claude CLI not found. Set its path in Settings ▸ AI, or install it. [Open settings]` |
| Installed but not authenticated | `claude is installed but not authenticated. Run claude login, then [Test connection].` |
| Version too old | `claude version too old — needs ≥ 2.1.179. [Test connection]` |
| Document-folder self-test failed | `Document folder self-test failed — AI write actions are disabled. Read/chat still work. [Retry]` |
| Anthropic API key not set | `Anthropic API key not set. Add it in Settings ▸ AI. [Open settings]` |
| Backend hung / runaway | bounded by `--max-budget-usd` + HoardPad timeout (NFR-8); the turn auto-stops with `— stopped (budget/time limit) —`; the editor never froze (off-UI, arch §3.4) |
| User-killed (Stop) | `— stopped —` + `N file operations were applied and can be undone.` (§6.4) |

A `[Test connection]` action (also in Settings, §7.2) runs the FR-24 readiness checks (auth + version + the fail-closed self-test) and reports per-backend success/failure. **Write agency is enabled only after the self-test passes** (FR-24) — until then the panel shows the document folder as not-armed (§6.7).

---

## 7. Settings UX

Realizes FR-35 (settings surface), FR-36 (persistence), FR-22 (hotkey), FR-20 (auto-save interval), FR-37 (theme), FR-8 (default language), and the AI-safety tunables. Settings persist to a single human-readable file (FR-36, arch §8.2); changes take effect without restart where FR-35 requires.

### 7.1 Settings layout

A settings surface (a dialog or a dedicated tab — ODQ-12) with left-nav categories:

```
+--------------------------------------------------------------------------------------+
|  Settings                                                                     [ x ]  |
+--------------------+-----------------------------------------------------------------+
|  • AI              |  AI                                                              |
|    Editor          |  ------------------------------------------------------------   |
|    Checklist       |  Active backend:  ( • claude -p ) ( Anthropic API ) ( Local )    |
|    Appearance      |  Agency mode:     ( • auto-apply ) ( approve-each ) ( read-only) |
|    Safety          |  AI panel hotkey: [ Ctrl+Shift+A ]               [ Change… ]     |
|    Files & save    |                                                                 |
|                    |  -- claude -p ---------------------------------------------     |
|                    |  CLI path:    [ /usr/local/bin/claude        ]  [ Test conn ]    |
|                    |  Status:      authenticated ✓ · v2.1.180 ✓ · self-test ✓        |
+--------------------+-----------------------------------------------------------------+
```

### 7.2 AI provider config (FR-23, FR-24)

Per-backend fields (FR-24 — each backend exposes its required config + a readiness check):

- **claude -p** (Archetype A) — CLI path; a `[Test connection]` running the FR-24 readiness checks (authenticated + version ≥ 2.1.179 + fail-closed self-test); a live status line (`authenticated ✓ · v… ✓ · self-test ✓` or the actionable error, §6.9).
- **Anthropic API** (Archetype B) — API key (masked input, stored in the restricted per-user data dir, NFR-7 — never logged); model name; `[Test connection]`.
- **Local LLM** (Archetype B) — Ollama/LiteLLM endpoint URL; model name; `[Test connection]`.
- Switching the active backend takes effect without restart (FR-23).

### 7.3 Editor preferences

- Default new-document language (FR-8 — default `Plain Text`).
- Tab/indent settings, word-wrap toggle, show-whitespace toggle (standard editor prefs; minimum is the FR-listed set, the rest are conventional).
- Move/duplicate-line and other shortcut bindings (the keymap, §8 — at minimum the AI hotkey is rebindable per FR-22; full rebinding is ODQ-13).

### 7.4 Theme selection (FR-37) + the lockstep switch

- **Theme:** `( ☾ Dark )  ( ☀ Light )` — dark is the first-run default (FR-37). Selecting a theme restyles **all** surfaces in lockstep — editor TextMate colors, chrome, AI panel, checklist (FR-37, arch §3.7).
- **The lockstep guarantee, and its one known residual.** The editor syntax theme + editor background + Markdown preview palette switch **same-frame** (arch §3.7 — the three pixel-identical surfaces). The Fluent **chrome** (menu/toolbar/window frame) may lag by **one frame** on heavy resource trees (arch §3.7 residual). This is acknowledged as a **known cosmetic**, not a bug to design around (DP-6 / §9.7) — the design does not add a loading spinner or a transition to mask it; a single-frame chrome catch-up is below the threshold of a perceived defect. No setting exposes or works around it.
- Editor syntax colors must meet WCAG contrast against the editor background — **≥ 4.5:1 body, ≥ 3:1 large/bold** (FR-37). The concrete HoardPad brand-palette values that satisfy this arrive in Phase 7 `brand.md`; this design requires the **ratios** (§10, §13), not the hex.

### 7.5 Safety tunables (FR-35) + auto-save (FR-20)

- **AI-safety tunables** (FR-35): the "Confirm each AI edit in large/home folders" toggle (default ON — FS-5, docs/decisions.md DR-1, §6.2); the FR-10(a) large-root file-count threshold (default 2,000); the FR-11 create cap (default 25); the FR-38 destructive cap (default 10) + its X% magnitude threshold (default 80%); the FR-40 distinct-files-touched cap (default 20); the FR-24 approve-each no-reply timeout (default ~60 s). Each is editable; **each clamps to a documented safe range** — a cap cannot be set to 0/unbounded in a way that disables the injection backstop (FR-35 Done-when). Invalid values are rejected with a clear message.
- **Auto-save interval** (FR-20): default 5 s idle (debounced), 30 s hard ceiling. Editable; takes effect on next applicable event.
- A note clarifies these are the measured/pinned defaults (SPIKE-3) and are safe-range-tunable, not arbitrary (FR-35).

### 7.6 Settings persistence (FR-36)

- All settings persist to a single human-readable file under the per-user data dir (FR-36, arch §8.2).
- A missing/corrupt settings file **falls back to documented defaults without crashing** (FR-36) — the user is told once: `Settings could not be read; defaults restored.`
- Changes are atomic (no torn settings file on crash, arch §8.2).

---

## 8. Navigation, commands & keyboard map

Realizes FR-3/FR-4 (open/save), FR-6 (line ops), FR-15 (preview toggle), FR-17/FR-18 (find), FR-22 (AI hotkey), FR-37 (theme), NFR-6 (keyboard operability), DP-4. Honors Notepad++ muscle memory where it does not collide with platform norms.

### 8.1 Menu structure

| Menu | Items (representative) |
|---|---|
| **File** | New, New Scratch, New Checklist, Open…, Open Folder…, Save, Save As…, Recent, Close Tab, Exit |
| **Edit** | Undo, Redo, Cut, Copy, Paste, ▸ Line (Move Up, Move Down, Duplicate, Delete) |
| **Search** | Find…, Replace…, Find Next, Find Previous, Find in Folder… |
| **View** | Toggle Markdown Preview, Folder Tree, Toolbar, Activity Log |
| **Language** | (searchable list of the 20 + Plain Text; checkmarks the active) |
| **AI** | Toggle AI Panel, Agency Mode ▸ (auto-apply / approve-each / read-only), Activity Log, Pending Proposals |
| **Checklist** | New Checklist, Checked-item Mode ▸ (Strikethrough / Move to bottom / Hide), Add Item, Move Item Up, Move Item Down, Indent, Outdent, Collapse, Expand |
| **Settings** | Preferences…, Theme ▸ (Dark / Light) |
| **Help** | About, Keyboard Shortcuts |

### 8.2 Command discoverability

- v1 ships **menus + a keyboard map** as the discoverability surface (DP-1, DP-4).
- A **command palette** (fuzzy-search all commands) is a strong UX fit but is **not in the PRD's FR set** — it is flagged as ODQ-14 (proposed: defer to v2 unless cheap; menus + shortcuts satisfy NFR-6 in v1). A "Keyboard Shortcuts" help screen lists the full map.

### 8.3 Keyboard-shortcut map (default; honoring Notepad++)

| Action | Default | FR | Notepad++ alignment |
|---|---|---|---|
| New scratch | `Ctrl+N` | FR-2 | match |
| Open file | `Ctrl+O` | FR-3 | match |
| Save | `Ctrl+S` | FR-4 | match |
| Save As | `Ctrl+Shift+S` | FR-4 | match |
| Close tab | `Ctrl+W` | FR-1 | match |
| Next / prev tab | `Ctrl+Tab` / `Ctrl+Shift+Tab` | FR-1 | match |
| Find | `Ctrl+F` | FR-18 | match |
| Replace | `Ctrl+H` | FR-17 | match |
| Find next / prev | `F3` / `Shift+F3` | FR-18 | match |
| Find in folder | `Ctrl+Shift+F` | FR-17 | match |
| Move line up / down | `Ctrl+Shift+Up` / `Down` | FR-6 | match (Notepad++ `Ctrl+Shift+Up/Down`) |
| Duplicate line | `Ctrl+D` | FR-6 | **deviation note** — Notepad++ uses `Ctrl+D`; some editors use it for "select next". HoardPad keeps Notepad++'s duplicate-line (ODQ-2). |
| Delete line | `Ctrl+Shift+K` | FR-6 | **deliberate split (pm F1)** — Notepad++'s delete-line is `Ctrl+L`; HoardPad takes the VS-Code `Ctrl+Shift+K` and **reserves `Ctrl+L`** for a future "select line / clear line" action, so the binding does not collide. The mixed Notepad++/VS-Code line-op map (`Ctrl+D` from Notepad++, `Ctrl+Shift+K` from VS Code) is a conscious choice folded into ODQ-2 for the maintainer to confirm. |
| Move checklist item up / down | `Alt+Up` / `Alt+Down` | FR-32 | HoardPad-specific (B4 — keyboard equivalent of drag-reorder within the sibling group; active only when a checklist item row has focus) |
| Indent / outdent checklist item | `Alt+Shift+→` / `Alt+Shift+←` | FR-32, FR-41 | HoardPad-specific (ODQ-11 — demote/promote one tier, subtree travels; depth cap = indent past tier 2 is a no-op; active when a checklist item row has focus) |
| Collapse / expand checklist parent | `←` / `→` (or `Space`) on a focused parent | FR-42 | HoardPad-specific (ODQ-11 — `←` collapses, `→` expands a parent's subtree; `Space` toggles; the twisty's keyboard path) |
| Toggle Markdown preview | `Ctrl+Shift+V` | FR-15 | HoardPad-specific (Notepad++ has no MD preview) |
| Toggle AI panel | `Ctrl+Shift+A` | FR-22 | HoardPad-specific; app-scoped, configurable (FR-22) |
| Toggle folder tree | `Ctrl+Shift+E` | §2 | aligns to VS Code "explorer" muscle memory |
| Theme toggle | `Ctrl+Shift+T` | FR-37 | HoardPad-specific |
| Undo / Redo (editor) | `Ctrl+Z` / `Ctrl+Y` | FR-6 | match |
| Undo last AI file op | `Ctrl+Alt+Z` | FR-28 | HoardPad-specific (distinct from buffer undo) |

- **Deliberate deviations** (DP-4): the `Ctrl+D` (duplicate-line, not select-next) and the `Ctrl+Shift+K` delete-line (VS-Code, not Notepad++'s `Ctrl+L`) are the conscious line-op choices folded into ODQ-2 (the reason — `Ctrl+L` reserved for a future select/clear-line — is in the table row above); the AI/preview/theme/folder/checklist-reorder shortcuts are new because Notepad++ has no such features.
- The AI-panel hotkey is **app-scoped** (FR-22, not system-global — Wayland-safe, SC-8) and reconfigurable (FR-22, §7.1).
- Default bindings are the v1 surface; **full user rebinding** of the whole map is ODQ-13 (proposed: rebind the AI hotkey in v1 per FR-22; defer full keymap editing to v2).
- **Known keymap risk — `Alt+Shift+←/→` on linux-x64 (C-2a; binding KEPT, not changed).** The decided indent/outdent binding stays `Alt+Shift+←/→` (ODQ-11 D5). On stock GNOME/IBus and KDE, `Alt+Shift` is a common keyboard-layout-switch modifier and the window manager / input method may consume `Alt+Shift+←/→` before Avalonia sees it. **Mitigations (already specified, no new decision):** the indent/outdent action is **user-rebindable** (the keymap-rebinding path, ODQ-13) and is **always reachable via `Checklist ▸ Indent / Outdent`** (§8.1) and the item-row context menu (§5.2), so nesting never depends solely on the shortcut. **Final binding confirmation on Linux is a Phase-8 build-time check** (verify the chord reaches Avalonia under the target compositors; if a common environment consumes it, surface a default-rebind suggestion — without changing the decided primary binding).

---

## 9. Interaction states (explicit per surface)

The SDLC Phase-6 done-criterion requires loading / empty / error / success states plus the HoardPad-specific states for every surface. This section is the per-surface state table.

### 9.1 Editor surface

| State | What the user sees |
|---|---|
| Loading (cold) | SC-1 budget ≤ 1000 ms; window appears with the file visible + editable; no spinner needed below budget |
| Empty (scratch) | empty editable buffer, `untitled •`, Plain Text (§2.2) |
| Large-file degraded | highlight suppressed + `Plain (large file) ▾` + dismissable strip (§3.6) |
| Dirty / unsaved | `•` marker on tab + title + status bar (FR-5) |
| Save success | `•` cleared; status bar `saved` |
| Save error | non-blocking error with an **actionable reason** + the buffer stays dirty (arch §8.4). The message names the cause and a recovery, matching the §6.9 AI-error pattern (web F6): e.g. `Save failed: disk full. Try Save As to a different location.` / `Save failed: permission denied.` / `Save failed: path no longer reachable (drive removed?).` |
| Recovered-after-crash | see §9.6 |

### 9.2 Markdown surface

| State | What the user sees |
|---|---|
| Source | colorized source (FR-14) |
| Preview (success) | rendered control tree (§4.2) |
| Large preview suppressed | `Preview not auto-rendered (N KB) [Render anyway]` (§3.6) |
| Preview rendering (transient) | for a doc under the preview threshold whose Candidate-A control-tree build is perceptible, the preview area must not flash blank for > one frame; if the synchronous build would visibly blank, a brief loading indicator (or an off-thread render path) is required (web F9 — Phase-8 confirmation item; mechanism pinned synchronous per arch §6, so this is a feel-tuning gate, related to ODQ-6) |
| Toggle round-trip | byte-identical buffer on return (SC-4) |

### 9.3 Checklist surface

| State | What the user sees |
|---|---|
| Empty / first-run | empty-state copy + focused add input (§5.6) |
| Saved (resting) | `✓ saved` (§5.4) |
| Saving (transient) | `… saving` |
| Save degraded (synced folder) | `⚠ save degraded` + tooltip (§5.4, NFR-4) |
| Locked child (completed parent) | descendant rows greyed/disabled (`muted` token), inert to pointer + keyboard, **still showing their own real check marks** (§5.1, FR-41); announced "disabled" to assistive tech (§10.2) |
| Collapsed parent | a `▸` twisty; the parent's subtree is hidden from the visible-row sequence (the descendants remain in the file and, if unchecked, remain active — Gap B); expanding shows `▾` and restores the subtree (FR-42) |
| Recovered-after-crash | reopened with last terminal mutation intact, incl. `parentId` / per-sibling `order` / `collapsed`; a completed parent re-locks its children, and unchecking restores their exact prior state (SC-6a/SC-6c) |

### 9.4 AI panel — the HoardPad-specific states

| State | What the user sees | FR |
|---|---|---|
| Idle (ready) | header shows backend + agency + sandbox armed; empty transcript | FR-22 |
| AI-thinking (streaming) | tokens stream into transcript; `[Stop]` active; cap meters live | FR-24 |
| AI-blocked-by-cap | the cap-reached confirm (§6.5) naming which cap + count; `[Stop]` has initial focus | FR-11/38/40 |
| Approve-each pending | the diff confirm + no-reply countdown; `[Deny]` has initial focus (§6.6.1) | FR-27/24 |
| Approve-each pending (undo-unavailable op) | the over-cap confirm variant — `⚠ undo unavailable` + `[ Approve (irreversible) ]` / `[Deny]` (§6.6.1) | FR-28(iii)/FR-10(b) |
| Boundary-violation-blocked | `⛔ blocked — write outside the document folder` chip + log row (§6.7) | FR-26 |
| No folder | write-disabled state (§6.8) | FR-26 |
| Self-test failed / not armed | `Document folder could not be armed ⚠` (§6.7, §6.9) | FR-24 |
| Degraded backend (session-open) | `Degraded — approve-each forced` (§6.7) | FR-26/10(c) |
| Degraded backend (mid-turn) | the mid-turn-degrade chip (ops 1–N applied/logged, remaining halted, agency→approve-each); cap meters freeze (§6.7) | FR-10(c)/arch §4.2 step 6 |
| Backend error | the actionable error line (§6.9) | FR-24 |
| Killed / stopped (user `[Stop]`) | `— stopped —` + applied-ops-undoable note (§6.4) | NFR-8 |
| Recovered after crash (mid-turn) | on relaunch the transcript shows partial content + `— (turn interrupted) —` + `N file operations were applied and can be undone.` (§6.4, B3); data already durable (PRD §9.3/§9.4) | FR-19/NFR-8 |
| Pending proposals present | `[Pending (N)]` badge (§6.6.4) | FR-39 |

### 9.5 Folder tree

| State | What the user sees |
|---|---|
| No folder | pane hidden; `[Folder]` summons Open Folder |
| Open (armed) | tree with `(root)` labeled as the sandbox boundary |
| Root moved at restore | `Document folder root changed — re-confirm` (§6.7, §9.2 MA5) |

### 9.6 Recovered-after-crash (§9.2 session recovery — SC-7, FR-19)

On relaunch after an unclean exit, the session restores (FR-19): all tabs (incl. unsaved scratch + unsaved on-disk edits), the active tab, per-tab cursor, the open folder.

- A **scratch buffer** reappears with its content + `•` marker (SC-7).
- A **dirty on-disk file whose disk content is unchanged** reopens with its unsaved edits + `•` (FR-19).
- A **dirty on-disk file whose disk content changed while the app was dead** surfaces a **restore-conflict** dialog (FR-19 — prevents silent clobber on next Save):

```
+--------------------------------------------------------------------------------------+
|  Recovery conflict — notes.md changed on disk while HoardPad was closed              |
| ------------------------------------------------------------------------------------ |
|  Your recovered version differs from the file now on disk (another tool, the other   |
|  machine, or a sync daemon changed it).                                              |
|                                                                                       |
|  [ Keep my recovered version ]   [ Keep the version on disk ]   [ Show diff ]        |
+--------------------------------------------------------------------------------------+
```

- A **`.hoard`** reopens with its last terminal mutation intact (SC-6a/SC-6b) — the durability promise (DP-3); no conflict prompt in the normal case.
- If the **open folder (AI sandbox root) moved/renamed/deleted**, tabs restore but the AI write agency is **not** silently re-armed — the user re-confirms/re-picks the root (§9.2 MA5, §6.7).
- **At most one recovered state per buffer (FR-21, pm F2).** Recovery offers exactly one recoverable state per previously-open buffer — the user is never presented two competing recovered versions of the same buffer. A clean save+close leaves **no** recovery artifact, so a cleanly-closed buffer produces no spurious recovery prompt on next launch. (A Phase-8 recovery UI must not list duplicate recovered states for one buffer.)

### 9.7 The lockstep-theme one-frame residual (acknowledged)

On a theme switch, the editor + preview re-theme same-frame; the Fluent chrome may catch up **one frame** later on heavy resource trees (arch §3.7). This is a **known cosmetic**, surfaced here so a coding agent does not treat it as a bug to mask (no spinner, no transition added). DP-6 visible-degrade does **not** apply — a single-frame chrome catch-up is below the perceived-defect threshold; nothing is shown for it.

---

## 10. Accessibility requirements

Realizes NFR-6, FR-37 (contrast as a requirement, not a color). Full screen-reader certification is out of v1 scope (NFR-6), but **no design choice may make later accessibility infeasible** (NFR-6).

### 10.1 Keyboard-only operability (NFR-6, DP-4)

- **Every primary action is keyboard-reachable** (NFR-6): open/save/new/find/replace/toggle-preview/toggle-AI-panel/switch-tab — each has a shortcut (§8.3) and a menu path.
- **Focus order is explicit per surface:**
  - Window: menu → toolbar → tab strip → (folder tree →) editor → status bar; `F6`-class cycling moves between the major panes (editor ↔ folder tree ↔ AI panel).
  - AI panel: header (backend → agency) → sandbox line → transcript (scrollable, read) → cap meters → activity/pending tabs → prompt input → Stop. Tab order follows this top-to-bottom sequence.
  - Checklist: title → mode selector → item rows → add input. **Item rows are traversed in pre-order (the tree's display order):** a parent is reached before its (expanded) children, and a collapsed parent's children are skipped (they are not visible rows — Gap B, §5.5). Within a row the focusables are: twisty (parent only) → checkbox → text → delete. With a row focused, `Alt+Up`/`Alt+Down` reorders within the sibling group, `Alt+Shift+→`/`Alt+Shift+←` indents/outdents (subtree travels), and `←`/`→`/`Space` on a parent collapses/expands (§5.2, §8.3) — so reorder (FR-32), nesting (FR-41), and collapse (FR-42) are all fully keyboard-reachable. A **locked** row (descendant of a checked parent) is reachable for reading but its interactive controls are disabled (announced "disabled", §10.2).
  - Find panel: pattern → replace → toggles → scope → action buttons.
- **Visible focus indication** on every focusable control (NFR-6) — a focus ring using the `focus-ring-color` token (§13, ≥ 3:1 against both `surface` and `surface-alt`), never color-only (also serves the AA non-color-cue requirement).
- The **approve-each confirm and the cap-reached confirm are keyboard-operable and must never require a mouse** — and their **initial focus favors the safe action** (web F4): the approve-each confirm focuses `[Deny]` (§6.6.1) and the cap-reached confirm focuses `[Stop]` (§6.5), so an accidental `Enter` never applies an AI op or authorizes another capped op. Reaching `[Approve]` / `[Allow next …]` requires a deliberate `Tab`+`Enter` or a click.

### 10.2 Screen-reader labeling

- Every interactive control carries an accessible name/label (Avalonia `AutomationProperties`): the dirty marker, the fold affordances ("expand region" / "collapse region"), the checklist checkboxes ("checked"/"unchecked" + item text) and the `Alt+Up`/`Alt+Down` reorder (announced as "moved item up, position 3 of 7"), the agency selector, the cap meters (announced as "files touched 4 of 20"), the sandbox indicator ("sandbox armed, root ~/proj"), the Stop button.
- The activity-log rows are labeled so a screen reader can read op + path + outcome (the safety audit must be screen-reader-legible — DP-2).

**Tree ARIA for the nested checklist (ODQ-11 D11 — on the existing custom control, NOT a stock `TreeView`).** The checklist remains the custom `ItemsControl`-based control (it owns the FR-32 drag-reorder + FR-33 display-mode behavior a `TreeView` does not provide); it is extended with **tree semantics** so a screen reader reads the hierarchy. **Implementation pin (C-5, binding):** each item row exposes an **`AutomationPeer` subclass returning `AutomationControlType.TreeItem` with the numeric level** (the peer's `GetLevelCore`/UIA `LevelProperty`) — **setting `AutomationProperties.Level` in XAML alone is insufficient** for NVDA/JAWS tree navigation, which key off the `TreeItem` control type + numeric level reported by the peer. Per item row, the peer reports:
- **Level** — the tier (1/2/3 for depth 0/1/2), via the `AutomationPeer` numeric level (not XAML-only), announced as "level 2".
- **Position-in-set / set-size** (`AutomationProperties.PositionInSet` + `SizeOfSet`) — the item's place within its **sibling group**, announced as "3 of 5".
- **Expanded / collapsed** (`AutomationProperties.ExpandCollapseState` on a parent) — announced "expanded" / "collapsed"; toggling the twisty announces the new state (FR-42).
- **Disabled** (the locked state) — a descendant of a checked parent exposes `IsEnabled = false` and is announced "disabled" (FR-41).

**Checking/unchecking a parent fans out an `IsEnabled` change to every descendant (validator note) — the announcement model must cover it.** When a parent's `checked` toggles, the completion-lock changes `effectiveEnabled` for the **whole subtree** at once (every descendant's `IsEnabled` flips). The control must not emit one `IsEnabled`-changed announcement per descendant (a flood on a large subtree). Instead it announces the **block-level transition once** — on checking: "section completed, N items locked"; on unchecking: "section reopened, N items active" — as an `assertive` live-region event, with the per-row `disabled`/enabled state then reflected in each row's properties for navigation (read on focus, not broadcast). This keeps the safety-relevant state change legible without flooding (consistent with the §10.2 live-region policy below).

**Live-region announcement policy — four concrete rules (web F5, WCAG 2.1 SC 4.1.3).** A Phase-8 implementer sets the Avalonia `AutomationPeer` live-setting per surface as follows (the rule prevents both flooding and silenced safety events):

1. **Transcript streaming tokens — `polite`, batched at turn-complete only** (not per-token). The implementer announces the final "AI responded" boundary, never each streamed token, so a 20-second stream does not produce a flood of deferred announcements.
2. **File-op chips in the transcript (applied / approved / blocked / undone) — `assertive`, announced individually.** These are safety-relevant discrete events and must interrupt.
3. **Activity-log rows — `polite`, announced on row-append.**
4. **Cap-reached confirm and sandbox-violation chip — `assertive`** (a blocking safety gate or a safety event just occurred).

**Screen-reader labeling for the safety-critical confirms (llm F7).** The **cap-reached confirm** is an `assertive` live-region announcement on appearance: the screen reader reads the full message including **which cap tripped and the count**. The **approve-each diff** is announced as a summary (e.g. "AI proposes to edit src/app.py: 3 lines added, 1 line removed; in the document folder; undoable") — individual diff lines are reachable via navigation but not read aloud by default; the **over-cap variant** announces "undo unavailable — this op cannot be reversed". The confirm buttons carry full semantic labels: `Approve — apply this edit`, `Deny — reject this edit`, `Allow next file — will ask again`, `Stop — end this turn`.

(Avalonia 11.x live-region support is mature on Windows UIA but partial on Linux AT-SPI2; the NFR-6 "full screen-reader certification out of v1 scope" caveat covers cross-OS reliability — the labeling is implemented regardless.)

### 10.3 Contrast as a requirement (not a color, FR-37, NFR-6)

- **Editor syntax colors** vs editor background: **≥ 4.5:1 for body/normal tokens, ≥ 3:1 for large or bold tokens** (FR-37).
- **All UI text** vs its background: **≥ 4.5:1 body, ≥ 3:1 large/bold** (NFR-6).
- These ratios are **requirements on Phase 7 `brand.md`** — the HoardPad brand palette must be tuned to satisfy them in both dark and light themes. This document states the ratio; `brand.md` supplies the values that meet it (§13). A `brand.md` palette that fails these ratios fails FR-37/NFR-6.
- **Status / state is never color-only** — every state that uses a `success`/`danger`/`warning` color token also carries a glyph or text (web F8): the blocked chip has `⛔` + "blocked"; the dirty marker is `•`; the `✓saved` indicator has the `✓` glyph + "saved"; the **cap `warning`/`danger` meter shows the count (e.g. `touched 18/20`), not just a color shift**; the downgrade badge carries its reason text. This keeps state legible for color-blind users and is the design rule that satisfies the "no design choice precludes accessibility" clause.

---

## 11. Notepad++ parity matrix

Grounded in `notepad-plusplus-features.md`. Each feature: **match** (v1 parity), **adapt** (delivered differently), or **drop-for-v1** (deferred per PRD §4.3 / §12).

| Notepad++ feature | v1 decision | Where / why |
|---|---|---|
| Syntax highlighting & folding | **match** | FR-7 (20 langs, TextMate) + FR-9 (folding); §3 |
| User-defined language for unrecognized formats | **adapt** | FR-8 manual override + bundled supplemental grammars (FR-7); no full UDL editor in v1 |
| Column/block editing | **drop-for-v1** | PRD §4.3 deferred |
| Auto-completion (word/function/param) | **drop-for-v1** | PRD §2 explicit deferral — AI host in, autocomplete out (v2) |
| PCRE regex find/replace | **match** | FR-17/FR-18; §3.5 |
| Function list | **drop-for-v1** | PRD §4.3 deferred |
| Document map (minimap) | **drop-for-v1** | PRD §4.3 deferred |
| Bookmarking | **drop-for-v1** | not in v1 FR set (ODQ-15 — small, candidate add) |
| Tabbed interface | **match** | FR-1; §2 |
| Multi-view (split) | **drop-for-v1** | PRD §4.3 deferred; §4.2 swap-not-split |
| Synchronized scrolling | **drop-for-v1** | PRD §4.3 deferred; §4.4 |
| Macro recorder | **drop-for-v1** | PRD §4.3 deferred |
| Auto-save / crash protection | **match** | FR-20/FR-21; §9.6 |
| Session management (reopen tabs/state) | **match** | FR-19; SC-7; §9.6 |
| Plugin admin / extensibility | **drop-for-v1** | PRD §4.3 deferred (no plugin system v1) |
| Markdown rendered preview | **adapt (HoardPad addition)** | FR-15/FR-16 — Notepad++ has none; §4 |
| Agentic AI panel | **adapt (HoardPad addition)** | FR-22…FR-29 — the wedge; §6 |
| Native `.hoard` checklist (nestable, completion-lock) | **adapt (HoardPad addition)** | FR-30…FR-34, FR-41/FR-42; §5 |

---

## 12. Traceability

Each design section → the FR(s) / arch §(s) it gives a surface. The basis the review round + the Phase-8 build check against.

| Design section | Realizes |
|---|---|
| §2 Window shell & layout | FR-1, FR-3/4/5, FR-6 (status bar), FR-22, FR-37; arch §1.4, §6 |
| §3 Editor surface | FR-1, FR-6, FR-7, FR-8, FR-9, FR-14, FR-17, FR-18, NFR-9; arch §3.3, §6 |
| §4 Markdown experience | FR-14, FR-15, FR-16, SC-4; arch §6 (Candidate-A render tree) |
| §5 Checklist UX | FR-30, FR-31, FR-32, FR-33, FR-34, FR-41, FR-42, SC-6 (incl. SC-6c), §9.1 (v2); arch §5.1, §6.5 |
| §6 AI panel | FR-22…FR-29, FR-10, FR-11, FR-38, FR-39, FR-40, SC-1/2 (stop), NFR-3, NFR-8, NFR-10; arch §4 |
| §6.5 cap indicators (four pinned parameters) | FR-11 (created/25), FR-38 (destructive/10) **+ FR-38 X% firing-threshold/80%**, FR-40 (touched/20); arch §4.4 (conversation-keyed cap-state), per-archetype enforcement arch §4.1/§4.5 |
| §6.6 approve-each + log + undo + pending | FR-27, FR-24, FR-28, FR-29, FR-39; arch §4.2, §4.5, §4.6 |
| §6.7 sandbox display + block state | FR-26, NFR-3, SC-5(d) |
| §7 Settings | FR-23, FR-24, FR-35, FR-36, FR-20, FR-37, FR-8; arch §8.2 |
| §8 Navigation + keymap | FR-3/4, FR-6, FR-15, FR-17/18, FR-22, FR-28, FR-37, NFR-6 |
| §9 Interaction states | SDLC Phase-6 done-criterion; FR-19, SC-6/7, arch §3.7 |
| §10 Accessibility | NFR-6, FR-37 (contrast as requirement) |
| §11 Parity matrix | `notepad-plusplus-features.md`, PRD §4.3/§12 |
| §13 Semantic tokens | FR-37 (palette-agnostic until Phase 7 brand.md) |

---

## 13. Semantic design-token vocabulary

This document names color/emphasis by **role**, never by value. Phase 7 `brand.md` (root, the canonical source of truth for every bound value) binds each role to a concrete HoardPad brand-palette value (dark + light) that satisfies the §10 contrast ratios. The Phase-8 build references these role names; it must not hardcode hex. The bound values below are folded in from `brand.md §1` (T7.3); **root `brand.md` remains canonical** — if the two ever diverge, `brand.md` and its `scripts/check_contrast.py` proof win.

Each row carries a **contrast requirement** stated as a ratio against a named peer token (web F3, B5); the Dark + Light columns give the bound value that meets it (proven in `brand.md §9`). Contrast is always a ratio against a named background, never a freestanding value.

| Token | Role | Used by | Dark (default) | Light (alternate) | Contrast requirement |
|---|---|---|---|---|---|
| `surface` | primary background of editor/panel | editor area, panels, status bar | `#16131f` | `#fbfaf7` | (background — peers contrast against it) |
| `surface-alt` | secondary background (tree, toolbar, headers, AI overlay) | folder tree, toolbar, AI header + overlay background | `#211d2e` | `#fffefb` | (background — peers contrast against it) |
| `overlay-surface` | AI-panel overlay background (web token gap) | the §6.1 AI overlay if `surface-alt` is not distinct enough against the editor `surface` | `#6b6180` | `#9888af` | ≥ 3:1 *against* `surface` (visually distinct as an overlay); text on it ≥ 4.5:1 |
| `on-surface` | primary foreground text | body text, item text, transcript | `#f2effa` | `#1b1a22` | ≥ 4.5:1 against `surface` and `surface-alt`/`overlay-surface` |
| `muted` | de-emphasized text/icons | gutter line numbers, hints, "saved" resting | `#948fa6` | `#6e6a78` | ≥ 4.5:1 against its background (≥ 3:1 if large/bold per FR-37) |
| `gutter` | line-number + fold gutter foreground (on a gutter background) | §3.1 | `#948fa6` | `#6e6a78` | gutter foreground ≥ 4.5:1 against the gutter background |
| `selection` | text-selection background | editor selection | `#3a3358` | `#d8d0ea` | selected text (`on-surface`) ≥ 4.5:1 against `selection` |
| `primary` | primary action / active accent | active tab, primary buttons | `#b79ad6` | `#5a3f78` | text/glyph on `primary` ≥ 4.5:1; `primary` UI mark ≥ 3:1 against adjacent surface |
| `accent` | secondary highlight | links (FR-16), `(root)` sandbox label | `#6ea8da` | `#2f5d8f` | ≥ 4.5:1 against its background |
| `success` | positive state | `✓ saved`, `applied`/`approved`, diff `+` additions | `#45c98a` | `#14794a` | ≥ 4.5:1 against its background (≥ 3:1 large/bold) |
| `danger` | destructive / blocked / error state | `⛔ blocked`, diff `−` removals, over-cap confirm, `save degraded` | `#ff6b63` | `#c23934` | ≥ 4.5:1 against its background (≥ 3:1 large/bold) |
| `warning` | caution short of error | cap meter at 60–80% of cap, downgrade badge | `#f6b73e` | `#9a5a00` | ≥ 4.5:1 against its background (≥ 3:1 large/bold) |
| `focus-ring-color` | keyboard focus indicator color (a ring/outline treatment uses this color) | every focusable control (§10.1) | `#6ea8da` | `#2f5d8f` | ≥ 3:1 against **both** `surface` and `surface-alt` (WCAG 2.1 SC 1.4.11 non-text contrast); the bound value clears both backgrounds in each theme (`brand.md §9` pairs 13–16) since a focused control may sit on either background |

> All hex above is bound from `brand.md §1` and proven in `brand.md §9` (`scripts/check_contrast.py`, WCAG 2.1). The gutter background the `gutter` foreground draws on is `gutter-bg` `#1b1828` (dark) / `#f4f2ef` (light) — a `brand.md §1.2` supporting token, not a §13 role. Dark is the first-run default; light is the documented alternate (FR-37, §7.4).

Rules:
- `focus-ring-color` is a **color role**, not the visual treatment — the ring/outline pattern is the App-layer component; this token is only the color it draws with (web F3). The bound `#6ea8da` (dark) / `#2f5d8f` (light) clears ≥ 3:1 against both `surface` and `surface-alt` in its theme.
- Every state that uses `success`/`danger`/`warning` **also carries a glyph or text** (§10.3) — color is never the sole signal.
- The AI overlay background: this conditional resolved to a **distinct `overlay-surface`** (T7.2, `brand.md §1.1`). `surface-alt` vs `surface` computes to only 1.12:1 (dark) / 1.03:1 (light) — below the ≥ 3:1 SC 1.4.11 distinctness floor — so the §6.1 AI overlay uses the distinct `overlay-surface` (`#6b6180` dark / `#9888af` light), which clears 3.18:1 / 3.11:1 against `surface` while keeping `on-surface` body text ≥ 4.5:1 on it. `surface-alt` remains the token for surfaces that sit beside (not over) the editor.
- The token set is intentionally small; `brand.md` may add brand-specific accents but must keep these roles + contrast requirements stable so the Phase-8 build's bindings do not churn.

---

## 14. Open design questions

Each with a proposed default in the PRD/architecture house style so none blocks Phase 7. These are design-shaping, not scope decisions (scope is the PRD; pinned HOW is the architecture).

- **ODQ-1 — In-folder replace: review-before-write default.** §3.5 shows a review surface before an in-folder replace writes. **Proposed default: review-before-write ON by default** (matches PRD OQ-2's intent; FR-18 "can be reviewed before write"). A "skip review" toggle is available for trusted bulk replaces.
- **ODQ-2 — line-op bindings (`Ctrl+D` duplicate, `Ctrl+Shift+K` delete, `Ctrl+L` reserved).** HoardPad keeps Notepad++'s `Ctrl+D`=duplicate-line (not the VS Code select-next) but takes the VS-Code `Ctrl+Shift+K` for delete-line, **reserving `Ctrl+L`** (Notepad++'s delete-line) for a future select/clear-line action so the bindings do not collide (pm F1). **Proposed default: this mixed map (`Ctrl+D` Notepad++, `Ctrl+Shift+K` VS-Code, `Ctrl+L` reserved).** Surfaced for the maintainer to confirm the conscious split; revisit if their muscle memory wants Notepad++'s `Ctrl+L` delete-line back.
- **ODQ-3 — AI-panel default hotkey.** §8 proposes `Ctrl+Shift+A`. **Proposed default: `Ctrl+Shift+A`, app-scoped (FR-22), user-reconfigurable.** Confirm no collision with a more-wanted binding.
- **ODQ-4 — AI panel: overlay vs docked default.** FR-22 mandates an in-window overlay; §2.4 offers a pin-to-dock option. **Proposed default: overlay by default, pin-to-right-dock available.** Confirm which feels right for daily use.
- **ODQ-5 — Encoding-change UI granularity.** §3.4 specifies display + reinterpret/convert actions. **Proposed default: status-bar display + a reinterpret menu + a convert-on-save menu** (minimum for FR-6). Full encoding picker depth is Phase-8-confirmable.
- **ODQ-6 — First-colorize latency feel.** §3.2: lazy grammar registration pays a one-time first-colorize cost (~150 ms, SC-2). **Proposed default: render the buffer as plain text immediately, then colorize when the grammar resolves — no flash long enough to read as a glitch.** Phase-8 confirms the feel against the measured delta.
- **ODQ-7 — Multi-window.** v1 is single-window (PRD §1). **Proposed default: single window v1; multi-window deferred** (each launch its own process unless single-instance is built, arch OQ-ARCH-5).
- **ODQ-8 — Markdown split + sync-scroll.** §4.2/§4.4: v1 ships swap, not side-by-side split or synced scroll. **Proposed default: swap-only v1; split + sync-scroll deferred (PRD §4.3).**
- **ODQ-9 — Telemetry (recorded decline).** No telemetry, ever, for v1 (C-6/NFR-7 hard constraint; arch OQ-ARCH-6). **Proposed default: no telemetry; success validated locally against PRD §5.** Documented decline, not an oversight.
- **ODQ-10 — Checklist edit undo stack.** §5.2: checklist item add/edit/delete — does it get its own undo distinct from the AI file-op undo? **Proposed default: a per-checklist in-list undo for the last few item mutations (add/delete/check/reorder), separate from AI undo.** No architectural home pinned — confirm in Phase 8 (small). **Note (pm F5):** any in-list undo is itself a **terminal mutation** that must re-persist through the FR-31 write queue (zero-debounce), so it stays inside the FR-31 durability invariant and cannot reintroduce a state-loss path.
- **ODQ-11 — Checklist nesting / indent-outdent — RESOLVED 2026-06-20 (nesting IS in v1; completion-lock model).** The maintainer reopened ODQ-11 (overruling the Phase-6 flat-for-v1 adjudication) and the design was decided in `docs/odq11-nested-checklists-plan.md` (D1–D11), then folded into the PRD (FR-41/FR-42 appended; FR-30/31/32/33 + SC-6 + §9.1→v2 amended; re-signed off 2026-06-20), the architecture (§5.1 store + §6.5 control), the tech-design (§3.4/§3.5/§5.7), and this design (§5.1/§5.2/§5.3/§5.5/§5.6 + §8.1/§8.3/§9.3/§10). **The decisions:** D1 flat `items[]` + `parentId` + per-sibling `order` (preserves FR-31); D2 completion-lock (`checked` = "completed" uniformly; checking a parent locks its subtree); D2a manual-anytime parent-check; D3 lock retains + restores child state exactly; D3a children show their real marks while greyed; D4 max 3 tiers (tier-4 indent is a no-op); D5 `Alt+Shift+←/→` indent/outdent, drag-to-nest deferred to v1.1; D6 completed blocks participate in the display modes as a unit; D7 durable `collapsed` twisty; D8 delete-non-empty-parent prompts subtree-vs-promote; D9 reparent/move carries the subtree; D10 `schemaVersion` 1→2 migration; D11 tree ARIA on the existing custom control (not a stock `TreeView`). No FR/SC id was renumbered; FR-31 durability is preserved (one flat array, one whole-file atomic rewrite per mutation, new mutations zero-debounce terminal).
- **ODQ-12 — Settings: dialog vs tab.** §7.1. **Proposed default: a settings tab (consistent with the single-window, tab-centric model)** over a modal dialog. Either satisfies FR-35/36.
- **ODQ-13 — Full keymap rebinding.** §8.3. **Proposed default: rebind the AI hotkey in v1 (FR-22); defer full keymap editing to v2** (menus + fixed defaults satisfy NFR-6).
- **ODQ-14 — Command palette.** §8.2 — strong UX fit, not in the FR set. **Proposed default: defer to v2 unless cheap; v1 ships menus + shortcuts + a Keyboard Shortcuts help screen (NFR-6 satisfied).**
- **ODQ-15 — Bookmarking.** §11 — a small Notepad++ parity item not in the FR set. **Proposed default: drop-for-v1; v2 candidate.**

---

## 15. Reviewers required

**Phase-6 review COMPLETE and folded into v2.** The four-specialist review (`senior-csharp-desktop-developer`, `llm-integration-engineer`, `web-designer-ux`, `project-manager`) is done — all verdicts APPROVE-WITH-CHANGES, zero hard BLOCK; all 5 blockers + all should-fix items are resolved in v2 (see the v2 review-resolution log near the top). Reports are at `docs/review-phase6/`. The `unit-test-engineer` review listed below was not part of the dispatched four-agent round; it remains a recommended gate before the maintainer's sign-off. The original REVIEW_REQUEST block is retained for the record:

```
REVIEW_REQUEST: senior-csharp-desktop-developer | §2,§3,§4,§5,§6 | Confirm every screen composes only the arch §6 M18 controls (FR-1 tab reorder, FR-6 line ops, FR-7/9 editor, FR-15 Candidate-A preview, FR-30/32 checklist, FR-22 in-window overlay) and introduces no control or layout that has no architectural home; flag any UX that would force a non-Avalonia or non-overlay implementation.
REVIEW_REQUEST: llm-integration-engineer | §6 | Validate the AI-panel UX faithfully exposes the arch §4 two-archetype model, the four pinned caps (FR-11/38/40 + X%), the conversation-keyed cap-state no-re-arm behavior, the synchronously-blocking approve-each confirm + no-reply fail-closed/FR-39 queue, the instant-stop process-tree kill, and the sandbox-armed/degraded/blocked states — without misrepresenting enforcement strength or re-deciding any pinned cap/flag.
REVIEW_REQUEST: web-designer-ux | §9,§10,§13 | Confirm the per-surface interaction-state coverage is complete, the focus-order/keyboard-only/screen-reader requirements are sufficient and implementable, and the semantic-token vocabulary is coherent and palette-agnostic (no value leakage Phase 7 brand.md would have to undo).
REVIEW_REQUEST: project-manager | §11,§12,§14 | Verify the parity matrix and traceability map every PRD FR with a UI surface to a screen/flow with no scope drift, and that every Open Design Question (esp. ODQ-11 checklist nesting, which exceeds the §9.1 schema) is a genuine open item with a defensible default rather than silent scope expansion.
REVIEW_REQUEST: unit-test-engineer | §9,§12 | Confirm the interaction states named here are each observable/assertable (so the Phase-8 SC-4/SC-5/SC-6/SC-7 gates and the cap/blocked/recovered states can be exercised against a concrete UI signal), flagging any state that is described but not testably surfaced.
```

## Awaiting review

The four-agent Phase-6 review is folded into v2 (above). **ODQ-11 (checklist nesting) is RESOLVED 2026-06-20** — the completion-lock nested design (D1–D11) is authored into §5/§8/§9/§10 of this v3, consistent with the PRD v2 schema (FR-41/FR-42, §9.1 schemaVersion 2), the architecture, and the tech-design; the `senior-csharp-multi-platform` validator's GO-WITH-GAPS (Gap B collapsed-row reading, Gap D clamp example) was closed in the contracts and reflected here (§5.5). **Recommended re-review (orchestrator dispatches): re-spin the four Phase-6 reviewers scoped to the checklist surface (§5 + §8.1/§8.3 + §9.3 + §10), reports to `docs/review-odq11/`** (per the ODQ-11 plan §5). The earlier `unit-test-engineer` state-observability gate (§9/§12) still stands, now covering the new locked-child / collapsed / completion-lock states. Per the council-review protocol, a per-section `/council` pass is recommended before final sign-off.
