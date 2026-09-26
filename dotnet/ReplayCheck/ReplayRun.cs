using System.Diagnostics;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using static Substrate;

// One recorded combat: inspect the tape, replay it as the game's RunReplay does, compare every checkpoint.
sealed class ReplayRun(string mcrPath, string outDir, bool inspectOnly, bool restorePostActionChecksum, bool strictSteps, int dropEvent)
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    CombatReplay _replay = null!;
    readonly List<Dictionary<string, object?>> _checks = new();
    int _eventIndex = -1;
    int _mainThread;
    object? _firstDivergence;
    readonly List<object> _steps = new();
    readonly HashSet<GameAction> _executed = new();

    public int Execute()
    {
        Directory.CreateDirectory(outDir);
        var boot = Stopwatch.StartNew();
        Boot();
        long bootMs = boot.ElapsedMilliseconds;

        var reader = new PacketReader();
        reader.Reset(File.ReadAllBytes(mcrPath));
        _replay = reader.Read<CombatReplay>();
        Inspect(bootMs);
        if (inspectOnly) return 0;

        _mainThread = Environment.CurrentManagedThreadId;
        var load = Stopwatch.StartNew();
        // --- NMultiplayerTest.RunReplay, minus scene/asset/audio calls ---
        RunState runState = RunState.FromSerializable(_replay.serializableRun);
        ulong netId = runState.Players[0].NetId;
        RunManager.Instance.SetUpReplay(runState, _replay, netId);
        // TestMode.IsOn (set by sts2-cli) disables the game's checksum oracle; turn it back on.
        RunManager.Instance.ChecksumTracker.IsEnabled = true;
        RunManager.Instance.ChecksumTracker.ChecksumGenerated += OnChecksum;
        if (restorePostActionChecksum) RestorePostActionChecksum();
        RunManager.Instance.ActionExecutor.BeforeActionExecuted += a => _executed.Add(a);
        LocalContext.NetId = netId;
        RunManager.Instance.CombatStateSynchronizer.IsDisabled = true;
        RunManager.Instance.Launch();
        Await(RunManager.Instance.GenerateMap(), "GenerateMap");
        RunManager.Instance.ActionQueueSet.FastForwardNextActionId(_replay.nextActionId);
        RunManager.Instance.ActionQueueSynchronizer.FastForwardHookId(_replay.nextHookId);
        RunManager.Instance.ChecksumTracker.LoadReplayChecksums(_replay.checksumData, _replay.nextChecksumId);
        RunManager.Instance.PlayerChoiceSynchronizer.FastForwardChoiceIds(_replay.choiceIds);
        RunManager.Instance.RewardsSetSynchronizer.FastForwardRewardIds(_replay.rewardIds);
        Await(RunManager.Instance.LoadIntoLatestMapCoord(AbstractRoom.FromSerializable(_replay.serializableRun.PreFinishedRoom, runState)), "LoadIntoLatestMapCoord");
        Require(WaitFor(() => !RunManager.Instance.ActionExecutor.IsPaused), "ActionExecutor unpause after room entry");
        long loadMs = load.ElapsedMilliseconds;

        var step = Stopwatch.StartNew();
        string? stall = null;
        try
        {
            for (_eventIndex = 0; _eventIndex < _replay.events.Count; _eventIndex++)
            {
                if (_eventIndex == dropEvent) continue; // negative control
                var t0 = Stopwatch.GetTimestamp();
                Dispatch(_replay.events[_eventIndex], runState);
                if (strictSteps) _steps.Add(new { i = _eventIndex, boundary = AwaitStepBoundary(runState), ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds });
            }
            if (!WaitFor(() => RunManager.Instance.ActionExecutor.FinishedExecutingActions().IsCompleted))
                stall = "queue did not drain after last event";
        }
        catch (Exception ex) { stall = $"event {_eventIndex}: {ex.GetType().Name}: {ex.Message}"; File.WriteAllText(Path.Combine(outDir, "stall.txt"), ex.ToString()); }
        long stepMs = step.ElapsedMilliseconds;

        var seen = _checks.Select(c => (uint)c["id"]!).ToHashSet();
        var unreached = _replay.checksumData.Where(c => !seen.Contains(c.checksumData.id))
            .Select(c => new { id = c.checksumData.id, c.context }).ToList();
        var report = new Dictionary<string, object?>
        {
            ["events"] = _replay.events.Count,
            ["events_dispatched"] = Math.Min(_eventIndex, _replay.events.Count),
            ["recorded_checksums"] = _replay.checksumData.Count,
            ["local_checksums"] = _checks.Count,
            ["matched"] = _checks.Count(c => (bool)c["match"]!),
            ["mismatched"] = _checks.Count(c => !(bool)c["match"]!),
            ["unreached_recorded"] = unreached,
            ["first_divergence"] = _firstDivergence,
            ["stall"] = stall,
            ["terminal"] = Terminal(runState),
            ["main_thread"] = _mainThread,
            ["checkpoint_threads"] = _checks.GroupBy(c => (int)c["thread"]!).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            ["steps"] = _steps,
            ["timing_ms"] = new { boot = bootMs, load_into_combat = loadMs, stepping = stepMs, per_event = _replay.events.Count == 0 ? 0 : (double)stepMs / _replay.events.Count },
            ["checks"] = _checks,
        };
        File.WriteAllText(Path.Combine(outDir, "replay_report.json"), JsonSerializer.Serialize(report, Pretty));
        Console.WriteLine(JsonSerializer.Serialize(report.Where(kv => kv.Key != "checks").ToDictionary(), Pretty));
        return _firstDivergence == null && stall == null && unreached.Count == 0 ? 0 : 2;
    }

    // Step boundary = the dispatched input has resolved and the next input is available, or terminal.
    // Queue drained (a choice-paused action also drains the queue), end-turn phases finished, and either
    // combat is over or it is the player's side again.
    string AwaitStepBoundary(RunState runState)
    {
        Await(RunManager.Instance.ActionExecutor.FinishedExecutingActions(), $"queue drain after event {_eventIndex}");
        Require(WaitFor(() => !CombatManager.Instance.EndingPlayerTurnPhaseOne && !CombatManager.Instance.EndingPlayerTurnPhaseTwo), "end-turn phases");
        if (!CombatManager.Instance.IsInProgress || runState.Players[0].Creature.IsDead) return "terminal";
        Require(WaitFor(() => CombatManager.Instance.DebugOnlyGetState()?.CurrentSide != CombatSide.Enemy
            || !CombatManager.Instance.IsInProgress), "player side");
        Await(RunManager.Instance.ActionExecutor.FinishedExecutingActions(), $"queue drain (player side) after event {_eventIndex}");
        if (!CombatManager.Instance.IsInProgress || runState.Players[0].Creature.IsDead) return "terminal";
        bool choicePending = _executed.Any(a => a.State == GameActionState.GatheringPlayerChoice);
        return choicePending ? "awaiting_choice" : "awaiting_input";
    }

    static object Terminal(RunState runState)
    {
        var cs = CombatManager.Instance.DebugOnlyGetState();
        var me = runState.Players[0].Creature;
        return new
        {
            player_hp = me.CurrentHp, player_dead = me.IsDead, combat_in_progress = CombatManager.Instance.IsInProgress,
            enemies = cs?.Enemies.Select(e => new { id = e.Monster?.Id.ToString(), hp = e.CurrentHp, dead = e.IsDead }).ToList(),
        };
    }

    // Mirrors NMultiplayerTest.RunReplay's event loop; frame waits become polling waits.
    void Dispatch(CombatReplayEvent e, RunState runState)
    {
        switch (e.eventType)
        {
            case CombatReplayEventType.GameAction:
            {
                Require(WaitFor(() => !CombatManager.Instance.EndingPlayerTurnPhaseOne && !CombatManager.Instance.EndingPlayerTurnPhaseTwo), "end-turn phases");
                GameAction action = e.action!.ToGameAction(runState.GetPlayer(e.playerId!.Value)!);
                if (action.ActionType == GameActionType.CombatPlayPhaseOnly)
                    Require(WaitFor(() => CombatManager.Instance.DebugOnlyGetState()?.CurrentSide != CombatSide.Enemy), "player side");
                RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(action);
                if (action is EndPlayerTurnAction || action is ReadyToBeginEnemyTurnAction)
                    Await(RunManager.Instance.ActionExecutor.FinishedExecutingActions(), $"FinishedExecutingActions after {action}");
                break;
            }
            case CombatReplayEventType.HookAction:
                RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(
                    RunManager.Instance.ActionQueueSynchronizer.GetHookActionForId(e.hookId!.Value, e.playerId!.Value, e.gameActionType!.Value));
                break;
            case CombatReplayEventType.ResumeAction:
                RunManager.Instance.ActionQueueSet.ResumeActionWithoutSynchronizing(e.actionId!.Value);
                break;
            case CombatReplayEventType.PlayerChoice:
                RunManager.Instance.PlayerChoiceSynchronizer.ReceiveReplayChoice(runState.GetPlayer(e.playerId!.Value)!, e.choiceId!.Value, e.playerChoiceResult!.Value);
                break;
        }
    }

    void OnChecksum(NetChecksumData local, string context, NetFullCombatState localState)
    {
        int idx = _replay.checksumData.FindIndex(c => c.checksumData.id == local.id);
        bool match = false; string? replayContext = null; uint? replayHash = null;
        if (idx >= 0)
        {
            ReplayChecksumData rec = _replay.checksumData[idx];
            replayContext = rec.context;
            replayHash = Hash(rec.fullState);
            match = replayHash == local.checksum && NormalizeContext(rec.context) == NormalizeContext(context);
        }
        _checks.Add(new() { ["id"] = local.id, ["event_index"] = _eventIndex, ["context"] = context, ["replay_context"] = replayContext, ["local"] = local.checksum, ["replay"] = replayHash, ["match"] = match, ["thread"] = Environment.CurrentManagedThreadId });
        if (!match && _firstDivergence == null)
        {
            string localText = localState.ToString();
            string replayText = idx >= 0 ? _replay.checksumData[idx].fullState.ToString() : "<no recorded checksum with this id>";
            File.WriteAllText(Path.Combine(outDir, $"diverge_{local.id}_local.txt"), localText);
            File.WriteAllText(Path.Combine(outDir, $"diverge_{local.id}_replay.txt"), replayText);
            string[] a = localText.Split('\n'), b = replayText.Split('\n');
            var diffs = Enumerable.Range(0, Math.Max(a.Length, b.Length))
                .Where(i => i >= a.Length || i >= b.Length || a[i] != b[i])
                .Take(12).Select(i => new { line = i, expected = i < b.Length ? b[i].Trim() : null, actual = i < a.Length ? a[i].Trim() : null }).ToList();
            object? lastEvent = _eventIndex >= 0 && _eventIndex < _replay.events.Count ? Describe(_replay.events[_eventIndex], _eventIndex) : null;
            _firstDivergence = new { checksum_id = local.id, local_context = context, replay_context = replayContext, last_dispatched_event = lastEvent, differing_lines = diffs };
        }
    }

    void Inspect(long bootMs)
    {
        SerializableRun run = _replay.serializableRun;
        var p = run.Players[0];
        var summary = new Dictionary<string, object?>
        {
            ["version"] = _replay.version, ["git_commit"] = _replay.gitCommit,
            ["model_id_hash_recorded"] = _replay.modelIdHash, ["model_id_hash_host"] = ModelIdSerializationCache.Hash,
            ["boot_ms"] = bootMs,
            ["initial_state"] = new
            {
                seed = run.SerializableRng?.Seed, run.Ascension, act_index = run.CurrentActIndex,
                visited_coords = run.VisitedMapCoords.Count, last_coord = run.VisitedMapCoords.LastOrDefault().ToString(),
                pre_finished_room = run.PreFinishedRoom?.ToString(),
                character = p.CharacterId?.ToString(), hp = p.CurrentHp, max_hp = p.MaxHp, p.Gold, p.MaxEnergy,
                deck = p.Deck.Select(c => c.ToString()).ToList(), relics = p.Relics.Select(r => r.ToString()).ToList(),
                potions = p.Potions.Select(x => x.ToString()).ToList(),
            },
            ["id_cursors"] = new { _replay.nextActionId, _replay.nextChecksumId, _replay.nextHookId, choice_ids = _replay.choiceIds, reward_ids = _replay.rewardIds },
            ["event_counts"] = _replay.events.GroupBy(e => e.eventType.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            ["events"] = _replay.events.Select(Describe).ToList(),
            // recorded: hash the game computed live (real netId); what Spirebird also captured.
            // rehash: hash of the stored state after WriteReplay's Anonymized(); what a replay must reproduce.
            ["checksums"] = _replay.checksumData.Select(c => new { id = c.checksumData.id, c.context, recorded = c.checksumData.checksum, rehash = Hash(c.fullState) }).ToList(),
        };
        File.WriteAllText(Path.Combine(outDir, "mcr_inspect.json"), JsonSerializer.Serialize(summary, Pretty));
        File.WriteAllText(Path.Combine(outDir, "mcr_initial_run.json"), JsonSerializer.Serialize(run, JsonSerializationUtility.GetTypeInfo<SerializableRun>()));
        if (_replay.checksumData.Count > 0)
            File.WriteAllText(Path.Combine(outDir, "mcr_first_state.txt"), _replay.checksumData[0].fullState.ToString());
        Console.WriteLine(JsonSerializer.Serialize(summary.Where(kv => kv.Key is not ("events" or "checksums")).ToDictionary(), Pretty));
    }

    static object Describe(CombatReplayEvent e, int i) => e.eventType switch
    {
        CombatReplayEventType.GameAction => new { i, type = "action", detail = e.action?.ToString(), net_type = e.action?.GetType().Name },
        CombatReplayEventType.HookAction => new { i, type = "hook", detail = (string?)$"hook {e.hookId} {e.gameActionType}", net_type = (string?)null },
        CombatReplayEventType.ResumeAction => new { i, type = "resume", detail = (string?)$"action {e.actionId}", net_type = (string?)null },
        _ => new { i, type = "choice", detail = (string?)$"choice {e.choiceId} {e.playerChoiceResult?.type}", net_type = (string?)null },
    };
}
