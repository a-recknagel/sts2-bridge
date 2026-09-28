using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using static Substrate;

// One native RunState from new-run setup through room travel. Run-level decisions stay on this manager; the
// combat action adapter only drives combat. Unsupported choices fail explicitly rather than inventing a state.
sealed class RunSession
{
    readonly RunState _run;
    readonly Player _me;
    readonly CombatSession _combat;
    readonly string _identity = Guid.NewGuid().ToString("N");
    // Offered reward sets, innermost last. Taking a reward can offer another set before it finishes (Kaleidoscope,
    // Neow's Bones' relics); the outer selection's task then waits on the inner set, as the game's screens stack.
    readonly List<OfferedSet> _sets = new();
    RewardsSet? _pendingRewards => _sets.LastOrDefault()?.Set;
    Task? _pendingRoomTask;
    string? _returnAfterRewards;
    object? _lastCombat;
    string _stage = "awaiting_map";
    int _decision;

    RunSession(RunState run)
    {
        _run = run;
        _me = run.Players[0];
        _combat = CombatSession.Attach(run);
        RewardsSet.testSelector = set =>
        {
            set.ThrowInTestIfRewardsNotTaken = false;
            var offered = new OfferedSet(set);
            _sets.Add(offered);
            return offered.Done.Task;
        };
    }

    public static RunSession Start(JsonObject spec)
    {
        string seed = (string?)spec["seed"] ?? throw new ArgumentException("run needs a seed");
        string characterId = (string?)spec["character"] ?? throw new ArgumentException("run needs a character");
        string unlocks = (string?)spec["unlocks"] ?? "all";
        UnlockState unlockState = unlocks switch
        {
            "all" => UnlockState.all,
            "none" => UnlockState.none,
            _ => throw new ArgumentException("unlocks must be all or none"),
        };
        int ascension = (int?)spec["ascension"] ?? 0;
        if (ascension < 0 || ascension > 10) throw new ArgumentException("ascension must be 0..10 for this build");
        RunManager rm = RunManager.Instance;
        if (rm.IsInProgress) rm.CleanUp(graceful: true);
        CharacterModel character = ModelDb.GetById<CharacterModel>(ModelId.Deserialize(characterId));
        Player player = Player.CreateForNewRun(character, unlockState, 1uL);
        RunState run = RunState.CreateForNewRun(new[] { player },
            ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList(),
            Array.Empty<ModifierModel>(), GameMode.Standard, ascension, seed);
        rm.SetUpNewSingleplayer(run, shouldSave: false);
        // The shipped game records every combat (off only because HeadlessInit sets TestMode). Its initial state is
        // taken inside EnterMapPointInternal just before the room is rolled, which is what CombatSnapshot writes.
        rm.CombatReplayWriter.IsEnabled = true;
        rm.Launch();
        var session = new RunSession(run);
        BundleSelector.Activate();
        Await(rm.FinalizeStartingRelics(), "FinalizeStartingRelics");
        Await(rm.EnterAct(0, doTransition: false), "EnterAct(0)");
        session.ClassifyRoom();
        return session;
    }

