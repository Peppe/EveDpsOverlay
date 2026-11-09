using System.Text;

namespace EveDps.Core;

/// <summary>A complete line read from a followed gamelog file.</summary>
public sealed record TailedLine(string FilePath, string Text);

public sealed class GamelogTailerOptions
{
    /// <summary>How often file lengths are polled.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// When true (default), an existing file is followed from its current end
    /// so a restart never re-emits old lines. When false, the full backlog is
    /// streamed on start.
    /// </summary>
    public bool StartAtEnd { get; set; } = true;
}

/// <summary>
/// Follows the newest EVE gamelog file in a directory. See
/// docs/spec/phase-1-tail-parse.md for the rules this implements:
/// polling instead of FileSystemWatcher, shared-read access, byte offsets
/// with partial-line buffering, and adoption of a newly created session file
/// only once it actually grows (header-only stub files are never followed).
/// </summary>
public sealed class GamelogTailer : IDisposable
{
    private readonly DirectoryInfo _directory;
    private readonly GamelogTailerOptions _options;
    private readonly List<byte> _partial = [];
    private readonly Dictionary<string, long> _knownLengths = new(StringComparer.OrdinalIgnoreCase);
    private readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private string? _currentPath;
    private long _offset;
    private Task? _loop;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public GamelogTailer(string logDirectory, GamelogTailerOptions? options = null)
    {
        _directory = new DirectoryInfo(logDirectory);
        _options = options ?? new GamelogTailerOptions();
    }

    /// <summary>Each complete line of the followed file, on the tailer thread.</summary>
    public event Action<TailedLine>? Line;

    /// <summary>The followed file changed. Argument is the full path.</summary>
    public event Action<string>? Following;

    /// <summary>A recoverable poll/read failure.</summary>
    public event Action<Exception>? PollError;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Step();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PollError?.Invoke(ex);
            }

            try
            {
                await Task.Delay(_options.PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Step()
    {
        var candidate = Directory.Exists(_directory.FullName)
            ? _directory.EnumerateFiles("*.txt", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        if (_currentPath is null)
        {
            if (candidate is null) return;

            // First adoption: the newest file, from its end so a restart does
            // not replay history that was already observed.
            _currentPath = candidate.FullName;
            _knownLengths[_currentPath] = candidate.Length;
            _offset = _options.StartAtEnd ? candidate.Length : 0;
            Following?.Invoke(_currentPath);
        }
        else if (candidate is not null &&
                 !string.Equals(_currentPath, candidate.FullName, StringComparison.OrdinalIgnoreCase))
        {
            if (!_knownLengths.TryGetValue(candidate.FullName, out var seen))
            {
                // First sighting of a newer file: record it, do not adopt yet.
                // A one-shot stub file never grows and is never followed.
                _knownLengths[candidate.FullName] = candidate.Length;
            }
            else if (candidate.Length > seen)
            {
                // It grew after we first saw it: a genuine new session file.
                // Adopt from the start — its earlier bytes were never read.
                _currentPath = candidate.FullName;
                _partial.Clear();
                _offset = 0;
                _knownLengths[_currentPath] = candidate.Length;
                Following?.Invoke(_currentPath);
            }
            // else: newest file exists but is not growing (e.g. a stub at
            // login). Keep following the current file.
        }

        if (_currentPath is not null)
            ReadCurrent(_currentPath);
    }

    private void ReadCurrent(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        if (length < _offset)
        {
            // Rotated/truncated: restart the file cleanly.
            _offset = 0;
            _partial.Clear();
        }

        if (length > _offset)
        {
            stream.Seek(_offset, SeekOrigin.Begin);
            var buffer = new byte[length - _offset];
            var read = stream.Read(buffer, 0, buffer.Length);
            _offset += read;
            _partial.AddRange(new ArraySegment<byte>(buffer, 0, read));
        }

        // Emit only complete lines; keep the partial remainder for next poll.
        while (true)
        {
            var newline = _partial.IndexOf((byte)'\n');
            if (newline < 0) break;
            var text = _utf8.GetString([.. _partial.Take(newline)]).TrimEnd('\r');
            _partial.RemoveRange(0, newline + 1);
            Line?.Invoke(new TailedLine(path, text));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Shutdown path: suppress.
        }
        _cts?.Dispose();
    }
}
