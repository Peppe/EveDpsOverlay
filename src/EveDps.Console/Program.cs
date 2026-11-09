using System.Globalization;
using System.Text;
using EveDps.Core;

// ─────────────────────────────────────────────────────────────────────────────
// EveDps.Console — phase 2 CLI. Specs: docs/spec/phase-1-tail-parse.md,
// docs/spec/phase-2-dps.md
//
//   dotnet run --project src/EveDps.Console
//     [--raw]                  print every tailed line before parsing
//     [--dps]                  live rolling-DPS status line (5/15/60 s windows)
//     [--from-start]           stream the followed file's backlog
//     [--replay <file>...]     batch-parse file(s), print events + windows
//     [--replay-live <file>]   replay a fixture as a live session; [--speed N] default ×60
//     [--smoke]                live mode for 2 s, print summary, exit 0
//     [<logdir>]               default: Documents\EVE\logs\Gamelogs
// ─────────────────────────────────────────────────────────────────────────────

var options = args.Where(a => a.StartsWith("--")).ToHashSet();
var replayFiles = new List<string>();
string? replayLiveFile = null;
var replaySpeed = 60.0;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--replay")
        while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            replayFiles.Add(args[++i]);
    else if (args[i] == "--replay-live" && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
        replayLiveFile = args[++i];
    else if (args[i] == "--speed" && i + 1 < args.Length &&
             double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
    {
        replaySpeed = s > 0 ? s : 60.0;
        ++i;
    }
}

var positional = args.Where(a =>
    !a.StartsWith("--") &&
    a != "--replay" &&
    a != "--replay-live" &&
    a != "--speed" &&
    (replayLiveFile is null || !a.Equals(replayLiveFile, StringComparison.OrdinalIgnoreCase)) &&
    (replayFiles.Count == 0 || !replayFiles.Contains(a))).ToList();

var defaultDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    "EVE", "logs", "Gamelogs");
var logDir = positional.FirstOrDefault() ?? defaultDir;

int exitCode;
if (replayFiles.Count > 0)
    exitCode = Replay().GetAwaiter().GetResult();
else if (replayLiveFile is not null)
    exitCode = ReplayLive(replayLiveFile, replaySpeed).GetAwaiter().GetResult();
else if (options.Contains("--smoke"))
    exitCode = Smoke().GetAwaiter().GetResult();
else
    exitCode = Live().GetAwaiter().GetResult();

return exitCode;

async Task<int> ReplayLive(string file, double speed)
{
    if (!File.Exists(file))
    {
        Console.Error.WriteLine($"[error] replay file not found: {file}");
        return 2;
    }
    using var exitSignal = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        exitSignal.Cancel();
    };
    using var feed = new ReplayFeed(file, speed);
    return await RunLiveAsync(exitSignal, smoke: false, feed);
}

async Task<int> Smoke()
{
    if (!Directory.Exists(logDir))
    {
        Console.Error.WriteLine($"[error] log directory not found: {logDir}");
        return 2;
    }
    using var exitSignal = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    return await RunLiveAsync(exitSignal, smoke: true).WaitAsync(TimeSpan.FromSeconds(10));
}

async Task<int> Live()
{
    if (!Directory.Exists(logDir))
    {
        Console.Error.WriteLine($"[error] log directory not found: {logDir}");
        return 2;
    }
    using var exitSignal = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        exitSignal.Cancel();
    };
    return await RunLiveAsync(exitSignal, smoke: false);
}