    public object Step(JsonObject action)
    {
        string type = (string?)action["type"] ?? throw new ArgumentException("action needs a type");
        object? combatReply = null;
        switch (_stage)
        {
            case "awaiting_map" when type == "map":
                SelectMap(action);
                ClassifyRoom();
                if (_stage == "combat") combatReply = _combat.Report("map");
                break;
            case "combat":
                combatReply = _combat.Step(action);
                if (!CombatManager.Instance.IsInProgress)
                {
                    if (_me.Creature.IsDead || _run.IsGameOver) _stage = "terminal";
                    else OfferNativeRewards();
                }
                break;
            case "awaiting_rewards" when type == "take_reward":
                TakeReward(action);
                break;
            case "awaiting_rewards" when type == "skip_rewards":
                if (_pendingRewards!.DisallowSkipping) throw new InvalidOperationException("this rewards set cannot be skipped");
                RunManager.Instance.RewardsSetSynchronizer.SkipLocalRewardsSet();
                FinishSet();
                SettleRewards();
                break;
            case "awaiting_proceed" when type == "proceed":
                _combat.DisableRewardSelector();
                // NRewardsScreen.OnProceedButtonPressed: after a boss, the player votes to move on (VoteToMoveToNextAct),
                // unless it is the first of two bosses. The last act's move enters the Architect's room, whose own
                // option calls WinRun. Anything else proceeds from the rewards screen, which resumes the parent event
                // of an event fight.
                if (_run.CurrentRoom is CombatRoom { RoomType: RoomType.Boss }
                    && !(_run.Map.SecondBossMapPoint != null && _run.CurrentMapCoord == _run.Map.BossMapPoint.coord))
                {
                    AbstractRoom? bossRoom = _run.CurrentRoom;
                    int act = _run.CurrentActIndex;
                    RunManager.Instance.ActChangeSynchronizer.SetLocalPlayerReady();
                    Require(WaitFor(() => _run.CurrentRoom != bossRoom && _run.CurrentRoom != null
                        && (_run.CurrentActIndex != act || _run.CurrentRoom.IsVictoryRoom)
                        && RunManager.Instance.ActionExecutor.FinishedExecutingActions().IsCompleted), "move to the next act");
                }
                else Await(RunManager.Instance.ProceedFromTerminalRewardsScreen(), "proceed from rewards");
                ClassifyRoom();
                break;
            case "event" when type == "event_option":
                ChooseEvent(action);
                break;
            case "rest" when type == "rest_option":
                ChooseRest(action);
                break;
            case "awaiting_room_choice" when type == "choose":
                _combat.AnswerRunChoice(action);
                if (_sets.Any(o => o.Selecting != null)) SettleRewards();
                else ResumeRoomTask();
                break;
            case "awaiting_bundle" when type == "bundle":
                BundleSelector.Choose(action);
                ResumeRoomTask();
                break;
            case "shop" when type == "buy":
                Buy(action);
                break;
            case "shop" when type == "leave_shop":
                _stage = "awaiting_map";
                break;
            case "treasure" when type == "open_chest":
                OpenChest();
                break;
            case "treasure_relic" when type == "pick_relic":
                PickTreasureRelic(action);
                break;
            default:
                throw new InvalidOperationException($"{type} is not legal at {_stage}");
        }
        _decision++;
        _lastCombat = combatReply;
        return Report();
    }

    // The game's own recording of the current combat, written as it writes latest.mcr but not anonymised: the
    // anonymiser swaps the player id for a random one, which the state hash covers, and this run's id is 1. Its initial
    // state is the run as it stood entering this room, so `load` on the file re-enters the same fight in another
    // process: the continuous run is never rebuilt, and a caller can try lines on the copy.
    public object CombatSnapshot(string path)
    {
        if (_stage != "combat") throw new InvalidOperationException($"no combat to snapshot at {_stage}");
        var replay = (CombatReplay?)typeof(CombatReplayWriter).GetField("_replay", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(RunManager.Instance.CombatReplayWriter) ?? throw new InvalidOperationException("the game recorded no initial state");
        var writer = new PacketWriter();
        writer.Write(replay);
        File.WriteAllBytes(path, writer.Buffer.AsSpan(0, writer.BytePosition).ToArray());
        return new { ok = true, path, events = replay.events.Count, checkpoints = replay.checksumData.Count,
            room_entry_floor = replay.serializableRun.VisitedMapCoords.Count };
    }

