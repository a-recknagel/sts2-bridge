// Vendored from sts2-cli (https://github.com/wuhao21/sts2-cli @084d1aa, MIT, (c) 2025 Hao Wu; see
// ../GodotStubs/LICENSE-sts2-cli): RunSimulator.EnsureModelDbInitialized and the Harmony patches it installs, trimmed
// to what this bridge exercises. Dropped on the way in:
//   - sts2-cli's localization files. It looks for them relative to its own bin directory, which never resolved from
//     a driver built elsewhere, so every parity result so far ran on the empty-table fallback. That fallback is kept.
//   - The Task.Yield patch. It only acts while sts2-cli's own end-turn loop sets a flag; nothing here does.
//   - The bundle-screen prefix. CombatWorker/BundleSelector owns FromChooseABundleScreen.
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

static class HeadlessInit
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static void Run()
    {
        TestMode.IsOn = true;

        // The engine needs mod discovery and assembly metadata even unmodded; TestMode skips the filesystem scan.
        MegaCrit.Sts2.Core.Modding.ModManager.Initialize(new MegaCrit.Sts2.Core.Modding.ModManagerFileIo(), null, null)
            .GetAwaiter().GetResult();
        MegaCrit.Sts2.Core.Modding.AssemblyInfo.Init();

        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());

        try { _ = MegaCrit.Sts2.Core.Platform.PlatformUtil.PrimaryPlatform; }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] PlatformUtil init: {ex.Message}"); }
        try { SaveManager.Instance.InitProfileId(0); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] SaveManager.InitProfileId: {ex.Message}"); }
        // PrefsSave.FastMode is read from gameplay paths (e.g. Slice.OnPlay's animation delay); null NREs them.
        try { SaveManager.Instance.InitPrefsDataForTest(); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] SaveManager.InitPrefsDataForTest: {ex.Message}"); }

        var harmony = new Harmony("sts2-bridge.headless-init");
        // Cmd.Wait is UI pacing (e.g. PreviewCardPileAdd); with no scene tree it never completes and the executor hangs.
        foreach (var wait in typeof(Cmd).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Wait"))
            harmony.Patch(wait, prefix: Prefix(nameof(CmdWait)));
        // TalkCmd.Play is a speech-bubble VFX that NREs mid enemy move (sts2-cli issue #64); callers ignore its result.
        harmony.Patch(typeof(TalkCmd).GetMethod("Play", BindingFlags.Public | BindingFlags.Static)!, prefix: Prefix(nameof(TalkCmdPlay)));

        InitLocManager(harmony);

        var subtypes = AbstractModelSubtypes.All;
        int failed = 0;
        foreach (var t in subtypes)
        {
            try { ModelDb.Inject(t); }
            catch (Exception ex) { if (++failed <= 5) Console.Error.WriteLine($"[WARN] ModelDb.Inject {t.Name}: {ex.GetType().Name}: {ex.Message}"); }
        }
        Console.Error.WriteLine($"[INFO] ModelDb: {subtypes.Count - failed} registered, {failed} failed out of {subtypes.Count}");

        // Progress resolves character models, so it comes after ModelDb.
        SaveManager.Instance.InitProgressData();

        // The built-in test reward selector assumes every reward is taken. Leave the screen the way a player does, so
        // a skipped card still completes the set and an event continuation waiting on it resumes.
        RewardsSet.testSelector = async set =>
        {
            var synchronizer = RunManager.Instance.RewardsSetSynchronizer;
            foreach (var reward in set.Rewards)
                await synchronizer.SelectLocalReward(reward);
            if (!synchronizer.IsRewardsSetCompleted(set))
                synchronizer.SkipLocalRewardsSet();
        };

        ModelIdSerializationCache.Init();
    }

    // LocManager.Initialize needs PlatformUtil. Install an uninitialised instance over empty tables instead, and make
    // lookups return their key: text is display only, and the observation carries ids and DynamicVars, not strings.
    static void InitLocManager(Harmony harmony)
    {
        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LocManager));
        typeof(LocManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!.SetValue(null, instance);
        var tables = TableNames.ToDictionary(n => n, n => new LocTable(n, new Dictionary<string, string>()));
        typeof(LocManager).GetField("_tables", Any)!.SetValue(instance, tables);
        typeof(LocManager).GetField("_engTables", Any)?.SetValue(instance, tables);
        try { typeof(LocManager).GetProperty("Language")?.SetValue(instance, "eng"); } catch { }
        try { typeof(LocManager).GetProperty("CultureInfo")?.SetValue(instance, System.Globalization.CultureInfo.InvariantCulture); } catch { }
        // The game's own formatter setup (SmartFormat extensions); the card text previews go through it.
        typeof(LocManager).GetMethod("LoadLocFormatters", Any)!.Invoke(instance, null);

        harmony.Patch(typeof(LocTable).GetMethod("GetRawText", BindingFlags.Instance | BindingFlags.Public, null, [typeof(string)], null)!,
            prefix: Prefix(nameof(GetRawText)));
        harmony.Patch(typeof(LocTable).GetMethod("GetLocString")!, prefix: Prefix(nameof(GetLocString)));
        harmony.Patch(typeof(LocTable).GetMethod("HasEntry", Any)!, prefix: Prefix(nameof(True)));
        harmony.Patch(typeof(LocTable).GetMethod("IsLocalKey", Any)!, prefix: Prefix(nameof(True)));
        harmony.Patch(typeof(LocString).GetMethod("Exists", BindingFlags.Static | BindingFlags.Public)!, prefix: Prefix(nameof(True)));
        harmony.Patch(typeof(LocTable).GetMethod("GetLocStringsWithPrefix", Any)!, prefix: Prefix(nameof(NoLocStrings)));
        // Inherited from sts2-cli, where Neutralize.OnPlay's DamageCmd.Attack() chain NREd headless. It replaces the
        // card's gameplay with damage + Weak, so it is a parity risk for Silent fights; see docs/combat-parity.md.
        harmony.Patch(typeof(MegaCrit.Sts2.Core.Models.Cards.Neutralize).GetMethod("OnPlay", BindingFlags.Instance | BindingFlags.NonPublic)!,
            prefix: Prefix(nameof(Neutralize)));
    }

    static readonly string[] TableNames =
    [
        "achievements", "acts", "afflictions", "ancients", "ascension", "bestiary", "card_keywords", "card_library",
        "card_reward_ui", "card_selection", "cards", "characters", "combat_messages", "credits", "enchantments",
        "encounters", "epochs", "eras", "events", "ftues", "game_over_screen", "gameplay_ui", "inspect_relic_screen",
        "intents", "main_menu_ui", "map", "merchant_room", "modifiers", "monsters", "orbs", "potion_lab", "potions",
        "powers", "relic_collection", "relics", "rest_site_ui", "run_history", "settings_ui", "static_hover_tips",
        "stats_screen", "timeline", "vfx",
    ];

    static HarmonyMethod Prefix(string name) => new(typeof(HeadlessInit).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!);

    static bool CmdWait(ref Task __result) { __result = Task.CompletedTask; return false; }
    static bool TalkCmdPlay(ref MegaCrit.Sts2.Core.Nodes.Vfx.NSpeechBubbleVfx? __result) { __result = null; return false; }
    static bool GetRawText(string key, ref string __result) { __result = key; return false; }
    static bool True(ref bool __result) { __result = true; return false; }
    static bool NoLocStrings(ref IReadOnlyList<LocString> __result) { __result = new List<LocString>(); return false; }

    static readonly FieldInfo LocTableName = typeof(LocTable).GetField("_name", Any)!;
    static bool GetLocString(LocTable __instance, string key, ref LocString __result)
    {
        __result = new LocString(LocTableName.GetValue(__instance) as string ?? "_unknown", key);
        return false;
    }

    static bool Neutralize(CardModel __instance, ref Task __result, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        __result = cardPlay.Target == null ? Task.CompletedTask : NeutralizeSafe(__instance, choiceContext, cardPlay);
        return false;
    }

    static async Task NeutralizeSafe(CardModel card, PlayerChoiceContext ctx, CardPlay play)
    {
        try
        {
            await CreatureCmd.Damage(ctx, play.Target!, card.DynamicVars.Damage, card, play);
            await PowerCmd.Apply<WeakPower>(ctx, play.Target!, card.DynamicVars["WeakPower"].BaseValue, card.Owner.Creature, card, false);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] Neutralize safe: {ex.Message}"); }
    }
}

// Runs a posted continuation inline, queueing nested posts and draining them after, so Task.Yield and awaits that
// capture the context resume on the calling thread at once.
class InlineSynchronizationContext : SynchronizationContext
{
    readonly Queue<(SendOrPostCallback, object?)> _queue = new();
    bool _executing;

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (_executing) { _queue.Enqueue((d, state)); return; }
        _executing = true;
        try
        {
            d(state);
            while (_queue.Count > 0) { var (cb, st) = _queue.Dequeue(); cb(st); }
        }
        finally { _executing = false; }
    }

    public override void Send(SendOrPostCallback d, object? state) => d(state);
}
