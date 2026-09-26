using System.Reflection;
using System.Text.Json.Nodes;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

// sts2-cli's substrate patch for FromChooseABundleScreen chooses bundle zero when no RunSimulator is installed.
// Replace only that prefix. A run waits for its caller; other CombatWorker modes use the game's TestMode branch.
static class BundleSelector
{
    static bool _installed;
    static PendingBundle? _pending;
    static bool _runActive;

    public static bool HasPending => _pending != null;
    public static object? Describe => _pending == null ? null : new
    {
        bundles = _pending.Bundles.Select((cards, index) => new
        {
            index, cards = cards.Select(card => card.Id.ToString()).ToList(),
        }).ToList(),
    };
    public static List<object> Legal => _pending == null ? new List<object>() :
        Enumerable.Range(0, _pending.Bundles.Count).Select(i => (object)new { type = "bundle", index = i }).ToList();

    public static void Activate()
    {
        if (!_installed)
        {
            MethodInfo method = typeof(CardSelectCmd).GetMethod("FromChooseABundleScreen")!;
            var harmony = new Harmony("combat-parity.bundle-decisions");
            harmony.Unpatch(method, HarmonyPatchType.Prefix, "sts2headless.locpatch");
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(BundleSelector).GetMethod(nameof(Prefix))!));
            _installed = true;
        }
        _pending = null;
        _runActive = true;
    }

    public static void Deactivate() { _pending = null; _runActive = false; }

    public static bool Prefix(Player player, IReadOnlyList<IReadOnlyList<CardModel>> bundles,
        ref Task<IEnumerable<CardModel>> __result)
    {
        if (!_runActive) return true;
        if (bundles.Count == 0)
        {
            __result = Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());
            return false;
        }
        if (_pending != null) throw new InvalidOperationException("another bundle choice is pending");
        uint choiceId = RunManager.Instance.PlayerChoiceSynchronizer.ReserveChoiceId(player);
        var pending = new PendingBundle(player, bundles, choiceId);
        _pending = pending;
        __result = Resolve(pending);
        return false;
    }

    static async Task<IEnumerable<CardModel>> Resolve(PendingBundle pending)
    {
        int index = await pending.Answer.Task;
        IReadOnlyList<CardModel> cards = pending.Bundles[index];
        RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(pending.Player, pending.ChoiceId,
            PlayerChoiceResult.FromIndex(index));
        typeof(CardSelectCmd).GetMethod("LogChoice", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object?[] { pending.Player, cards });
        return cards;
    }

    public static void Choose(JsonObject action)
    {
        PendingBundle pending = _pending ?? throw new InvalidOperationException("no bundle choice is pending");
        int index = (int?)action["index"] ?? throw new ArgumentException("bundle choice needs index");
        if (index < 0 || index >= pending.Bundles.Count) throw new ArgumentException("bundle index out of range");
        _pending = null;
        pending.Answer.SetResult(index);
    }

    sealed record PendingBundle(Player Player, IReadOnlyList<IReadOnlyList<CardModel>> Bundles, uint ChoiceId)
    {
        public TaskCompletionSource<int> Answer { get; } = new();
    }
}