    void SelectMap(JsonObject action)
    {
        int col = (int?)action["col"] ?? throw new ArgumentException("map action needs col");
        int row = (int?)action["row"] ?? throw new ArgumentException("map action needs row");
        MapPoint? point = LegalMapPoints().FirstOrDefault(p => p.coord.col == col && p.coord.row == row);
        if (point == null) throw new ArgumentException($"({col},{row}) is not a connected map choice");
        var vote = new MapVote { coord = point.coord,
            mapGenerationCount = RunManager.Instance.MapSelectionSynchronizer.MapGenerationCount };
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            new VoteForMapCoordAction(_me, _run.MapLocation, vote));
        Require(WaitFor(() => _run.CurrentMapCoord == point.coord && _run.CurrentRoom != null), "map vote and room entry");
        Await(RunManager.Instance.ActionExecutor.FinishedExecutingActions(), "map action queue");
    }

    // The points NMapScreen makes travelable: the act's starting point (an act's Ancient, or act 1's first fight on a
    // fresh profile) before anything is visited, the second boss after the first, the boss from the last row, and
    // otherwise MapTravel's points from the last visited one.
    IEnumerable<MapPoint> LegalMapPoints()
    {
        ActMap map = _run.Map;
        IReadOnlyList<MapCoord> visited = _run.VisitedMapCoords;
        IEnumerable<MapPoint> points;
        if (visited.Count == 0) points = new[] { map.StartingMapPoint };
        else if (map.SecondBossMapPoint != null && visited[^1] == map.BossMapPoint.coord) points = new[] { map.SecondBossMapPoint };
        else if (visited[^1].row == map.GetRowCount() - 1) points = new[] { map.BossMapPoint };
        else points = MapTravel.GetTravelablePointsFrom(_run, map.GetPoint(visited[^1])!);
        return points.OrderBy(p => p.coord.row).ThenBy(p => p.coord.col);
    }

    void ClassifyRoom()
    {
        if (_me.Creature.IsDead || _run.IsGameOver) { _stage = "terminal"; return; }
        switch (_run.CurrentRoom)
        {
            case CombatRoom when CombatManager.Instance.IsInProgress:
                _combat.DisableRewardSelector();
                _combat.WaitForBoundary();
                _stage = "combat";
                break;
            case EventRoom eventRoom:
                _stage = eventRoom.LocalMutableEvent.IsFinished ? "awaiting_map" : "event";
                break;
            case RestSiteRoom restRoom:
                _stage = restRoom.Options.Count == 0 ? "awaiting_map" : "rest";
                break;
            case MerchantRoom:
                _stage = "shop";
                break;
            case TreasureRoom:
                _stage = "treasure";
                break;
            case MapRoom:
            case CombatRoom:   // finished, rewards done: the map is next
                _stage = "awaiting_map";
                break;
            default:
                throw new NotSupportedException($"room {_run.CurrentRoom?.GetType().Name} at {_run.CurrentMapCoord} needs a decision adapter");
        }
    }

    void ChooseEvent(JsonObject action)
    {
        if (_run.CurrentRoom is not EventRoom room) throw new InvalidOperationException("not in an event");
        int index = (int?)action["index"] ?? throw new ArgumentException("event option needs index");
        var options = room.LocalMutableEvent.CurrentOptions.ToList();
        if (index < 0 || index >= options.Count || options[index].IsLocked)
            throw new ArgumentException("event option is unavailable");
        RunManager.Instance.EventSynchronizer.ChooseLocalOption(index);
        AwaitRoomTask(RunManager.Instance.EventSynchronizer.AwaitPendingOptionTasks());
        // EventOption.Chosen runs under TaskHelper.RunSafely: an option that throws is logged, and the event stays on
        // the same page as if nothing was chosen. Say so instead of handing the caller the same options again.
        if (_stage == "event" && room.LocalMutableEvent.CurrentOptions.SequenceEqual(options) && CombatSession.HasGameErrors)
            throw new InvalidOperationException("event option failed inside the game: " + CombatSession.DrainGameErrors().FirstOrDefault());
    }

