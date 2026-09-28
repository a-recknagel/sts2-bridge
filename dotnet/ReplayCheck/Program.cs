// ReplayCheck: read a recorded latest.mcr with the game's own PacketReader, then replay it on
// the headless substrate (GodotStubs + the IL-patched sts2.dll + HeadlessInit's Harmony patches), mirroring
// NMultiplayerTest.RunReplay step for step. Every checksum the game generates is compared by id
// against the recording; the first mismatch dumps both NetFullCombatStates.
//
// usage: ReplayCheck <latest.mcr> <outDir> [--inspect-only] [--strict-steps] [--drop-event N] [--no-restore-checksum]
//   --inspect-only         read the tape and write mcr_inspect.json / mcr_initial_run.json, no replay
//   --strict-steps         after every event, wait for the step boundary (resolved + next input, or terminal)
//   --drop-event N         negative control: skip recorded event N
//   --no-restore-checksum  leave TestMode's checksum gap in place (reproduces the 1/49 first attempt)
// env: STS2_LIB overrides the lib/ directory baked in at build time.
// Run it from a scratch working directory: user:// paths resolve relative to cwd under GodotStubs.
static class Program
{
    static int Main(string[] args)
    {
        Sts2Resolver.Install(typeof(Program).Assembly);
        return Harness.Run(args);
    }
}

// Separate type so no sts2 type is JIT-resolved before the assembly resolver above is installed.
static class Harness
{
    public static int Run(string[] args) => new ReplayRun(
        mcrPath: args[0],
        outDir: args[1],
        inspectOnly: args.Contains("--inspect-only"),
        restorePostActionChecksum: !args.Contains("--no-restore-checksum"),
        strictSteps: args.Contains("--strict-steps"),
        dropEvent: args.SkipWhile(a => a != "--drop-event").Skip(1).Select(int.Parse).DefaultIfEmpty(-1).First()).Execute();
}
