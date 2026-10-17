using Reloaded.Hooks.Definitions;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using RNSReloaded.Interfaces;
using RnSLootSlots.Config;

namespace RnSLootSlots;

public unsafe class Mod : IMod {
    private WeakReference<IRNSReloaded>? rnsReloadedRef;
    private WeakReference<IReloadedHooks>? hooksRef;
    private ILoggerV1 logger = null!;

    private Config.Config config = null!;
    private LootDirector? director;

    public void StartEx(IModLoaderV1 loader, IModConfigV1 modConfig) {
        this.rnsReloadedRef = loader.GetController<IRNSReloaded>();
        this.hooksRef = loader.GetController<IReloadedHooks>()!;
        this.logger = loader.GetLogger();

        var configurator = new Configurator(((IModLoader) loader).GetModConfigDirectory(modConfig.ModId));
        this.config = configurator.GetConfiguration<Config.Config>(0);

        this.Log($"Loaded. enabled={this.config.Enabled}, probe={this.config.ProbeMode}, " +
                 $"keepPotions={this.config.KeepPotionsAcrossAreas}, rescue={this.config.RescueDiscardedLoot}");

        // Nothing may touch RNSReloaded before Ready fires; it crashes the game.
        if (this.rnsReloadedRef.TryGetTarget(out var rnsReloaded)) {
            rnsReloaded.OnReady += this.Ready;
        } else {
            this.Log("WARN: RNSReloaded controller not found. Is the RNSReloaded mod installed and enabled?");
        }
    }

    private void Ready() {
        if (!this.config.Enabled) {
            this.Log("Disabled in config; no hooks installed.");
            return;
        }

        if (this.rnsReloadedRef is null
            || !this.rnsReloadedRef.TryGetTarget(out var rnsReloaded)
            || this.hooksRef is null
            || !this.hooksRef.TryGetTarget(out var hooks)) {
            this.Log("WARN: RNSReloaded or IReloadedHooks unavailable; not installing hooks.");
            return;
        }

        // This mod changes item state locally (potions persisting, and extra loot sharing their slots
        // once the reroute is written), so a lobby mixing it with vanilla clients can disagree about
        // inventories. Upstream gameplay mods block that case with LimitOnlinePlay(); it stays opt-in
        // here so the mod can be played with clients that all have it installed.
        if (this.config.LimitOnlinePlay) {
            rnsReloaded.LimitOnlinePlay();
            this.Log("Online play limited: vanilla clients cannot join this lobby.");
        } else {
            this.Log("Online play NOT limited. Every player in the lobby should have this mod installed.");
        }

        try {
            this.director = new LootDirector(rnsReloaded, hooks, this.logger, this.config);
            this.director.Install();

            // Fired for every script and object-event call; feeds the code trace that names whatever
            // refuses a toybox grant.
            rnsReloaded.OnExecuteIt += this.director.OnExecuteIt;
        } catch (Exception ex) {
            this.Log($"ERROR: failed to install hooks: {ex}");
        }
    }

    private void Log(string message) => this.logger.PrintMessage($"[RnSLootSlots] {message}", this.logger.ColorGreen);

    public void Suspend() { }
    public void Resume() { }
    public void Unload() { }
    public bool CanUnload() => false;
    public bool CanSuspend() => false;
    public Action Disposing => () => { };
}