    void ChooseRest(JsonObject action)
    {
        if (_run.CurrentRoom is not RestSiteRoom room) throw new InvalidOperationException("not at a rest site");
        int index = (int?)action["index"] ?? throw new ArgumentException("rest option needs index");
        if (index < 0 || index >= room.Options.Count || !room.Options[index].IsEnabled)
            throw new ArgumentException("rest option is unavailable");
        AwaitRoomTask(RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(index));
    }

    // A room task paused on a caller decision carries on; it may stop again at the next choice or rewards set.
    void ResumeRoomTask()
    {
        Task task = _pendingRoomTask ?? throw new InvalidOperationException("no room task is waiting");
        _pendingRoomTask = null;
        AwaitRoomTask(task);
    }

    void AwaitRoomTask(Task task)
    {
        Require(WaitFor(() => task.IsCompleted || _combat.HasPendingRunChoice || _pendingRewards != null
            || BundleSelector.HasPending), "room decision boundary");
        if (BundleSelector.HasPending)
        {
            _pendingRoomTask = task;
            _stage = "awaiting_bundle";
            return;
        }
        if (_pendingRewards != null)
        {
            _combat.EnableRewardSelector();
            _pendingRoomTask = task;
            _returnAfterRewards = "classify";
            _stage = "awaiting_rewards";
            return;
        }
        if (_combat.HasPendingRunChoice)
        {
            _pendingRoomTask = task;
            _stage = "awaiting_room_choice";
            return;
        }
        task.GetAwaiter().GetResult();
        ClassifyRoom();
    }

    void Buy(JsonObject action)
    {
        if (_run.CurrentRoom is not MerchantRoom room) throw new InvalidOperationException("not in a shop");
        int index = (int?)action["index"] ?? throw new ArgumentException("buy needs index");
        MerchantInventory inventory = room.GetLocalInventory();
        var entries = inventory.AllEntries.ToList();
        if (index < 0 || index >= entries.Count || !entries[index].IsStocked || !entries[index].EnoughGold)
            throw new ArgumentException("shop entry is unavailable");
        if (entries[index] is MerchantCardRemovalEntry removal)
        {
            AwaitRoomTask(removal.OnTryPurchaseWrapper(inventory));
            return;
        }
        bool bought = false;
        Await(Purchase(), "native shop purchase");
        async Task Purchase() => bought = await entries[index].OnTryPurchaseWrapper(inventory);
        if (!bought) throw new InvalidOperationException("game refused shop purchase");
    }

    void OpenChest()
    {
        if (_run.CurrentRoom is not TreasureRoom room) throw new InvalidOperationException("not in a treasure room");
        Await(room.DoNormalRewards(), "normal treasure rewards");
        // Extra treasure rewards are offered by the room's native method. If a relic or hook creates a set, the
        // same reward selector used after combat holds it at a caller decision boundary.
        _combat.EnableRewardSelector();
        Task extras = room.DoExtraRewardsIfNeeded();
        if (_pendingRewards != null) { _pendingRoomTask = extras; _returnAfterRewards = "treasure_relic"; _stage = "awaiting_rewards"; }
        else { Await(extras, "extra treasure rewards"); _combat.DisableRewardSelector(); _stage = "treasure_relic"; }
    }

