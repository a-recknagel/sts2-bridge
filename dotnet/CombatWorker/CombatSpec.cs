using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// A combat described by what a player brings into it, turned into the save the game would load it from.
//
//   {"character": "CHARACTER.DEFECT", "ascension": 10, "seed": "ANY", "encounter": "ENCOUNTER.THE_INSATIABLE_BOSS",
//    "act": 1,                                   optional; defaults to the act whose encounter list has it
//    "player": {"current_hp": 50, "max_hp": 75, "deck": [...], "relics": [...], "potions": [...], ...},
//    "run": {"rng": {...}, "map_point_history": [[...]]}}       optional
//
// "player" is a partial SerializablePlayer in the game's own JSON, the shape a .run history file (the local
// saves/history and the Spire Codex export alike) records its players in: deck entries are SerializableCards with
// upgrades, enchantments and props, relics are SerializableRelics with their saved counters, potions carry their
// slot. Any field it names replaces the one in a fresh run of that character, ascension and seed; the rest (player
// RNG, odds, unlocks, max energy, orb slots) stays as the new run made it.
//
// The fresh run goes through RunManager.SetUpNewSingleplayer, the game's own new-run setup (starting inventory,
// ascension effects, room generation), and comes back out as RunManager.ToSave. The player is then overlaid and the
// result loaded like any save: nothing is obtained, so relics' on-pickup effects do not fire a second time. What
// the spec says the player has is what they have.
//
// "run" is a partial SerializableRun, overlaid the same way before the player. It is how a spec re-enters a fight a
// run really had: the run's RNG counters at that combat, and the map history before it, which sets the floor number
// the encounter seeds its monsters with (EncounterModel.GenerateMonstersWithSlots).
static class CombatSpec
{
    public static (SerializableRun Save, EncounterModel Encounter) ToSave(JsonObject spec)
    {
        CharacterModel character = ModelDb.GetById<CharacterModel>(Id(spec, "character"));
        EncounterModel encounter = ModelDb.GetById<EncounterModel>(Id(spec, "encounter"));
        int ascension = (int?)spec["ascension"] ?? 0;
        string seed = (string?)spec["seed"] ?? throw new ArgumentException("spec needs a seed");
        int actIndex = (int?)spec["act"] ?? ActIndexOf(encounter);

        List<ActModel> acts = ActModel.GetDefaultList().ToList();
        acts[actIndex] = ActFor(actIndex, encounter);
        var player = Player.CreateForNewRun(character, UnlockState.all, 1uL);
        RunState run = RunState.CreateForNewRun(new[] { player }, acts.Select(a => a.ToMutable()).ToList(),
            Array.Empty<ModifierModel>(), GameMode.Standard, ascension, seed);
        run.CurrentActIndex = actIndex;

        RunManager rm = RunManager.Instance;
        if (rm.IsInProgress) rm.CleanUp(graceful: true);
        rm.SetUpNewSingleplayer(run, shouldSave: false);
        SerializableRun save = rm.ToSave(null);
        rm.CleanUp(graceful: true);
        // A new run has not drawn its map yet; what ToSave captures is a placeholder with no nodes. Without it, loading
        // generates the act's real map from the seed, as the start of an act does.
        foreach (SerializableActModel act in save.Acts) act.SavedMap = null;

        if (spec["run"] is JsonObject runOverlay)
        {
            SerializablePlayer fresh = save.Players[0];
            save = Merge(save, runOverlay, key => key is "players" or "acts"
                ? $"run.{key} comes from the spec's character, act and encounter" : null);
            save.Players[0] = fresh;
            foreach (SerializableActModel act in save.Acts) act.SavedMap = null;
        }
        if (spec["player"] is JsonObject overlay) save.Players[0] = Overlay(save.Players[0], overlay);
        return (save, encounter);
    }

    // Round-trips through the game's serializer, so an overlay is checked field by field the way a save file is.
    static SerializablePlayer Overlay(SerializablePlayer fresh, JsonObject overlay)
    {
        RequireKnownIds(overlay);
        return Merge(fresh, overlay, key => key is "character_id" or "net_id"
            ? $"player.{key} comes from the spec's character and the worker" : null);
    }

