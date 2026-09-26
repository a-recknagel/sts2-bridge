using System.Text.Json;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves;
using static Substrate;

// A recorded .mcr, decoded into the caller's vocabulary: what was played, and every checkpoint the game took.
// The worker itself never reads it while stepping; a caller uses it as a teacher or as an oracle.
static class Tape
{
    public static CombatReplay ReadReplay(string mcrPath)
    {
        var reader = new PacketReader();
        reader.Reset(File.ReadAllBytes(mcrPath));
        return reader.Read<CombatReplay>();
    }

    public static object Read(string mcrPath)
    {
        CombatReplay replay = ReadReplay(mcrPath);
        return new
        {
            ok = true,
            version = replay.version,
            // Most of the fight as a spec (CombatSpec): the recorded player in the game's save JSON, which is also
            // the shape of a .run history file's player. The encounter is not in the initial state (the game pulls
            // it from the act when the room is entered); a loaded session's obs.encounter names it.
            initial = new
            {
                character = replay.serializableRun.Players[0].CharacterId!.ToString(),
                ascension = replay.serializableRun.Ascension,
                act = replay.serializableRun.CurrentActIndex,
                player = JsonSerializer.SerializeToNode(replay.serializableRun.Players[0], JsonSerializationUtility.Options),
            },
            git_commit = replay.gitCommit,
            events = replay.events.Select((e, i) => Describe(e, i)).ToList(),
            // The .mcr stores anonymised states, so the hash a replay must reproduce is the re-hash of the stored
            // state, not the live hash stored beside it (the game's CheckAgainstReplayChecksum does the same).
            checkpoints = replay.checksumData.Select(c => new { id = c.checksumData.id, context = c.context, hash = Hash(c.fullState) }).ToList(),
        };
    }

    static object Describe(CombatReplayEvent e, int i) => e.eventType switch
    {
        CombatReplayEventType.GameAction => e.action switch
        {
            NetPlayCardAction p => new { i, kind = "play", combat_card = (uint?)p.card.CombatCardIndex, card = p.modelId.ToString(), target_combat_id = p.targetId },
            NetEndPlayerTurnAction t => new { i, kind = "end_turn", turn = t.turnNumber },
            NetReadyToBeginEnemyTurnAction => new { i, kind = "ready_to_begin_enemy_turn" },
            NetUsePotionAction u => (object)new { i, kind = "potion", slot = u.potionIndex, target_combat_id = u.targetId, target_player = u.targetPlayerId },
            var a => new { i, kind = "action", net_type = a?.GetType().Name, detail = a?.ToString() },
        },
        CombatReplayEventType.HookAction => new { i, kind = "hook", hook_id = e.hookId, action_type = e.gameActionType?.ToString() },
        CombatReplayEventType.ResumeAction => new { i, kind = "resume", action_id = e.actionId },
        _ => new
        {
            i, kind = "choice", choice_id = e.choiceId,
            result_type = e.playerChoiceResult?.type.ToString(),
            combat_cards = e.playerChoiceResult?.combatCards?.Select(c => c.CombatCardIndex).ToList(),
            indexes = e.playerChoiceResult?.indexes,
        },
    };
}
