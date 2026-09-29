using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;

// What a card choice is for. The selector the game consults gets only the options and pick counts; the CardSelectCmd
// method that asks for it knows the screen, the prompt the game would show (TO_DISCARD, HOLOGRAM.selectionScreenPrompt…)
// and, for hand selections, the model that asked. A prefix on each method records them for the selector to take.
// It only reads its arguments, so the fight plays the same.
static class ChoiceContext
{
    static bool _installed;
    static Context? _next;

    public sealed record Context(string Screen, string? Prompt, string? Source);

    public static void Install()
    {
        if (_installed) return;
        var harmony = new Harmony("combat-parity.choice-context");
        var prefix = new HarmonyMethod(typeof(ChoiceContext).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)!);
        // FromChooseABundleScreen is Neow's pack choice, which BundleSelector answers.
        foreach (MethodInfo m in typeof(CardSelectCmd).GetMethods(BindingFlags.Static | BindingFlags.Public)
                     .Where(m => m.Name.StartsWith("From") && m.Name != "FromChooseABundleScreen"))
            harmony.Patch(m, prefix: prefix);
        _installed = true;
    }

    static void Prefix(MethodBase __originalMethod, object[] __args)
    {
        ParameterInfo[] ps = __originalMethod.GetParameters();
        string? prompt = null, source = null;
        for (int i = 0; i < ps.Length && i < __args.Length; i++)
        {
            if (__args[i] is CardSelectorPrefs prefs && prefs.Prompt is { } p) prompt = p.LocEntryKey;
            else if (ps[i].Name == "source" && __args[i] is AbstractModel model) source = model.Id.ToString();
        }
        _next = new Context(__originalMethod.Name, prompt, source);
    }

    // The context of the choice being asked for now; each call to a CardSelectCmd method replaces it.
    public static Context? Take()
    {
        Context? c = _next;
        _next = null;
        return c;
    }
}