    void PickTreasureRelic(JsonObject action)
    {
        int? index = (int?)action["index"];
        var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
        if (index is int i && (sync.CurrentRelics == null || i < 0 || i >= sync.CurrentRelics.Count))
            throw new ArgumentException("treasure relic index is unavailable");
        // The native pick action resolves the vote, but the shipped UI's RelicsAwarded handler grants the relic.
        // Capture that event and perform the same grant once the pick action has finished in this headless worker.
        List<RelicPickingResult>? awarded = null;
        void Capture(List<RelicPickingResult> results) => awarded = results;
        sync.RelicsAwarded += Capture;
        try
        {
            sync.PickRelicLocally(index);
            Await(RunManager.Instance.ActionExecutor.FinishedExecutingActions(), "treasure relic pick");
        }
        finally { sync.RelicsAwarded -= Capture; }
        if (index != null && awarded == null)
            throw new InvalidOperationException("treasure pick finished without a relic award event");
        foreach (RelicPickingResult result in awarded ?? new List<RelicPickingResult>())
            if (result.relic != null && result.player != null)
                Await(RelicCmd.Obtain(result.relic.ToMutable(), result.player), "treasure relic grant");
        Await(RunManager.Instance.ActionExecutor.FinishedExecutingActions(), "treasure relic grant actions");
        _stage = "awaiting_map";
    }

    void OfferNativeRewards()
    {
        if (_run.CurrentRoom is not CombatRoom room || !room.IsPreFinished)
            throw new InvalidOperationException("combat ended without a pre-finished native room");
        if (!room.Encounter.ShouldGiveRewards) { _stage = "awaiting_proceed"; return; }
        _combat.EnableRewardSelector();
        Await(room.OfferRoomEndRewards(), "native combat rewards");
        Require(WaitFor(() => _pendingRewards != null), "rewards offer");
        _stage = "awaiting_rewards";
    }

    void TakeReward(JsonObject action)
    {
        OfferedSet offered = _sets.LastOrDefault() ?? throw new InvalidOperationException("no rewards set");
        RewardsSet set = offered.Set;
        int index = (int?)action["index"] ?? throw new ArgumentException("reward action needs index");
        if (index < 0 || index >= set.Rewards.Count || set.Rewards[index].SuccessfullySelected)
            throw new ArgumentException($"reward index {index} is not available");
        Reward reward = set.Rewards[index];
        if (reward is PotionReward && _me.PotionSlots.All(p => p != null))
            throw new ArgumentException("potion slots are full");
        // A card reward consults CardSelectCmd's global selector (GetSelectedCardReward). Everything else a reward can
        // open (a removal, a relic's deck choice) takes the local choice path, which reserves a choice id, so the
        // global selector is on only while a card reward is being taken.
        if (reward is CardReward cardReward)
        {
            if ((string?)action["alternative"] is string alternative)
            {
                if (!CardAlternatives(cardReward).Contains(alternative)) throw new ArgumentException($"card reward has no {alternative} option");
                _combat.SetRewardAlternative(alternative);
                offered.AlternativeTaken = true;
            }
            else
            {
                int card = (int?)action["card"] ?? throw new ArgumentException("card reward needs a card index or an alternative");
                if (card < 0 || card >= cardReward.Cards.Count()) throw new ArgumentException("card reward index out of range");
                _combat.SetRewardCardPick(card);
            }
            _combat.EnableRewardSelector();
        }
        else _combat.DisableRewardSelector();
        offered.Selecting = RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(reward);
        SettleRewards();
    }

    // Run reward selections until the caller has something to decide: a card choice a reward opened, a nested set,
    // the same set with rewards left, or, once every set is finished, what the offering room does next.
    void SettleRewards()
    {
        while (_sets.Count > 0)
        {
            OfferedSet top = _sets[^1];
            Require(WaitFor(() => top.Selecting is null or { IsCompleted: true } || _combat.HasPendingRunChoice || _sets[^1] != top),
                "reward selection boundary");
            if (_combat.HasPendingRunChoice) { _stage = "awaiting_room_choice"; return; }
            if (_sets[^1] != top) { _stage = "awaiting_rewards"; return; }
            if (top.Selecting != null)
            {
                bool selected = top.Selecting.GetAwaiter().GetResult();
                top.Selecting = null;
                _combat.DisableRewardSelector();
                // A reroll ends with the screen closed and the reward still offered; anything else refused is an error.
                if (!selected && !top.AlternativeTaken) throw new InvalidOperationException("game refused reward selection");
                top.AlternativeTaken = false;
            }
            if (!RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(top.Set)) { _stage = "awaiting_rewards"; return; }
            FinishSet();
        }
        AfterRewards();
    }

