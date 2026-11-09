using System.Text;

namespace EveDps.Core;

/// <summary>
/// Replays a captured gamelog file as a paced line feed (phase 9). Lines are
/// emitted on a background task, paced by each event's in-log timestamp with
/// a speed multiplier; idle gaps longer than MaxIdleGap of log time hold only
/// for that cap. Hosts hook Line exactly like the GamelogTailer's and read
/// CurrentVirtualTime as the clock for aggregator Take — wall-clock time is
/// wrong for old sessions and would push every event outside the windows.
/// </summary>
public sealed class ReplayFeed : IDisposable
{
    private readonly string _path;
    private readonly double _speed;
    private readonly TimeSpan _maxIdleGap;
    private readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private long _virtualTicks;
    private volatile bool _timeKnown;
    private Task? _loop;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>Each replayed line, on the replay task thread.</summary>
    public event Action<TailedLine>? Line;

    /// <summary>Replay reached the end of the file, or was cancelled.</summary>
    public event Action? Finished;

    public ReplayFeed(string filePath, double speed = 60.0, TimeSpan? maxIdleGap = null)
    {
        _path = filePath;
        _speed = speed > 0 ? speed : 60.0;
        _maxIdleGap = maxIdleGap ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>The replayed file's path (for host status displays).</summary>
    public string FilePath => _path;

    /// <summary>The speed multiplier in effect.</summary>
    public double Speed => _speed;

    /// <summary>
    /// The replayed stream's current log timestamp (UTC). Safe to read from
    /// any thread; monotonic within one replay. Until <see cref="TimeKnown"/>
    /// turns true the value is uninitialized (epoch ticks) — hosts must not
    /// use it for window math (epochs minus a window overflows DateTime).
    /// </summary>
    public DateTimeOffset CurrentVirtualTime =>
        new(Interlocked.Read(ref _virtualTicks), TimeSpan.Zero);

    /// <summary>
    /// True once the first timestamped line has been processed; false until
    /// then (and for files with no timestamped lines at all). Hosts gate
    /// aggregation on this — see MainWindow.UpdateHud.
    /// </summary>
    public bool TimeKnown => _timeKnown;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts!.Token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        var loop = _loop;
        if (loop is not null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* cancel path: swallow */ }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(_path, _utf8);
            }
            catch (Exception ex) when (ex is IOException or FileNotFoundException or UnauthorizedAccessException)
            {
                // File unreadable: hosts handle via Finished (console exits 2
                // if announced; HUD falls back to live). Report emptily.
                return;
            }

            DateTimeOffset? lastSeen = null;
            for (var i = 0; i < lines.Length && !ct.IsCancellationRequested; i++)
            {
                var line = lines[i];
                var stamp = LogParser.ParseLine(line)?.Time;
                if (stamp is not null)
                {
                    if (lastSeen is not null)
                    {
                        // Pace by the real gap, but never hold longer than the
                        // idle cap; the avoidable hours just skip.
                        var gap = stamp.Value - lastSeen.Value;
                        if (gap > TimeSpan.Zero)
                        {
                            var capped = gap > _maxIdleGap ? _maxIdleGap : gap;
                            await SafeDelay(TimeSpan.FromSeconds(capped.TotalSeconds / _speed), ct);
                        }
                    }
                    Interlocked.Exchange(ref _virtualTicks, stamp.Value.UtcTicks);
                    lastSeen = stamp;
                    _timeKnown = true;
                }

                Line?.Invoke(new TailedLine(_path, line));
            }
        }
        finally
        {
            Finished?.Invoke();
        }
    }

    private static async Task SafeDelay(TimeSpan wall, CancellationToken ct)
    {
        if (wall <= TimeSpan.Zero) return;
        try { await Task.Delay(wall, ct); }
        catch (OperationCanceledException) { /* stop requested */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* shutdown path: suppress */ }
        _cts?.Dispose();
    }
}
