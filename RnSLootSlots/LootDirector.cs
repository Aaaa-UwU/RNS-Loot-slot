using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using RNSReloaded.Interfaces;
using RNSReloaded.Interfaces.Structs;
using RnSLootSlots.Config;

namespace RnSLootSlots;

/// <summary>
/// The mod's runtime half. The design being built here: loot items 7-9 are placed into the player's
/// three potion slots, sharing them with potions, and the game's potion wipe at an area transition is
/// cancelled so both survive into the next area.
///
/// This build is the probing stage. It does two things:
///
/// 1. It observes the item-system scripts, so the slot layout (which squares are loot, which are
///    potions, what <c>type</c>/<c>sqId</c>/<c>rowId</c> they carry) and the placement rules can be
///    read out of a live game rather than guessed at.
/// 2. It can already cancel the potion wipe (<c>scr_itemsys_erase_potions</c>), behind a config toggle,
///    because that half is understood well enough to try: the script's name says what it does and the
///    worst case is vanilla behaviour staying in place.
///
/// The placement reroute (loot into potion squares) is deliberately not written yet: it depends on the
/// layout facts this build measures. See the README for the phase plan.
/// </summary>
public unsafe sealed class LootDirector {
    private readonly IRNSReloaded rns;
    private readonly IReloadedHooks hooks;
    private readonly ILoggerV1 logger;
    private readonly Config.Config config;
    private readonly GameRefs refs;

    /// <summary>The script suspected of wiping potions at an area transition. Cancelling it is phase one.</summary>
    private const string ErasePotionsScript = "scr_itemsys_erase_potions";

    /// <summary>Runs once per area transition, so it timestamps the wipe and frames the probe output.</summary>
    private const string MoveNextScript = "scr_hallwayprogress_move_next";

    private IHook<ScriptDelegate>? erasePotionsHook;
    private IHook<ScriptDelegate>? moveNextHook;
    private IHook<ScriptDelegate>? pickupHook;

    /// <summary>Effect-play hooks, keyed by script name so a detour can reach its own original.</summary>
    private readonly Dictionary<string, IHook<ScriptDelegate>> effectHooks = [];
    private readonly Dictionary<string, int> effectLogCounts = [];

    /// <summary>
    /// The most recent code names seen by <see cref="OnExecuteIt"/>, oldest first. OnExecuteIt fires
    /// for every script and object-event call, so when the toybox's refusal effect is caught this ring
    /// buffer holds the calls leading up to it — the click handler and the capacity check among them,
    /// named rather than guessed.
    /// </summary>
    private readonly Queue<string> codeTrace = [];
    private const int CodeTraceLength = 80;
    private int traceDumps;
    private const int MaxTraceDumps = 25;

    /// <summary>The pickup script. Its return value is the picked item's dataId.</summary>
    private const string PickupScript = "scr_itemsys_pickup";

    /// <summary>
    /// Row bases in the itemsys grid, measured from the probe logs (2026-10-06): the 19 rows are
    /// [0..2] special, [3..6] the four players' hand rows, [7..10] their ability rows, [11..14] their
    /// loot rows and [15..18] their potion rows. A cell's row therefore carries its own itemRowId.
    /// </summary>
    private const int LootRowBase = 11;
    private const int PotionRowBase = 15;

    /// <summary>The loot row's width, measured from the container dumps; a discard only counts as a capacity overflow at this count.</summary>
    private const int LootRowCapacity = 6;

    /// <summary>The pickup source row that means "picked from a loot table" (row 1); the store is row 2.</summary>
    private const int LootTableSourceRow = 1;

    /// <summary>
    /// A potion-row cell whose dataId is below this is empty. Empty cells carry -1; occupied cells
    /// carry the item's dataId.
    /// </summary>
    private const double EmptyCellDataId = -1;

    private int pickupsSeen;
    private int rescuedItems;

    /// <summary>Observation-only hooks, keyed by script name so a detour can reach its own original.</summary>
    private readonly Dictionary<string, IHook<ScriptDelegate>> probeHooks = [];

    /// <summary>Per-script probe budget, so a hot script cannot flood the log.</summary>
    private readonly Dictionary<string, int> probeCounts = [];

    /// <summary>Scripts whose <c>self</c> field list has already been dumped.</summary>
    private readonly HashSet<string> selfKeysDumped = [];

    private bool globalsDumped;
    private int transitionsSeen;
    private int eraseCalls;

