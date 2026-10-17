# RnSLootSlots

A Rabbit and Steel mod that raises what a player can carry by **sharing the three potion slots**:
loot items 7–9 are placed into the potion squares instead of a seventh loot square, and the game's
potion wipe at an area transition is **cancelled**, so potions — and the loot sharing their slots —
survive into the next area.

Built on [RNSReloaded](https://github.com/notnite/rnsreloaded/) (Reloaded II), the same stack as
RnSElites. An independent project: it shares no code with RnSElites beyond the vendored interface
project and the config plumbing.

## Verified in game (2026-10-06)

Confirmed against a live game by the user:

- **Loot items 7–9 land in the potion squares and behave normally.** With six loot items held,
  taking a seventh from a loot table rescues it into a free potion square: the log shows the cell
  written and laid out at the potion row's real coordinates, and the item appears and works in the
  visible bar.
- **Potions survive area transitions** with `Keep Potions Across Areas` on — no wipe.
- **Store purchases are not rescued**: only the loot-table flow is (source row 1), so a bought gem
  upgrade can no longer be moved into a potion square.
- **Not yet covered: the Toybox.** Its own capacity check refuses a seventh grant *before* any pickup
  call, so it still stops at six loot / three potions — that path is unfinished (see the probe log
  below for how far the search got).

## Status: functional (single player verified)

The two features are on by default:

1. **Share Potion Slots** — a loot item picked from a loot table while the loot row is full is placed
   into a free potion square. The pickup detour brackets the call with occupancy counts of the loot
   and potion rows; when nothing landed and the loot row was full, the item is written into the free
   cell of the potion row (`slots[15+player]`) and `scr_itemsys_setpos_potion` lays it out. Only the
   loot-table flow (pickup source row 1) is rescued; store purchases are left exactly as vanilla.
2. **Keep Potions Across Areas** — `scr_itemsys_erase_potions` is suppressed, so the wipe at a stage
   transition does not run and potions survive.

Probe Mode (off by default) re-enables the whole diagnostic layer built while the mechanism was
being measured: script-call logging with argument and field dumps, the pickup outcome lines, the
`itemData` definition-row dumps, and the code-name trace that names whatever refuses a Toybox grant.
The measured facts and the search history are in the sections below.

## Repo layout

```
RnSLootSlots/                  C# mod
  Mod.cs                       entry point; installs hooks once RNSReloaded is Ready
  LootDirector.cs              hooks, probe logging, the potion-wipe cancel
  GameRefs.cs                  null-tolerant readers over game globals and instances
  Config/                      Reloaded JSON config (Config.json, written on first run)
  ModConfig.json               Reloaded manifest
Publish/                       release tooling and the mod-page README/CHANGELOG
RNSReloaded.Interfaces/        vendored upstream interface project (see Provenance)
```

## Build

```bash
dotnet build RnSLootSlots/RnSLootSlots.csproj
```

`RELOADEDIIMODS` is set on this machine (`C:\Users\Tony\Desktop\Reloaded-II\Mods`), so the build
deploys straight into the loader's folder; without it, output falls back to `out/RnSLootSlots`.

**Enabling it:** Reloaded refuses to start the game when an enabled mod's dependencies are disabled
(and fails without writing a log). Enable **RnSLootSlots** in the launcher together with
`reloaded.sharedlib.hooks`, `Reloaded.Memory.SigScan.ReloadedII` and **RNSReloaded**. Enable state
lives in `Apps\rabbitsteel.exe\AppConfig.json` → `EnabledMods`; editing it while the launcher is open
gets overwritten.

**Config:** the persisted file is `Reloaded-II\User\Mods\RnSLootSlots\Config.json` — not the copy in
the mod folder. It is watched and reloads live; the DLL still only loads at game start.

## Publishing

Published on the official Reloaded NuGet feed as **RnSLootSlots**:

- Package page: <https://packages.sewer56.moe/packages/rnslootslots>
- Feed: `https://packages.sewer56.moe/v3/index.json` — preconfigured in Reloaded; the launcher's
  **Download Mods** search picks the package up after its search index rebuilds.
- `ModConfig.json` carries `PluginData.NuGet.DefaultRepositoryUrls`, so installed copies update
  from the same feed.

The upload key is bound permanently to the first upload of the package and cannot be reset. It is
stored in `Publish/NuGetApiKey.txt` (gitignored); back it up.

`Publish/Publish.ps1` builds, stages and packages a release, using the packaging library that
ships with the launcher (`Reloaded.Mod.Loader.Update.Packaging.dll`) — the same output the
launcher's Publish dialog produces. Add `-Upload` to push it and `-Version 1.4.0` to bump the
manifest first. Update `Publish/README.md` and `Publish/CHANGELOG.md` before each release.

```powershell
.\Publish\Publish.ps1                  # build + package
.\Publish\Publish.ps1 -Upload          # build + package + upload
```

## What the first probe run established (2026-10-06)

- **The hotbar is four rows**, confirmed by the `set_itemsys_slot` init sequence: row 0 (1 slot),
  row 1 (4 slots — the abilities), **row 2 (6 slots — the loot row)**, **row 3 (3 slots — the potion
  row)**. The row key in those calls is `4 × rowId + 3`; the slot instance's own `rowId` matches.
- **Storage hangs off the itemsys controller**: the `self` of `setpos_loot` / `erase_potions` carries
  `slots`, `lootCount`, `handRowSize`, `rowSize`, `highlightRow` / `highlightSlot`. The per-player
  hotbar object (`scr_hotbarsyspl_update_from_itemsys`'s `self`) carries `hotbar`.
- **The potion wipe fires at stage changes, not at every hallway notch**: `erase_potions` ran 4 times
  against 25+ `move_next` transitions, each time ~1 s after a transition (e.g. `08:09:10` transition
  → `08:09:11` wipe). It takes no arguments (`argc=0`) and runs on the controller.
- **The loot table flow**: `populate_loot` → `open_loot_table` → `populate_loot_end(N)` (N = 3 or 5
  offered items) → the player picks → `erase_loot_table` → `pickup` + `pickup_loot` (same args,
  paired). Pickup args are `(0, rowSpec, index)` with rowSpec ∈ {1, 2}; which row each spec maps to is
  what the container dumps in v0.2 resolve.

Still missing before the placement reroute can be written: the **contents** of `slots`/`hotbar`
(which square holds what), the exact mutation a pickup performs, what the wipe clears, and the
full-row behaviour. v0.2 captures all four.

## What the second probe run established (2026-10-06, v0.2)

- **The itemsys grid is 19 rows**: `[0..2]` special (row 0 is the pickup staging cell, one cell),
  `[3..6]` the four players' hand rows, `[7..10]` their ability rows, `[11..14]` their **loot rows
  (6 cells each)**, `[15..18]` their **potion rows (3 cells each)**. A cell is
  `[type, dataId, x, y, targetX, targetY, sqId, …]`; empty cells carry `dataId = -1`. The hotbar
  slots reference the rows through `itemRowId` (hand 3, abilities 7, loot 11, potions 15 for player 0).
- **The pickup mechanism**: for every pick the game calls `place_item_in_row(itemId, 0)` — staging
  the item in row 0 — then `pickup(player, 0, 0)`, whose **return value is the picked item's
  dataId**. The pickup moves the staged item into the player's loot row, or into the potion row when
  the item is a potion (measured: one potion pick landed in row 15).
- **The silent discard confirmed at the data level**: with six loot items held, a seventh pickup
  cleared the staging cell and added nothing anywhere — no row's occupancy changed
  (`loot 6->6`, `potion 1->1`). Exactly the reported behaviour.
- Row occupancy per player is readable as `rowSize[11+player]` / `count of cells with dataId >= 0`,
  and the controller's `handRowSize` mirrors the local player's loot count.

## First normal-run session (2026-10-06, v0.9) — what failed, and why

- **`Keep Potions Across Areas` is verified in game**: potions survived an area transition. ✅
- The rescue fired 7 times, but **nothing landed**: the log's before/after dumps are identical around
  `place_item_in_row(item, 15)`. The function does take the row id as its second argument (the game
  uses it to place loot-table/store offers into their rows), so the refusal is an **item-type rule**:
  a loot item cannot be placed into the potion row through it.
- **A false positive that could have granted free items**: several "discards" happened with only 4/6
  loot held — they were store purchases refused for another reason (no gold), and the old rescue would
  have granted them. Only the placement being a no-op prevented that. v1.0 adds the guard: a discard
  is only treated as overflow when the loot row is actually full.

## Gem incident (2026-10-06, v1.0) and v1.1's response

A store **gem upgrade** was wrongly moved into a potion square. The purchase went through the same
`pickup` call, landed in neither the loot nor the potion row (upgrades do not belong to either), and
with the loot row full the guard passed — so the v1.0 cell write placed it. "Did the row change?"
cannot tell a capacity overflow from a purchase that goes somewhere else entirely.

v1.1 therefore:

- **rescues only the loot-table flow** (`pickup`'s source-row argument, row 1; the store is row 2).
  Whatever a store sells, it is not rescued unless the item's own type says it is loot — and
  identifying that needs the definition row, which is why:
- **every pick now dumps its definition row** (`itemData[itemId] cols=…`, bounded to 25 per session).
  The game's weapon-type vocabulary (`weapontype_loot`, `weapontype_potion`, …) has **no upgrade
  entry**, so the value that separates loot from potion from upgrade is a column of this row; one
  session's dumps identify it, and the store flow can then be rescued on the item's type instead of
  being skipped.

## v1.0: guard + direct cell write

- The rescue now runs only when the loot row holds `LootRowCapacity` (6) items.
- Placement no longer goes through `place_item_in_row`. The free potion cell's `dataId` element is
  written in place — preserving the element's own RValue representation, refusing to touch a cell that
  is not empty — followed by `scr_itemsys_setpos_potion(player)`; the caller's own hotbar-refresh calls
  run afterwards and are what should pick the cell up.
- Every write logs a read-back (`wrote item … reads back as …`) so the next session shows whether the
  cell took the value and whether the hotbar followed.

## Phase 3 prototype (v0.3, experimental)

With **Rescue Discarded Loot** enabled, the pickup is bracketed by occupancy counts of the player's
loot and potion rows. When neither gained a cell (the item was discarded) and the pickup returned a
positive item id, the item is placed into a free potion-row cell by calling
`scr_itemsys_place_item_in_row(itemId, 15 + player)`, followed by `scr_itemsys_setpos_potion(player)`
to lay the row out. The detour runs inside `pickup`, i.e. **before** the caller's own hotbar-refresh
calls, so the game syncs the rescued cell to the hotbar itself.

What the next run must show: a rescued item appearing in a potion square (`after rescue` dump shows
an occupied `[3, itemId, …]` cell in row 15), and the potion hotbar slot rendering it.

## What the third session established (2026-10-06, v0.3, Toybox)

- **The Toybox has its own capacity check with a prompt**, and it refuses *before* any pickup: with
  six loot items held, granting a seventh produced the `eff_toybox_inventory_full` message and **no
  `scr_itemsys_pickup` call at all**. Per-category caps: loot stops at 6, potions stop at 3.
- **Grants that fit go through the normal pickup path**: six loot grants ran `pickup` and landed in
  the loot row (0→6), then three potion grants ran `pickup` and landed in the potion row (0→3) —
  each logged as `stored` by the pickup detour. So the toybox's block is a check in its own
  selection path, not a different storage route.
- Consequence for the mod: raising the cap in the Toybox means hooking the toybox selection path
  (`scrc_c_toybox_select` → `scrc_h_toybox_select`) to fall through to a free potion square instead
  of the refusal, the same way the pickup detour rescues a discarded loot table pickup. v0.4 adds
  probes on that path.

### v0.4 probe additions

`scrc_c_toybox_select`, `scrc_h_toybox_select`, `scr_toybox_page_setup_items`,
`scr_itemsys_open_toybox` are now observed; the startup line also reports `rescue=`. The run to do:
in the Toybox, fill the loot row to six and try a seventh — the log should show which script runs at
the refusal and with which arguments, which is what the toybox-side fix will redirect.

**Result of that run (2026-10-06):** the `scrc_*_toybox_select` handlers never fired — the toybox
grant/refusal path is something else. So v0.5/v0.6 go looking for it the other way round:

- **v0.5** added a code-name ring buffer fed by `OnExecuteIt` (which fires for every script *and*
  object-event call) and hooks on the five `scr_effect_play*` scripts. Result: the refusal effect's
  key does **not** arrive as a string argument to the play script, so that trigger never matched.
- **v0.6** triggers on the **code name** instead: the refusal effect object raises its own Create
  event through the same dispatch, so a name containing `inventory_full` fires the dump of the last
  80 calls. Every toybox-flavoured code name is also reported once (`toybox code seen: …`, capped),
  so whichever object event handles the grant gets named even without the trace.

The run to do: restart, fill the loot row to six in the Toybox, try a seventh, and read the
`code trace` block (or the `toybox code seen` lines) — that names the check to redirect.

**Result of the v0.6 run (2026-10-06):** the toybox's scaffolding objects were named
(`obj_notchExToybox`, `obj_itemToyboxDeco` with Step/Draw/Other_10/Other_11 events), but the refusal
itself added **no new code name** — so the handler is one of the already-seen ones, and the
effect-name trigger never fired because the refusal effect is not a named object event. v0.7 therefore
dumped the trace on the rare events surrounding the click instead: the toybox's `Other_*` user events
and the inventory-square click-feedback scripts, budget raised to 25.

**Result of the v0.7 run (2026-10-06):** those dumps contained only the per-frame Step/Draw loop —
and the refused click produced **no script call at all** (no `place_item_in_row`, no `pickup`, no
`loot_amount`). The refusal is decided inline in some object event's body, so no script hook can see
it. The effect-play calls were captured with arguments `(<Ref>, x, y, milliseconds)` — the effect
identity is a Ref, not a name.

v0.8 therefore dumps the trace from **inside the effect-play call**: OnExecuteIt reports a code name
*before* its body runs, so the last entry in the ring buffer at that moment is the event whose body
called the effect — i.e. the inline handler, named. The per-frame triggers were removed so the dump
budget is spent on the six-odd effect calls a session instead of the frame loop.

## Running the probe

Probe Mode is **off by default**; turn it on (config, live-reloaded — the DLL only at game start) to
collect diagnostics. Then play a run and:

- fill the loot row to 6 and take a 7th from a loot table (the rescue logs one line per rescue),
- take an 8th, then a 9th with every potion square occupied (left discarded),
- carry a potion across a stage boundary.

Read `%APPDATA%\Reloaded-Mod-Loader-II\Logs\<timestamp> ~ RabbitSteel.log`; every line from this mod
is prefixed `[RnSLootSlots]`. With probing on you get: per-script call lines with arguments and
`self` fields, `before`/`after` container walks around pickups and the potion wipe, pickup outcome
lines (`stored` / `discarded`), `itemData[id] cols=…` definition rows, and `code trace` blocks around
effect plays (the Toybox search).

## Remaining work

1. **Toybox support.** The Toybox's own capacity check refuses a seventh grant *before* any pickup
   call, so it still stops at six loot / three potions. The check is inline in an object event body
   (`obj_itemToyboxDeco`'s user events are the suspects — its effects are played from there), which no
   script hook can see; the v0.5–v0.9 rounds narrowed it to that object and left the probe
   infrastructure (code trace + `deco fields` dumps) in place for whoever picks it up.
2. **Multiplayer** is untested. Inventories synchronise, so every client should run the same mod
   version; `Limit Online Play` is available for mixed lobbies.
3. **Store-bought loot overflow** is deliberately not rescued yet (store purchases can be upgrades,
   which belong in no loot row). If it should be, the item's definition row — `itemData[itemId]`, whose
   columns are dumped while probing — holds the column that tells loot from upgrade.

## Open questions / decisions

- **Full-row behaviour (measured 2026-10-06)**: in vanilla, when the six loot squares are full a
  further loot item is **silently discarded** — no swap prompt, no UI. There is no vanilla swap flow
  to hook; phase 3 must intercept the placement decision itself.
- **Contention rule — decided (2026-10-06)**: no displacement, ever. Loot fills the six loot squares
  first and then the **free** potion squares; if no square is free (loot row full and all three
  potion squares occupied), the item is **silently discarded**, matching vanilla. Potion offers are
  assumed to follow the mirror rule (potions only go into free potion squares, otherwise discarded)
  — confirm in play.
- **Potions persist** across areas once the wipe is cancelled — a gameplay change, accepted as part
  of the design; `Limit Online Play` stays available for mixed lobbies.
- **Ordering**: fill the six loot squares first, then the three potion squares, in slot order — no
  manual placement.

## Provenance

`RNSReloaded.Interfaces/` is a vendored copy of the interface project from
[NotNite/RNSReloaded](https://github.com/notnite/rnsreloaded/) — copied from the RnSElites project's
own vendored copy (itself unmodified upstream) so this project is self-contained.