    static T Merge<T>(T fresh, JsonObject overlay, Func<string, string?> refuse)
    {
        JsonObject merged = JsonSerializer.SerializeToNode(fresh, JsonSerializationUtility.Options)!.AsObject();
        foreach ((string key, JsonNode? value) in overlay)
        {
            if (refuse(key) is string why) throw new ArgumentException(why);
            merged[key] = value?.DeepClone();
        }
        return merged.Deserialize<T>(JsonSerializationUtility.Options)
            ?? throw new ArgumentException($"{typeof(T).Name} overlay deserialised to null");
    }

    // A save with an id this build does not know loads anyway: SaveUtil swaps in DeprecatedCard, DeprecatedRelic and
    // so on. That is right for a player's save file and wrong for a spec, where it would start a different fight from
    // the one asked for. Modded runs in the Spire Codex export and runs from older builds both hit this.
    static void RequireKnownIds(JsonObject overlay)
    {
        var unknown = new List<string>();
        void Check<T>(JsonNode? entry) where T : AbstractModel
        {
            string? id = (string?)entry?["id"];
            if (id == null) return;
            bool known;
            try { known = ModelDb.GetByIdOrNull<T>(ModelId.Deserialize(id)) != null; }
            catch (JsonException) { known = false; }
            if (!known) unknown.Add(id);
        }
        foreach (JsonNode? card in overlay["deck"]?.AsArray() ?? new JsonArray())
        {
            Check<CardModel>(card);
            Check<EnchantmentModel>(card?["enchantment"]);
        }
        foreach (JsonNode? relic in overlay["relics"]?.AsArray() ?? new JsonArray()) Check<RelicModel>(relic);
        foreach (JsonNode? potion in overlay["potions"]?.AsArray() ?? new JsonArray()) Check<PotionModel>(potion);
        if (unknown.Count > 0)
            throw new ArgumentException($"ids not in this build: {string.Join(", ", unknown.Distinct())}");
    }

    static int ActIndexOf(EncounterModel encounter)
    {
        IReadOnlyList<IReadOnlyList<ActModel>> byIndex = ModelDb.ActsByIndex;
        for (int i = 0; i < byIndex.Count; i++)
            if (byIndex[i].Any(a => a.AllEncounters.Any(e => e.Id == encounter.Id))) return i;
        return 0; // event-only encounters belong to no act's list
    }

    // Some acts have alternatives at the same index (Overgrowth and Underdocks); take the one the encounter is from.
    static ActModel ActFor(int index, EncounterModel encounter)
    {
        IReadOnlyList<ActModel> candidates = ModelDb.ActsByIndex[index];
        return candidates.FirstOrDefault(a => a.AllEncounters.Any(e => e.Id == encounter.Id))
            ?? candidates.First(a => a.IsDefault);
    }

    static ModelId Id(JsonObject spec, string key) =>
        ModelId.Deserialize((string?)spec[key] ?? throw new ArgumentException($"spec needs {key}"));

    // What a spec can name: characters, and every encounter with the act and room type it belongs to.
    public static object Catalog() => new
    {
        ok = true,
        characters = ModelDb.AllCharacters.Select(c => c.Id.ToString()).ToList(),
        starting_hp = ModelDb.AllCharacters.ToDictionary(c => c.Id.ToString(), c => c.StartingHp),
        encounters = ModelDb.AllEncounters.Select(e => new
        {
            id = e.Id.ToString(),
            room_type = e.RoomType.ToString(),
            acts = ModelDb.Acts.Where(a => a.AllEncounters.Any(x => x.Id == e.Id)).Select(a => a.Id.ToString()).ToList(),
            act_index = ActIndexOf(e),
            // In an act's weak pool: the hallway fights the game draws for the first floors of the act.
            weak = ModelDb.Acts.Any(a => a.AllWeakEncounters.Any(x => x.Id == e.Id)),
        }).ToList(),
    };
}