    /// <summary>
    /// The item-system scripts worth watching while the layout is being mapped. Placement runs through
    /// can_place/place/pickup; the hotbar scripts mirror items into the visible slots; the setpos
    /// scripts are where the six-square layout lives; the store/populate scripts are where potions are
    /// handed out.
    /// </summary>
    private static readonly string[] ProbeScripts = [
        "scr_itemsys_can_place",
        "scr_itemsys_place_item_in_row",
        // scr_itemsys_pickup is deliberately absent: it has a dedicated hook (PickupDetour) because
        // the rescue experiment needs to act inside the call, not just observe it.
        "scr_itemsys_pickup_loot",
        "scr_itemsys_loot_amount",
        "scr_itemsys_swap_pl_squares",
        "scr_itemsys_erase_loot_table",
        "scr_itemsys_setpos_loot",
        "scr_itemsys_setpos_loot_row",
        "scr_itemsys_setpos_potion",
        "scr_hotbarslot_set_itemsys_slot",
        "scr_itemsys_update_hbslot",
        "scr_hotbarsyspl_update_from_itemsys",
        "scr_itemsys_populate_store_network_potion",
        "scr_itemsys_open_loot_table",
        "scr_itemsys_populate_loot",
        "scr_itemsys_populate_loot_end",
        // The toybox grants items through its own host/client selection path, and its "inventory
        // full" refusal happens before any pickup call — so the check lives in one of these.
        // The scrc_* handlers did not fire when a grant was refused (probed 2026-10-06), so the
        // lobby-buff variants are observed too; the code trace below catches the real path either way.
        "scrc_c_toybox_select",
        "scrc_h_toybox_select",
        "scr_lobbyclient_buff_toyboxselect",
        "scr_lobbyhost_buff_toyboxselect",
        "scr_toybox_page_setup_items",
        "scr_toybox_clear_items",
        "scr_itemsys_open_toybox",
    ];

    /// <summary>
    /// Effect-play scripts. Each takes the effect's key, and the toybox's refusal plays one named
    /// <c>eff_toybox_inventory_full</c> — catching it timestamps the refusal and triggers a dump of the
    /// code-name trace, which is how the check that produced it gets named.
    /// </summary>
    private static readonly string[] EffectPlayScripts = [
        "scr_effect_play",
        "scr_effect_play_battle",
        "scr_effect_play_layer",
        "scr_effect_play_parent",
        "scr_effect_play_thislayer",
    ];

    /// <summary>
    /// Instance fields worth printing on a probed call. The item row, its squares and the hotbar slots
    /// are all instances of the same family; these are the fields that identify which one is which.
    /// </summary>
    private static readonly string[] SlotFields = [
        "slotId", "sqId", "rowId", "itemRowId", "itemSlotId", "sqFlags",
        "type", "dataId", "active", "uniqueId", "playerId", "teamId",
        "level", "stock", "itemSys", "inventorySystem", "invsys_uiState", "invsys_scroll",
        // The itemsys controller carries these; lootCount and the row sizes are what say how much a
        // player is holding and how wide each row is.
        "lootCount", "handRowSize", "rowSize", "highlightRow", "highlightSlot",
    ];

    /// <summary>
    /// Container fields worth walking when a state dump is taken: the itemsys controller's rows and the
    /// per-player hotbar object's slots. Whichever of these an instance has is dumped.
    /// </summary>
    private static readonly string[] ContainerFields = [
        "slots", "hotbar", "lootCount", "handRowSize", "rowSize", "shown", "active",
    ];

    /// <summary>Identifying fields printed for a slot-like object encountered inside a container walk.</summary>
    private static readonly string[] SlotObjectFields = [
        "slotId", "rowId", "sqId", "itemRowId", "itemSlotId", "type", "dataId", "stock", "active", "uniqueId",
    ];

    /// <summary>
    /// Globals that might hold the item storage. Unknown by name so far, which is exactly what the
    /// probe is for: whichever of these exists and is an array of items is the one to read.
    /// </summary>
    private static readonly string[] GlobalCandidates = [
        "itemData", "playerNum", "playerHp", "player",
        "hotbarSlots", "hbslots", "itemSys", "playerItem", "inventorySystem",
    ];

    /// <summary>Whether the verbose diagnostics are on. Functional behaviour never depends on it.</summary>
    private bool Probing => this.config.ProbeMode;

    public LootDirector(IRNSReloaded rns, IReloadedHooks hooks, ILoggerV1 logger, Config.Config config) {
        this.rns = rns;
        this.hooks = hooks;
        this.logger = logger;
        this.config = config;
        this.refs = new GameRefs(rns);
    }

    /// <summary>Installs every hook. Safe to call once, from the loader's Ready callback.</summary>
    public void Install() {
        this.erasePotionsHook = this.Hook(ErasePotionsScript, this.ErasePotionsDetour);
        this.moveNextHook = this.Hook(MoveNextScript, this.MoveNextDetour);
        this.pickupHook = this.Hook(PickupScript, this.PickupDetour);

        this.InstallEffectHooks();

        if (this.config.ProbeMode) {
            this.InstallProbeHooks();
        }

        this.Log($"Installed. erasePotions={this.erasePotionsHook != null}, " +
                 $"moveNext={this.moveNextHook != null}, pickup={this.pickupHook != null}, " +
                 $"effectHooks={this.effectHooks.Count}, " +
                 $"probeHooks={this.probeHooks.Count}, " +
                 $"keepPotions={this.config.KeepPotionsAcrossAreas}, rescue={this.config.RescueDiscardedLoot}");
    }