async Task<int> RunLiveAsync(CancellationTokenSource exitSignal, bool smoke, ReplayFeed? replay = null)
{
    var ct = exitSignal.Token;
    var events = 0;
    long outgoing = 0, incoming = 0, misses = 0;
    var agg = new DpsAggregator(WeaponTypeProfiles.Load(ProfilePath()));
    var aggGate = new object();
    var outGate = new object();
    var statusShown = false;
    var statusLength = 0;

    void ClearStatus()
    {
        if (!statusShown) return;
        Console.Write("\r" + new string(' ', Math.Max(0, statusLength)) + "\r");
        statusShown = false;
        statusLength = 0;
    }

    void OnLine(TailedLine line)
    {
        lock (outGate)
        {
            if (options.Contains("--raw")) Console.WriteLine($"  {line.Text}");
            CombatEvent? ev;
            lock (aggGate) ev = LogParser.ParseCombat(line.Text);
            if (ev is null) return;

            lock (aggGate) agg.Add(ev);
            events++;
            if (ev.IsMiss) misses++;
            else if (ev.Direction == CombatDirection.Outgoing) outgoing += ev.Damage;
            else incoming += ev.Damage;
            PrintEvent(ev);
        }
    }

    Func<DateTimeOffset> clock;
    GamelogTailer? tailer = null;
    if (replay is not null)
    {
        clock = () => replay.CurrentVirtualTime;
        replay.Finished += () =>
        {
            lock (outGate)
            {
                ClearStatus();
                Console.WriteLine(
                    $"\n> replay finished ({replay.CurrentVirtualTime:HH:mm:ss} log time)");
            }
            exitSignal.Cancel();
        };
        replay.Line += OnLine;
        lock (outGate)
            Console.WriteLine($"> replaying {Path.GetFileName(replay.FilePath)} (×{replay.Speed:0.##})");
        replay.Start();
    }
    else
    {
        clock = () => DateTimeOffset.UtcNow;
        tailer = new GamelogTailer(logDir, new GamelogTailerOptions
        {
            StartAtEnd = !options.Contains("--from-start"),
        });
        tailer.Following += f => { lock (outGate) { ClearStatus(); Console.WriteLine($"\n> following {Path.GetFileName(f)}"); } };
        tailer.PollError += e => { lock (outGate) { ClearStatus(); Console.Error.WriteLine($"[warn] {e.Message}"); } };
        tailer.Line += OnLine;
        tailer.Start();
    }

    bool dpsView = options.Contains("--dps");
    Task? renderer = null;
    if (dpsView)
    {
        renderer = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }

                DpsSnapshot snap;
                if (replay is { TimeKnown: false }) continue; // epoch clock race-gated, do not Take yet
                lock (aggGate) snap = agg.Take(clock());
                if (Console.IsOutputRedirected) continue;   // don't spam pipes
                var text = FormatWindows(snap).Substring(2); // status line w/o leading "# "
                lock (outGate)
                {
                    ClearStatus();
                    Console.Write(text);
                    statusShown = true;
                    statusLength = text.Length;
                }
            }
        });
    }

    try
    {
        await Task.Delay(Timeout.Infinite, ct);
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C or smoke timeout.
    }

    if (renderer is not null)
    {
        try { await renderer.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* shutdown race is fine */ }
    }

    DpsSnapshot final;
    lock (aggGate) final = agg.Take(
        replay is { TimeKnown: false } ? DateTimeOffset.UtcNow : clock());

    lock (outGate)
    {
        ClearStatus();
        Console.WriteLine();
        if (dpsView)
        {
            Console.WriteLine(FormatWindows(final));
            Console.WriteLine(FormatTypes(final));
        }
        Summary(events, outgoing, incoming, misses);
    }

    tailer?.Dispose();
    return 0;
}