    void FinishSet()
    {
        OfferedSet top = _sets[^1];
        _sets.RemoveAt(_sets.Count - 1);
        top.Done.SetResult();
    }

    void AfterRewards()
    {
        if (_pendingRoomTask == null) { _stage = "awaiting_proceed"; return; }
        Task task = _pendingRoomTask;
        _pendingRoomTask = null;
        string? after = _returnAfterRewards;
        _returnAfterRewards = null;
        _combat.DisableRewardSelector();
        if (after == "treasure_relic") { Await(task, "extra treasure rewards"); _stage = "treasure_relic"; }
        else AwaitRoomTask(task);
    }

    sealed class OfferedSet(RewardsSet set)
    {
        public RewardsSet Set { get; } = set;
        public TaskCompletionSource Done { get; } = new();
        public Task<bool>? Selecting { get; set; }
        public bool AlternativeTaken { get; set; }
    }

    // The card reward screen's extra options besides Skip, which skip_rewards covers: REROLL, Pael's Wing's SACRIFICE.
    static List<string> CardAlternatives(CardReward reward) =>
        CardRewardAlternative.Generate(reward).Select(a => a.OptionId).Where(id => id != "Skip").ToList();

    public object Report()
    {
        object? combat = _lastCombat;
        if (_stage == "combat" && combat == null) combat = _combat.Report("observe");
        _lastCombat = null;
        RewardsSet? rewards = _pendingRewards;
        return new
        {
            ok = true,
            boundary = _stage,
            // The run ended in the Architect's room: TheArchitect's option called WinRun (which then ends every player).
            victory = _run.IsGameOver && _run.CurrentRoom?.IsVictoryRoom == true,
            decision = _decision,
            run_identity = _identity,
            run_state_identity = RuntimeHelpers.GetHashCode(_run),
            game_version = RunManager.Instance.NetService.LocalVersion.version,
            model_id_hash = RunManager.Instance.NetService.LocalVersion.idDatabaseHash,
            obs = new
            {
                seed = _run.Rng.Seed, act = _run.CurrentActIndex, floor = _run.ActFloor,
                room = _run.CurrentRoom?.GetType().Name,
                coord = _run.CurrentMapCoord is MapCoord c ? new { col = (int)c.col, row = (int)c.row } : null,
                hp = _me.Creature.CurrentHp, max_hp = _me.Creature.MaxHp, gold = _me.Gold,
                // What the map shows next from here: the point types one row on.
                next_point_types = _run.Map == null ? null : LegalMapPoints().Select(p => p.PointType.ToString()).Distinct().ToList(),
                deck = _me.Deck.Cards.Select(c => c.Id.ToString()).ToList(),
                relics = _me.Relics.Select(r => r.ToSerializable()).ToList(),
                potions = _me.PotionSlots.Select((p, i) => p?.ToSerializable(i)).Where(p => p != null).ToList(),
                player_rng = _me.PlayerRng.ToSerializable(),
                rng = _run.Rng.ToSerializable(),
            },
            legal = _stage switch
            {
                "awaiting_map" => LegalMapPoints().Select(p => (object)new { type = "map", col = (int)p.coord.col,
                    row = (int)p.coord.row, point_type = p.PointType.ToString() }).ToList(),
                "awaiting_rewards" => RewardActions(rewards!),
                "awaiting_proceed" => new List<object> { new { type = "proceed" } },
                "event" => _run.CurrentRoom is EventRoom er ? er.LocalMutableEvent.CurrentOptions
                    .Select((o, i) => (object)new { type = "event_option", index = i, key = o.TextKey,
                        locked = o.IsLocked, proceed = o.IsProceed }).Where((_, i) => !er.LocalMutableEvent.CurrentOptions[i].IsLocked).ToList()
                    : new List<object>(),
                "rest" => _run.CurrentRoom is RestSiteRoom rr ? rr.Options
                    .Select((o, i) => (object)new { type = "rest_option", index = i, id = o.OptionId })
                    .Where((_, i) => rr.Options[i].IsEnabled).ToList() : new List<object>(),
                "awaiting_room_choice" => _combat.RunChoiceLegal,
                "awaiting_bundle" => BundleSelector.Legal,
                "shop" => ShopActions(),
                "treasure" => new List<object> { new { type = "open_chest" } },
                "treasure_relic" => TreasureRelicActions(),
                _ => new List<object>(),
            },
            rewards = rewards?.Rewards.Select((r, i) => new { index = i, type = r.GetType().Name,
                selected = r.SuccessfullySelected,
                cards = r is CardReward cr ? cr.Cards.Select(c => c.Id.ToString()).ToList() : null,
                alternatives = r is CardReward cra ? CardAlternatives(cra) : null,
                id = r switch
                {
                    RelicReward relic => relic.Relic?.Id.ToString(),
                    PotionReward potion => potion.Potion?.Id.ToString(),
                    _ => null,
                },
                gold = r is GoldReward gr ? (int?)gr.Amount : null }).ToList(),
            shop = _run.CurrentRoom is MerchantRoom merchant ? merchant.GetLocalInventory().AllEntries
                .Select((entry, i) => new { index = i, type = entry.GetType().Name,
                    id = entry switch
                    {
                        MerchantCardEntry card => card.CreationResult?.Card.Id.ToString(),
                        MerchantRelicEntry relic => relic.Model?.Id.ToString(),
                        MerchantPotionEntry potion => potion.Model?.Id.ToString(),
                        _ => null,
                    },
                    cost = entry.Cost, stocked = entry.IsStocked, affordable = entry.EnoughGold }).ToList() : null,
            combat,
            choice = _stage == "awaiting_room_choice" ? _combat.RunChoice : null,
            bundle_choice = _stage == "awaiting_bundle" ? BundleSelector.Describe : null,
            game_errors = CombatSession.DrainGameErrors(),
        };
    }

