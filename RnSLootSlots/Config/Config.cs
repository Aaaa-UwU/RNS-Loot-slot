using System.ComponentModel;

namespace RnSLootSlots.Config;

public class Config : Configurable<Config> {
    [DisplayName("Enabled")]
    [Description("Master switch for the mod.")]
    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    [DisplayName("Share Potion Slots")]
    [Description("Loot items 7-9 go into the three potion squares, sharing them with potions. On by default; this is the mod. When the loot row is full, a picked item that the game would silently discard is placed into a free potion square instead. Only loot-table picks are moved — store purchases are left exactly as vanilla.")]
    [DefaultValue(true)]
    public bool RescueDiscardedLoot { get; set; } = true;

    [DisplayName("Keep Potions Across Areas")]
    [Description("Cancel the game's potion wipe at an area transition, so potions (and any loot sharing their slots) survive into the next area. On by default; this is the mod. Turn it off for vanilla potion behaviour.")]
    [DefaultValue(true)]
    public bool KeepPotionsAcrossAreas { get; set; } = true;

    [DisplayName("Probe Mode")]
    [Description("Diagnostics: log the item-system scripts, the pickup outcome of every pick and the code trace around toybox refusals. Off during normal play; turn it on when something needs measuring.")]
    [DefaultValue(false)]
    public bool ProbeMode { get; set; } = false;

    [DisplayName("Probe Log Limit")]
    [Description("How many times each observed script may write a log line while Probe Mode is on. The item scripts are hot, so the budget keeps the log readable; raise it when a specific script needs a longer trace.")]
    [DefaultValue(150)]
    public int ProbeLogLimit { get; set; } = 150;

    [DisplayName("Limit Online Play")]
    [Description("Off by default, so modded and vanilla clients can join the same lobby. This mod changes item state locally (potions persisting, extra loot in potion slots), so a lobby mixing it with vanilla clients can disagree about inventories. Turn this on to block vanilla clients.")]
    [DefaultValue(false)]
    public bool LimitOnlinePlay { get; set; } = false;
}
