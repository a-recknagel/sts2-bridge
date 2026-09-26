# Trying buffs and other sandbox changes

For an easier training fight, add a power after loading the combat. The worker has no buff command;
add a method to `CombatSession` and expose it in `Program.cs` if your experiment needs one.
For example, with `using MegaCrit.Sts2.Core.Models.Powers;`:

```csharp
public object GivePlating(int amount)
{
    Await(PowerCmd.Apply<PlatingPower>(new ThrowingPlayerChoiceContext(),
        _me.Creature, amount, null, null, false), "give_plating");
    AwaitBoundary();
    return Report("observe");
}
```

Call it at a settled player-input boundary. Use the game's command helpers so power hooks run,
wait for the result, then return the usual observation. Pick whatever command name and arguments
suit the experiment. Once you change the state, the original recording's checksums will differ.

For reference, the game's AutoSlay bot starts with 999 Plating and Regen, then adds 200 Strength
per turn from turn 3 (`CombatRoomHandler` / `AutoSlayConfig`, v0.111.0).
Plating alone won't save you from the Insatiable: Sandpit kills through block.
Frantic Escape buys more turns; Strength helps finish before the timer does.