    List<object> ShopActions()
    {
        var actions = new List<object>();
        if (_run.CurrentRoom is MerchantRoom room)
        {
            foreach (var (entry, index) in room.GetLocalInventory().AllEntries.Select((entry, index) => (entry, index)))
                if (entry.IsStocked && entry.EnoughGold)
                    actions.Add(new { type = "buy", index, item_type = entry.GetType().Name, cost = entry.Cost });
        }
        actions.Add(new { type = "leave_shop" });
        return actions;
    }

    List<object> TreasureRelicActions()
    {
        var relics = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics;
        var actions = (relics ?? Array.Empty<RelicModel>()).Select((r, i) =>
            (object)new { type = "pick_relic", index = (int?)i, id = r.Id.ToString() }).ToList();
        actions.Add(new { type = "pick_relic", index = (int?)null });
        return actions;
    }

    List<object> RewardActions(RewardsSet set)
    {
        var actions = new List<object>();
        foreach (var (reward, index) in set.Rewards.Select((reward, index) => (reward, index)))
        {
            if (reward.SuccessfullySelected) continue;
            if (reward is PotionReward && _me.PotionSlots.All(p => p != null)) continue;
            if (reward is CardReward card)
            {
                actions.AddRange(Enumerable.Range(0, card.Cards.Count()).Select(i =>
                    (object)new { type = "take_reward", index, card = i }));
                actions.AddRange(CardAlternatives(card).Select(id => (object)new { type = "take_reward", index, alternative = id }));
            }
            else actions.Add(new { type = "take_reward", index });
        }
        if (!set.DisallowSkipping) actions.Add(new { type = "skip_rewards" });
        return actions;
    }
}