async Task<int> Replay()
{
    var exit = 0;
    foreach (var file in replayFiles)
    {
        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"[error] not found: {file}");
            exit = 2;
            continue;
        }

        Console.WriteLine($"-- {Path.GetFileName(file)} --");
        var events = 0;
        long outgoing = 0, incoming = 0, misses = 0;
        var agg = new DpsAggregator(WeaponTypeProfiles.Load(ProfilePath()));
        DateTimeOffset last = DateTimeOffset.MinValue;

        foreach (var line in File.ReadLines(file, new UTF8Encoding(false, false)))
        {
            var ev = LogParser.ParseCombat(line);
            if (ev is null) continue;
            events++;
            agg.Add(ev);
            if (ev.Time > last) last = ev.Time;
            PrintEvent(ev);
            if (ev.IsMiss) misses++;
            else if (ev.Direction == CombatDirection.Outgoing) outgoing += ev.Damage;
            else incoming += ev.Damage;
        }

        Console.WriteLine();
        // Reference = last event time: windows describe the session's final burst.
        var takeAt = events > 0 ? last : DateTimeOffset.UtcNow;
        var snap = agg.Take(takeAt);
        Console.WriteLine(FormatWindows(snap));
        Console.WriteLine(FormatTypes(snap));
        Console.WriteLine(FormatAttackers(snap));
        Summary(events, outgoing, incoming, misses);
        Console.WriteLine();
    }
    return exit;
}

static void PrintEvent(CombatEvent ev)
{
    var direction = ev.Direction == CombatDirection.Outgoing ? "OUT" : "IN ";
    var weapon = ev.Weapon is null ? "" : $" via {ev.Weapon}";
    var kind = string.IsNullOrEmpty(ev.HitKind) ? "" : $" - {ev.HitKind}";
    var miss = ev.IsMiss ? "  [MISS]" : "";
    Console.WriteLine($"{ev.Time:HH:mm:ss} {direction} {ev.Damage,6} {ev.OtherParty}{weapon}{kind}{miss}");
}

static string FormatWindows(DpsSnapshot snap)
{
    var windows = string.Join(" | ", snap.Windows.Select(w =>
        $"{w.Seconds:0}s OUT {w.OutgoingDps:0.0} IN {w.IncomingDps:0.0} ({w.Shots})"));
    return $"# {windows} | session {snap.Session.TotalOutgoing}/{snap.Session.TotalIncoming}";
}

static string FormatTypes(DpsSnapshot snap)
{
    // Largest window (60 s by default) unless the live view swapped windows.
    var w = snap.Windows[0];
    var parts = string.Join(" ", w.OutgoingTypes.Select(t => $"{TypeAbbrev(t.Name)} {t.Dps:0.0}"));
    var unmapped = snap.UnmappedWeapons.Count == 0
        ? "none"
        : string.Join(", ", snap.UnmappedWeapons);
    return $"# types({w.Seconds:0}s) {parts} | unmapped: {unmapped}";
}

static string TypeAbbrev(string name) => name switch
{
    DamageTypes.Thermal => "TH",
    DamageTypes.Kinetic => "KIN",
    DamageTypes.Explosive => "EX",
    DamageTypes.Unknown => "UNK",
    _ => name, // "EM" prints as-is
};

static string ProfilePath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "EveDpsOverlay", "type-profiles.json");

/// <summary>
/// Phase 7: per-attacker block for --replay summaries — top attackers by dps
/// at the snapshot's primary window, descending, plus an Others-share line
/// for cross-checks against the window's IncomingDps.
/// </summary>
static string FormatAttackers(DpsSnapshot snap)
{
    var w = snap.Windows[0]; // aggregates sort windows largest-first
    var rows = w.Attackers;
    if (rows.Count == 0) return "# attackers(none)";
    const int MaxRows = 5;
    var lines = new List<string> { $"# attackers({w.Seconds:0}s) " + string.Join(
        " | ", rows.Take(MaxRows).Select(a => $"{a.Name} {a.IncomingDps:0.#}{(a.Misses > 0 ? $"/{a.Misses}m" : "")}")) };
    var othersDps = rows.Skip(MaxRows).Sum(a => a.IncomingDps);
    if (rows.Count > MaxRows)
        lines.Add($"#   others({rows.Count - MaxRows}) {othersDps:0.#} dps | rows sum {rows.Sum(a => a.IncomingDps):0.#} vs window IN {w.IncomingDps:0.#}");
    return string.Join("\n", lines);
}

static void Summary(int events, long outgoing, long incoming, long misses) =>
    Console.WriteLine($"# {events} combat events | OUT {outgoing} dmg | IN {incoming} dmg | {misses} misses");