    /// <summary>
    /// Installs the effect-play hooks. Each detour reaches its original through the dictionary, so the
    /// hook is stored there before it is enabled.
    /// </summary>
    private void InstallEffectHooks() {
        if (!this.Probing) return;

        foreach (var scriptName in EffectPlayScripts) {
            try {
                var id = this.rns.ScriptFindId(scriptName);
                if (id <= 0) {
                    this.Log($"probe: effect script '{scriptName}' not found; not observed.");
                    continue;
                }

                var data = this.rns.GetScriptData(id - 100000);
                if (data == null || data->Functions == null || data->Functions->Function == 0) continue;

                var name = scriptName;
                var hook = this.hooks.CreateHook<ScriptDelegate>(
                    (self, other, returnValue, argc, argv) => this.EffectPlayDetour(name, self, other, returnValue, argc, argv),
                    data->Functions->Function);

                this.effectHooks[name] = hook;
                hook.Activate();
                hook.Enable();
            } catch (Exception ex) {
                this.Log($"WARN: failed to hook effect script '{scriptName}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Fired by RNSReloaded for every script and object-event call. Keeps the ring buffer filled while
    /// probing, so a refusal is preceded by its own call sequence in the log.
    /// </summary>
    public void OnExecuteIt(ExecuteItArguments args) {
        try {
            if (!this.config.ProbeMode) return;

            var name = this.CodeName(args);
            this.codeTrace.Enqueue(name);
            while (this.codeTrace.Count > CodeTraceLength) this.codeTrace.Dequeue();

            // Any toybox-flavoured code is named once, so the refusal handler cannot stay anonymous
            // even if it is an object event rather than a script.
            if (this.toyboxCodeCount < MaxToyboxCodeLogs
                && name.Contains("toybox", StringComparison.OrdinalIgnoreCase)
                && this.toyboxCodeSeen.Add(name)) {
                this.toyboxCodeCount++;
                this.Log($"toybox code seen: {name}");
            }

            // The toybox's grant/refusal loop lives in obj_itemToyboxDeco's user events (measured
            // 2026-10-06: its Other_10/Other_11 bodies are what play the toybox effects). Dumping that
            // object's fields once is how the pending/selected item id — the piece needed to grant an
            // overflow item ourselves — gets found.
            if (name.Contains("itemToyboxDeco", StringComparison.OrdinalIgnoreCase) && this.decoDumps < MaxDecoDumps) {
                this.decoDumps++;
                this.DumpDecoObject(name, args.Self);
            }

            // Trace triggers. The toybox grant is refused before any pickup and produces no new code
            // name (measured 2026-10-06), so the trigger has to be something that runs at the moment
            // of the click instead:
            //  - the decoration object's user events (Other_*), which is where a networked toybox
            //    selection would be applied,
            //  - the inventory-square click feedback scripts,
            //  - and the refusal effect's own name, should it ever appear as an event.
            // The every-frame Step/Draw events are deliberately not triggers: they would flood.
            if (this.traceDumps < MaxTraceDumps && IsTraceTrigger(name)) {
                this.traceDumps++;
                this.Log($"--- code trace: last {this.codeTrace.Count} calls before {name} ---");
                foreach (var traced in this.codeTrace) this.Log($"    {traced}");
                this.Log("--- end code trace ---");
            }
        } catch {
            // this runs inside the game's script dispatch; it must never throw
        }
    }

    /// <summary>
    /// Code names whose appearance should dump the trace. Only the refusal effect's own name qualifies
    /// now: the per-frame toybox user events burned the whole dump budget in the previous run without
    /// showing anything actionable. The precise trigger is the effect-play hook, which reports the
    /// event that called it.
    /// </summary>
    private static bool IsTraceTrigger(string name) =>
        name.Contains("inventory_full", StringComparison.OrdinalIgnoreCase);

    private readonly HashSet<string> toyboxCodeSeen = [];
    private int toyboxCodeCount;
    private const int MaxToyboxCodeLogs = 40;

    private int decoDumps;
    private const int MaxDecoDumps = 6;
    private bool decoKeysDumped;

    /// <summary>
    /// The player hotbar object (the mirror of the itemsys rows), captured from a
    /// <c>scr_hotbarsyspl_update_from_itemsys</c> call. Its <c>hotbar</c> field holds the visible slot
    /// objects, so dumping it after a rescue shows whether the game synced the written cell to the bar.
    /// </summary>
    private CInstance* hotbarInstance;

    /// <summary>
    /// Dumps the toybox decoration object's state: its full field list once, then the values of the
    /// fields that could name what the player is interacting with — the pending item id is what a
    /// toybox-side grant needs, and this is where it would live.
    /// </summary>
    private void DumpDecoObject(string code, CInstance* self) {
        try {
            if (self == null) return;

            if (!this.decoKeysDumped) {
                this.decoKeysDumped = true;
                this.Log($"{code}: deco fields=[{string.Join(", ", this.refs.StructKeys(self))}]");
            }

            var parts = new List<string>();
            foreach (var field in this.refs.StructKeys(self)) {
                if (field.Length < 2) continue;

                var interesting = field.Contains("item", StringComparison.OrdinalIgnoreCase)
                                  || field.Contains("data", StringComparison.OrdinalIgnoreCase)
                                  || field.Contains("hover", StringComparison.OrdinalIgnoreCase)
                                  || field.Contains("select", StringComparison.OrdinalIgnoreCase)
                                  || field.Contains("mouse", StringComparison.OrdinalIgnoreCase)
                                  || field.Contains("click", StringComparison.OrdinalIgnoreCase)
                                  || field.Contains("pend", StringComparison.OrdinalIgnoreCase);
                if (!interesting) continue;

                parts.Add($"{field}={this.DescribeCell(this.refs.Field(self, field))}");
            }

            this.Log($"{code}: deco values: {(parts.Count == 0 ? "<none>" : string.Join(", ", parts))}");
        } catch {
            // diagnostics must never disturb gameplay
        }
    }

    private string CodeName(ExecuteItArguments args) =>
        args.Code != null && args.Code->Name != null
            ? Marshal.PtrToStringAnsi((nint) args.Code->Name) ?? "<unnamed>"
            : "<unknown>";

    /// <summary>
    /// Logs the arguments of each effect-play call, sparsely. The key arrives as a non-string value
    /// (measured 2026-10-06), so the arguments are described by type rather than read as a string; the
    /// refusal is identified by its effect object's event through the code trace instead.
    /// </summary>
    private RValue* EffectPlayDetour(string script, CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        try {
            if (!this.Probing) {
                // Fall through to the original below; the hooks are only installed while probing anyway.
                if (this.effectHooks.TryGetValue(script, out var passthrough)) {
                    return passthrough.OriginalFunction(self, other, returnValue, argc, argv);
                }

                return returnValue;
            }

            var count = this.effectLogCounts.GetValueOrDefault(script) + 1;
            this.effectLogCounts[script] = count;
            if (count <= 30) {
                this.Log($"{script} #{count}: argc={argc}{this.DescribeArgs(argc, argv)}");

                // OnExecuteIt reports a code name *before* its body runs, so the last entry in the
                // ring buffer while this call is being made is the event whose body called it. That
                // names the toybox's inline refusal handler — the one place script hooks cannot see.
                if (this.traceDumps < MaxTraceDumps) {
                    this.traceDumps++;
                    this.Log($"--- code trace: last {this.codeTrace.Count} calls, effect '{script}' #{count} is running now ---");
                    foreach (var traced in this.codeTrace) this.Log($"    {traced}");
                    this.Log("--- end code trace ---");
                }
            }
        } catch (Exception ex) {
            this.Log($"WARN: effect-play detour for '{script}' failed: {ex.Message}");
        }

        if (this.effectHooks.TryGetValue(script, out var hook)) {
            return hook.OriginalFunction(self, other, returnValue, argc, argv);
        }

        this.Log($"WARN: no original registered for effect script '{script}'; the call is dropped.");
        return returnValue;
    }

    /// <summary>
    /// Creates, activates and enables a hook on a named game script. Returns null (with a log line) when
    /// the script cannot be resolved, so a game update degrades one feature instead of crashing.
    /// </summary>
    private IHook<ScriptDelegate>? Hook(string scriptName, ScriptDelegate detour) {
        try {
            var id = this.rns.ScriptFindId(scriptName);
            if (id <= 0) {
                this.Log($"WARN: script '{scriptName}' not found; that hook is disabled.");
                return null;
            }

            var data = this.rns.GetScriptData(id - 100000);
            if (data == null || data->Functions == null || data->Functions->Function == 0) {
                this.Log($"WARN: script data for '{scriptName}' is unusable; that hook is disabled.");
                return null;
            }

            // Activate immediately: Reloaded requires each hook to be committed before the next one is
            // created on the same address, and hook creation must not happen concurrently.
            var hook = this.hooks.CreateHook<ScriptDelegate>(detour, data->Functions->Function);
            hook.Activate();
            hook.Enable();
            return hook;
        } catch (Exception ex) {
            this.Log($"WARN: failed to hook '{scriptName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Installs the observation hooks. Each detour must reach its own original through the dictionary,
    /// so the hook is stored there <em>before</em> it is enabled: a call arriving in that window would
    /// otherwise find no original and be unable to pass through.
    /// </summary>
    private void InstallProbeHooks() {
        foreach (var scriptName in ProbeScripts) {
            try {
                var id = this.rns.ScriptFindId(scriptName);
                if (id <= 0) {
                    this.Log($"probe: script '{scriptName}' not found; not observed.");
                    continue;
                }

                var data = this.rns.GetScriptData(id - 100000);
                if (data == null || data->Functions == null || data->Functions->Function == 0) {
                    this.Log($"probe: script data for '{scriptName}' is unusable; not observed.");
                    continue;
                }

                var name = scriptName;
                var hook = this.hooks.CreateHook<ScriptDelegate>(
                    (self, other, returnValue, argc, argv) => this.ProbeDetour(name, self, other, returnValue, argc, argv),
                    data->Functions->Function);

                this.probeHooks[name] = hook;
                hook.Activate();
                hook.Enable();
            } catch (Exception ex) {
                this.Log($"WARN: failed to hook probe script '{scriptName}': {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Hooks
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The potion wipe. With <c>Keep Potions Across Areas</c> on, the original is not called: that IS the
    /// cancellation, and everything potions share their slots with survives with them.
    ///
    /// A skipped call returns whatever the caller passed as its return slot untouched. The script is a
    /// cleanup routine whose result nobody reads, so leaving the slot empty is safe; if a game update
    /// turns it into a value-returning check, this is the line to revisit.
    /// </summary>
    private RValue* ErasePotionsDetour(CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        var call = 0;
        var withinBudget = false;

        try {
            call = ++this.eraseCalls;
            withinBudget = this.Probing && call <= this.config.ProbeLogLimit;

            if (withinBudget) {
                this.Log($"erase_potions #{call}: argc={argc}{this.DescribeArgs(argc, argv)}{this.DescribeFields(self)}");

                // The wipe's before/after pair is the whole point: it shows exactly what it clears.
                this.DumpContainer($"  before erase_potions #{call}", self);
            }

            if (this.Probing && call == 1) {
                this.DumpGlobalsOnce();
                this.DumpSelfKeysOnce(ErasePotionsScript, self);
            }

            if (this.config.KeepPotionsAcrossAreas) {
                if (withinBudget) {
                    this.Log("erase_potions suppressed (Keep Potions Across Areas is on).");
                    this.DumpContainer($"  after suppressed erase_potions #{call}", self);
                }

                // Suppressing the wipe is the point of the toggle: do not call the original.
                return returnValue;
            }
        } catch (Exception ex) {
            this.Log($"WARN: erase_potions detour failed: {ex.Message}");
        }

        if (this.erasePotionsHook == null) return returnValue;
        var result = this.erasePotionsHook.OriginalFunction(self, other, returnValue, argc, argv);

        try {
            if (withinBudget) {
                this.DumpContainer($"  after erase_potions #{call}", self);
            }
        } catch (Exception ex) {
            this.Log($"WARN: erase_potions after-dump failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>Area transition marker: every transition is logged so the wipe's timing is visible in the log.</summary>
    private RValue* MoveNextDetour(CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        try {
            if (this.Probing) {
                var transition = ++this.transitionsSeen;
                if (transition <= this.config.ProbeLogLimit) {
                    this.Log($"area transition #{transition} (move_next).");
                }

                this.DumpGlobalsOnce();
            }
        } catch (Exception ex) {
            this.Log($"WARN: move_next detour failed: {ex.Message}");
        }

        if (this.moveNextHook == null) return returnValue;
        return this.moveNextHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    /// <summary>
    /// The pickup call. Its return value is the picked item's dataId, and the call is where a full loot
    /// row turns into a silent discard: measured 2026-10-06, the game stages the item in row 0 and the
    /// pickup moves it into the player's loot row, clearing the staging cell whether or not the move
    /// succeeded — a discarded item simply leaves nothing behind.
    ///
    /// The detour therefore brackets the call with occupancy counts of the player's loot and potion
    /// rows. When neither gained a cell nothing landed, and with Rescue Discarded Loot on the item is
    /// placed into a free potion-row cell instead. The placement runs before the caller's own
    /// hotbar-refresh calls (which happen after this script returns), so the game syncs the new cell to
    /// the hotbar itself.
    /// </summary>
    private RValue* PickupDetour(CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        var player = 0;
        var lootBefore = -1;
        var potionBefore = -1;
        var count = 0;
        var withinBudget = false;

        try {
            player = argc > 0 ? (int) GameRefs.ToDouble(argv[0]) : 0;
            lootBefore = this.CountOccupied(self, LootRowBase + player);
            potionBefore = this.CountOccupied(self, PotionRowBase + player);

            count = ++this.pickupsSeen;
            withinBudget = this.Probing && count <= this.config.ProbeLogLimit;
            if (withinBudget) {
                this.Log($"{PickupScript} #{count}: argc={argc}{this.DescribeArgs(argc, argv)}" +
                         $" loot={lootBefore} potion={potionBefore}");
            }
        } catch (Exception ex) {
            this.Log($"WARN: pickup detour (before) failed: {ex.Message}");
        }

        if (this.pickupHook == null) return returnValue;
        var result = this.pickupHook.OriginalFunction(self, other, returnValue, argc, argv);

        try {
            var itemId = (int) GameRefs.ToDouble(result);
            var lootAfter = this.CountOccupied(self, LootRowBase + player);
            var potionAfter = this.CountOccupied(self, PotionRowBase + player);
            var readable = lootBefore >= 0 && potionBefore >= 0 && lootAfter >= 0 && potionAfter >= 0;
            var landed = readable && (lootAfter > lootBefore || potionAfter > potionBefore);

            if (withinBudget) {
                this.Log($"{PickupScript} #{count} -> item={itemId} " +
                         $"loot {lootBefore}->{lootAfter} potion {potionBefore}->{potionAfter} " +
                         $"({(landed ? "stored" : readable ? "discarded" : "unreadable")})");
            }

            // The item's definition row is dumped for every pick while probing (bounded), which is how
            // the column that tells loot from potion from store upgrade gets identified.
            if (this.Probing) {
                this.DumpItemDataRow(itemId);
            }

            if (readable && !landed && itemId > 0 && this.config.RescueDiscardedLoot) {
                var sourceRow = argc > 1 ? (int) GameRefs.ToDouble(argv[1]) : -1;
                this.TryRescue(self, player, itemId, lootAfter, sourceRow);
            }
        } catch (Exception ex) {
            this.Log($"WARN: pickup detour (after) failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Places a discarded item into a free cell of the player's potion row.
    ///
    /// Two things were measured in the first normal-run session (2026-10-06) that shape this:
    ///
    /// 1. **Only a full loot row can explain a discard.** Picks that failed with room left in the row
    ///    were store purchases refused for another reason (no gold), and granting those would be giving
    ///    items away — hence the <paramref name="lootCount"/> guard.
    /// 2. **`scr_itemsys_place_item_in_row` refuses the move.** Calling it as
    ///    <c>(item, row)</c> places offers into the loot-table/store rows, but (item, 15) — a loot item
    ///    into the potion row — is a no-op; the rows enforce an item-type rule. The rescue therefore
    ///    writes the free cell's <c>dataId</c> directly and lets the game's own layout and hotbar
    ///    refresh calls pick the cell up.
    /// </summary>
    private void TryRescue(CInstance* self, int player, int itemId, int lootCount, int sourceRow) {
        try {
            // Only the loot-table flow is rescued. Store purchases (row 2) can be upgrades, which do
            // not belong in any loot row at all — one was wrongly moved into a potion square in the
            // 2026-10-06 session. Loot-table picks (row 1) are always loot.
            if (sourceRow != LootTableSourceRow) {
                if (this.Probing) {
                    this.Log($"rescue: item {itemId} came from source row {sourceRow}, not the loot table " +
                             $"(row {LootTableSourceRow}); left alone.");
                }

                return;
            }

            // A discard with room in the loot row is not a capacity problem; leave it alone.
            if (lootCount < LootRowCapacity) {
                if (this.Probing) {
                    this.Log($"rescue: item {itemId} was discarded with only {lootCount}/{LootRowCapacity} loot held " +
                             "— not a capacity issue; left alone.");
                }

                return;
            }

            var potionRow = PotionRowBase + player;
            var freeCell = this.FindFreeCell(self, potionRow);

            if (freeCell < 0) {
                // The player's chosen rule: no free square anywhere means the item is discarded.
                this.Log($"item {itemId} discarded: the loot row is full and every potion square is taken.");
                return;
            }

            if (this.Probing) {
                this.DumpContainer($"  before rescue of item {itemId} into row {potionRow} cell {freeCell}", self);
            }

            if (!this.WriteItemIntoCell(self, potionRow, freeCell, itemId)) {
                this.Log($"WARN: could not write item {itemId} into potion row {potionRow} cell {freeCell}; left discarded.");
                return;
            }

            if (this.Probing) {
                var readBack = this.CellDataId(this.Row(self, potionRow)!, freeCell);
                this.Log($"rescue: item {itemId} written into potion row {potionRow} cell {freeCell} (reads back as {readBack:0}).");
            }

            // Lay the potion row out again so the new cell receives its position; the caller refreshes
            // the hotbar itself once this script returns.
            this.refs.CallScript("scr_itemsys_setpos_potion", self, null, player);

            this.rescuedItems++;
            this.Log($"loot item {itemId} placed into potion square {freeCell + 1} " +
                     $"(full loot row; rescued so far: {this.rescuedItems}).");

            if (this.Probing) {
                this.DumpContainer($"  after rescue of item {itemId}", self);

                if (this.hotbarInstance != null) {
                    this.DumpContainer($"  hotbar mirror after rescue of item {itemId}", this.hotbarInstance);
                }
            }
        } catch (Exception ex) {
            this.Log($"WARN: rescue failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes an item's dataId into an empty cell, preserving the element's own numeric representation —
    /// an RValue stores Real and Int32 at the same offset but at different widths, so writing the wrong
    /// one yields garbage. Refuses to overwrite a cell that is not empty.
    /// </summary>
    private bool WriteItemIntoCell(CInstance* self, int rowIndex, int cellIndex, int itemId) {
        try {
            var row = this.Row(self, rowIndex);
            if (row == null) return false;

            var cell = this.rns.ArrayGetEntry(row, cellIndex);
            if (cell == null || cell->Type != RValueType.Array) return false;

            var dataId = this.rns.ArrayGetEntry(cell, 1);
            if (dataId == null) return false;
            if (GameRefs.ToDouble(dataId) > EmptyCellDataId) return false;

            switch (dataId->Type) {
                case RValueType.Real:
                    dataId->Real = itemId;
                    return true;
                case RValueType.Int32:
                    dataId->Int32 = itemId;
                    return true;
                case RValueType.Int64:
                    dataId->Int64 = itemId;
                    return true;
                default:
                    return false;
            }
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Dumps an item's definition row (<c>itemData[itemId][0]</c>) column by column, bounded. It is how
    /// the column that tells loot from potion from store upgrade gets identified — the game's type
    /// vocabulary (<c>weapontype_loot</c>/<c>weapontype_potion</c>) has no upgrade entry, so the
    /// distinguishing value is somewhere in this row.
    /// </summary>
    private void DumpItemDataRow(int itemId) {
        try {
            if (itemId <= 0 || this.itemDataDumps >= MaxItemDataDumps) return;

            var itemData = this.refs.Global("itemData");
            if (itemData == null) return;

            var row = this.rns.ArrayGetEntry(itemData, itemId);
            if (row == null) {
                this.Log($"itemData[{itemId}]: <absent>");
                return;
            }

            var level0 = this.rns.ArrayGetEntry(row, 0);
            if (level0 == null || level0->Type != RValueType.Array) {
                this.Log($"itemData[{itemId}]: level 0 is not an array.");
                return;
            }

            this.itemDataDumps++;
            var columns = this.refs.ArrayLength(level0);
            var cells = new List<string>();
            for (var i = 0; i < Math.Min(columns, 16); i++) {
                cells.Add($"[{i}]={this.DescribeCell(this.rns.ArrayGetEntry(level0, i))}");
            }

            this.Log($"itemData[{itemId}] cols={columns}: {string.Join(" ", cells)}");
        } catch {
            // diagnostics must never disturb gameplay
        }
    }

    private int itemDataDumps;
    private const int MaxItemDataDumps = 25;

    /// <summary>Number of occupied cells in an itemsys row, or -1 when the row cannot be read.</summary>
    private int CountOccupied(CInstance* self, int rowIndex) {
        var row = this.Row(self, rowIndex);
        if (row == null) return -1;

        var length = this.refs.ArrayLength(row);
        var occupied = 0;
        for (var i = 0; i < length; i++) {
            if (this.CellDataId(row, i) > EmptyCellDataId) occupied++;
        }

        return occupied;
    }

    /// <summary>Index of the first empty cell in an itemsys row, or -1 when the row is full or unreadable.</summary>
    private int FindFreeCell(CInstance* self, int rowIndex) {
        var row = this.Row(self, rowIndex);
        if (row == null) return -1;

        var length = this.refs.ArrayLength(row);
        for (var i = 0; i < length; i++) {
            if (this.CellDataId(row, i) <= EmptyCellDataId) return i;
        }

        return -1;
    }

    /// <summary>
    /// The cell array of a row in the itemsys controller's <c>slots</c>, or null when either the row or
    /// the container cannot be read. A row is <c>[[type, dataId, x, y, …], …]</c>, measured from the
    /// container dumps.
    /// </summary>
    private RValue* Row(CInstance* self, int rowIndex) {
        var slots = this.refs.Field(self, "slots");
        if (slots == null || slots->Type != RValueType.Array) return null;

        var row = this.rns.ArrayGetEntry(slots, rowIndex);
        return row != null && row->Type == RValueType.Array ? row : null;
    }

    /// <summary>
    /// A cell's item dataId, or a value at or below <see cref="EmptyCellDataId"/> when the cell is empty
    /// or unreadable.
    /// </summary>
    private double CellDataId(RValue* row, int cellIndex) {
        var cell = this.rns.ArrayGetEntry(row, cellIndex);
        if (cell == null || cell->Type != RValueType.Array) return double.NegativeInfinity;

        var dataId = this.rns.ArrayGetEntry(cell, 1);
        return dataId == null ? double.NegativeInfinity : GameRefs.ToDouble(dataId);
    }

    /// <summary>
    /// Observation only: logs the first few calls of a probed script with its arguments and the
    /// identifiable fields of its <c>self</c>, then passes the call through untouched. The first call
    /// also dumps the candidate globals and the full field list of <c>self</c>, because those are what
    /// name the storage.
    /// </summary>
    private RValue* ProbeDetour(string script, CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        var count = 0;
        var withinBudget = false;

        try {
            count = this.probeCounts.GetValueOrDefault(script) + 1;
            this.probeCounts[script] = count;
            withinBudget = count <= this.config.ProbeLogLimit;

            if (withinBudget) {
                this.Log($"{script} #{count}: argc={argc}{this.DescribeArgs(argc, argv)}{this.DescribeFields(self)}");
            }

            if (count == 1) {
                this.DumpGlobalsOnce();
                this.DumpSelfKeysOnce(script, self);
            }

            // The hotbar mirror object is the one thing a rescue cannot verify by reading the itemsys
            // rows back: it holds the visible slots, and it is only reachable through this call.
            if (script == "scr_hotbarsyspl_update_from_itemsys") {
                this.hotbarInstance = self;
            }
        } catch (Exception ex) {
            this.Log($"WARN: probe detour for '{script}' failed: {ex.Message}");
        }

        if (!this.probeHooks.TryGetValue(script, out var hook)) {
            // The hook was enabled without its original being registered; the call cannot be passed through.
            this.Log($"WARN: no original registered for probed script '{script}'; the call is dropped.");
            return returnValue;
        }

        var result = hook.OriginalFunction(self, other, returnValue, argc, argv);

        try {
            if (withinBudget) {
                if (ReturnLoggedScripts.Contains(script)) {
                    this.Log($"{script} #{count} -> {this.DescribeCell(result)}");
                }

                // The baseline dump captures the resting layout the first few times each of these runs.
                if (DumpsBaseline(script, count)) {
                    this.DumpContainer($"  after {script} #{count}", self);
                }
            }
        } catch (Exception ex) {
            this.Log($"WARN: probe return logging for '{script}' failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>Scripts whose first few calls get a one-sided dump, to capture the resting layout.</summary>
    private static bool DumpsBaseline(string script, int count) =>
        count <= 3 && script is "scr_itemsys_setpos_loot" or "scr_itemsys_setpos_potion" or "scr_hotbarsyspl_update_from_itemsys";

    /// <summary>
    /// Scripts whose return value is worth a line. loot_amount and can_place are getters — their result
    /// is the answer; the pickup/place pair's results say whether the game accepted the placement.
    /// </summary>
    private static readonly HashSet<string> ReturnLoggedScripts = [
        "scr_itemsys_loot_amount",
        "scr_itemsys_can_place",
        "scr_itemsys_place_item_in_row",
        "scr_itemsys_pickup",
        "scr_itemsys_pickup_loot",
    ];

    // ---------------------------------------------------------------------------------------------
    // Probe helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>Dumps the candidate item-storage globals once per session, at the first in-game call.</summary>
    private void DumpGlobalsOnce() {
        if (this.globalsDumped) return;
        this.globalsDumped = true;

        try {
            var parts = new List<string>(GlobalCandidates.Length);
            foreach (var name in GlobalCandidates) {
                var value = this.refs.Global(name);
                parts.Add($"{name}={this.DescribeCell(value)}");
            }

            this.Log($"globals: {string.Join(", ", parts)}");
        } catch {
            // diagnostics must never disturb gameplay
        }
    }

    /// <summary>Dumps an instance's full field list once per script, so the slot family can be identified.</summary>
    private void DumpSelfKeysOnce(string script, CInstance* self) {
        try {
            if (!this.selfKeysDumped.Add(script)) return;
            if (self == null) {
                this.Log($"{script}: self was null.");
                return;
            }

            var keys = this.refs.StructKeys(self);
            this.Log($"{script}: self fields=[{string.Join(", ", keys)}]");
        } catch {
            // diagnostics must never disturb gameplay
        }
    }

    /// <summary>
    /// Dumps whatever item containers an instance carries — the itemsys controller's rows and the
    /// per-player hotbar's slots — so a before/after pair around a call shows exactly what moved.
    /// Depth- and entry-limited on purpose: a container is wide, and the log is not a database.
    /// </summary>
    private void DumpContainer(string label, CInstance* self) {
        if (self == null) {
            this.Log($"{label}: self was null.");
            return;
        }

        try {
            var parts = new List<string>();
            foreach (var field in ContainerFields) {
                var value = this.refs.Field(self, field);
                if (value == null) continue;
                parts.Add($"{field}={this.DescribeValue(value, 3)}");
            }

            this.Log(parts.Count == 0
                         ? $"{label}: no container fields on self."
                         : $"{label}: {string.Join("  ", parts)}");
        } catch {
            // diagnostics must never disturb gameplay
        }
    }

    /// <summary>
    /// Formats a value, walking arrays and slot objects a few levels deep. Objects print only their
    /// identifying fields, so a row of slot objects stays one readable line instead of exploding into
    /// every field each one carries.
    /// </summary>
    private string DescribeValue(RValue* value, int depth) {
        if (value == null) return "<absent>";

        try {
            switch (value->Type) {
                case RValueType.Array: {
                    var length = this.refs.ArrayLength(value);
                    if (depth <= 0) return $"<array len={length}>";

                    var entries = new List<string>();
                    var shown = Math.Min(length, 16);
                    for (var i = 0; i < shown; i++) {
                        entries.Add(this.DescribeValue(this.rns.ArrayGetEntry(value, i), depth - 1));
                    }

                    if (length > shown) entries.Add($"…(+{length - shown})");
                    return $"[{string.Join(", ", entries)}]";
                }
                case RValueType.Object: {
                    if (depth <= 0) return "<object>";

                    var instance = value->Object;
                    if (instance == null) return "<null-object>";

                    var fields = new List<string>();
                    foreach (var field in SlotObjectFields) {
                        var fieldValue = this.refs.Field(instance, field);
                        if (fieldValue != null) fields.Add($"{field}={this.DescribeCell(fieldValue)}");
                    }

                    return fields.Count == 0 ? "<object>" : $"{{{string.Join(", ", fields)}}}";
                }
                default:
                    return this.DescribeCell(value);
            }
        } catch {
            return "<unreadable>";
        }
    }

    /// <summary>Formats the arguments of a call, up to a sane number of them.</summary>
    private string DescribeArgs(int argc, RValue** argv) {
        if (argc <= 0) return "";

        try {
            var parts = new List<string>(Math.Min(argc, 8));
            for (var i = 0; i < Math.Min(argc, 8); i++) {
                parts.Add(this.DescribeCell(argv[i]));
            }

            return $" args=[{string.Join(", ", parts)}]";
        } catch {
            return " args=<unreadable>";
        }
    }

    /// <summary>Formats the identifying fields present on an instance, skipping the ones it does not have.</summary>
    private string DescribeFields(CInstance* self) {
        if (self == null) return " self=<null>";

        try {
            var parts = new List<string>();
            foreach (var field in SlotFields) {
                var value = this.refs.Field(self, field);
                if (value != null) parts.Add($"{field}={this.DescribeCell(value)}");
            }

            return parts.Count == 0 ? " self{}" : $" self{{{string.Join(", ", parts)}}}";
        } catch {
            return " self=<unreadable>";
        }
    }

    /// <summary>Formats one value by its runtime type.</summary>
    private string DescribeCell(RValue* value) {
        if (value == null) return "<absent>";

        try {
            return value->Type switch {
                RValueType.String => $"\"{this.refs.SafeString(value)}\"",
                RValueType.Real => value->Real.ToString("0.##"),
                RValueType.Int32 => value->Int32.ToString(),
                RValueType.Int64 => value->Int64.ToString(),
                RValueType.Bool => value->Bool ? "true" : "false",
                RValueType.Array => $"<array len={this.refs.ArrayLength(value)}>",
                RValueType.Object => "<object>",
                _ => $"<{value->Type}>",
            };
        } catch {
            return $"<{value->Type}>";
        }
    }

    private void Log(string message) => this.logger.PrintMessage($"[RnSLootSlots] {message}", this.logger.ColorGreen);
}
