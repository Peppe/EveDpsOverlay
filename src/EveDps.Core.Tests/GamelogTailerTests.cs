using System.Text;
using EveDps.Core;

namespace EveDps.Core.Tests;

/// <summary>Shared polling harness for tailer integration tests.</summary>
public sealed class TailerHarness : IDisposable
{
    public string Dir { get; }

    public GamelogTailer Tailer { get; }

    private readonly object _gate = new();
    private readonly List<TailedLine> _lines = [];
    private readonly List<string> _followed = [];

    public IList<TailedLine> Lines
    {
        get { lock (_gate) return _lines.ToArray(); }
    }

    public IList<string> Followed
    {
        get { lock (_gate) return _followed.ToArray(); }
    }

    public TailerHarness(bool startAtEnd = false, bool directoryExists = true)
    {
        Dir = Path.Combine(Path.GetTempPath(), "evetail-" + Guid.NewGuid().ToString("N")[..8]);
        if (directoryExists) Directory.CreateDirectory(Dir);
        Tailer = new GamelogTailer(Dir, new GamelogTailerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(30),
            StartAtEnd = startAtEnd,
        });
        Tailer.Line += l => { lock (_gate) _lines.Add(l); };
        Tailer.Following += f => { lock (_gate) _followed.Add(f); };
        Tailer.PollError += e => { lock (_gate) _lines.Add(new TailedLine("<error>", e.Message)); };
    }

    public string WriteFile(string name, string content)
    {
        var path = Path.Combine(Dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void AppendTo(string name, string content) =>
        File.AppendAllText(Path.Combine(Dir, name), content);

    /// <summary>Waits until the predicate holds, then fails the test on timeout.</summary>
    public async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(condition(), "condition not met within timeout");
    }

    public void Dispose()
    {
        Tailer.Dispose();
        try { Directory.Delete(Dir, recursive: true); }
        catch { /* best effort */ }
    }
}

public class GamelogTailerTests
{
    private static readonly TimeSpan AlmostForever = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task Streams_backlog_and_appended_lines()
    {
        using var h = new TailerHarness(startAtEnd: false);
        h.WriteFile("20261101_171924.txt", "header A\r\ncombat 1\r\n");
        h.Tailer.Start();

        await h.WaitUntil(() => h.Lines.Count >= 2, AlmostForever);
        h.AppendTo("20261101_171924.txt", "combat 2\r\n");
        await h.WaitUntil(() => h.Lines.Count >= 3, AlmostForever);

        var texts = h.Lines.Select(l => l.Text).ToList();
        Assert.Equal(3, texts.Count);
        Assert.Contains("combat 2", texts);
        Assert.All(h.Lines, l => Assert.DoesNotContain('\n', l.Text));
        Assert.All(h.Lines, l => Assert.False(string.IsNullOrWhiteSpace(l.Text)));
    }

    [Fact]
    public async Task Restart_over_existing_file_does_not_replay_it()
    {
        using var h = new TailerHarness(startAtEnd: true);
        h.WriteFile("20261101_171924.txt", "already seen\r\nlines\r\n");
        h.Tailer.Start();

        // Wait a few poll cycles; nothing may be emitted for existing content.
        await Task.Delay(150);
        Assert.Empty(h.Lines);

        h.AppendTo("20261101_171924.txt", "new line after restart\r\n");
        await h.WaitUntil(() => h.Lines.Count >= 1, AlmostForever);
        Assert.Single(h.Lines);
        Assert.Equal("new line after restart", h.Lines[0].Text);
    }

    [Fact]
    public async Task Adopts_a_newly_created_growing_session_file()
    {
        using var h = new TailerHarness(startAtEnd: false);
        h.WriteFile("a.txt", "A one\r\n");
        h.Tailer.Start();
        await h.WaitUntil(() => h.Lines.Count >= 1, AlmostForever);

        // A new session file appears with an initial header block. It must be
        // recorded first, then adopted only when it grows past that size.
        h.WriteFile("b.txt", "stub header\r\n");
        await Task.Delay(100); // let at least one poll record b at its stub size
        h.AppendTo("b.txt", "B session starts\r\nB first hit\r\n");
        await h.WaitUntil(() => h.Lines.Count >= 4, AlmostForever);

        Assert.Equal(4, h.Lines.Count);
        var texts = h.Lines.Select(l => l.Text).ToList();
        Assert.Contains("stub header", texts);
        Assert.Contains("B first hit", texts);
        // No duplicate replay of the A backlog after the switch:
        Assert.Equal(1, texts.Count(t => t == "A one"));
        // Both files were followed, newer one adopted:
        Assert.Contains(h.Followed, p => p != null && p.EndsWith("b.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Header_only_stub_never_becomes_the_followed_file()
    {
        using var h = new TailerHarness(startAtEnd: false);
        var aPath = h.WriteFile("a.txt", "A one\r\n");
        h.Tailer.Start();
        await h.WaitUntil(() => h.Lines.Count >= 1, AlmostForever);

        // Stub with a FRESHER timestamp, but it never grows afterwards.
        h.WriteFile("stub.txt",
            "------------------------------------------------------------\r\n" +
            "  Gamelog\r\n" +
            "  Session Started: 2026.11.07 13:27:58\r\n" +
            "------------------------------------------------------------\r\n");
        await Task.Delay(150);

        h.AppendTo(aPath, "A two\r\n");
        await h.WaitUntil(() => h.Lines.Count >= 2, AlmostForever);

        var stubLines = h.Lines.Where(l => l.FilePath.Contains("stub", StringComparison.Ordinal));
        Assert.Empty(stubLines);
        var texts = h.Lines.Select(l => l.Text).ToList();
        Assert.Contains("A two", texts);
    }

    [Fact]
    public async Task Buffers_partial_lines_across_polls()
    {
        using var h = new TailerHarness(startAtEnd: false);
        var path = h.WriteFile("a.txt", string.Empty);
        h.Tailer.Start();

        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Write(Encoding.UTF8.GetBytes("hello wor"));
        }
        await Task.Delay(100); // unflushed partial sits across polls

        h.AppendTo("a.txt", "ld\r\nnext\r\n");
        await h.WaitUntil(() => h.Lines.Count >= 2, AlmostForever);

        Assert.Equal(2, h.Lines.Count);
        var texts = h.Lines.Select(l => l.Text).ToList();
        Assert.Contains("hello world", texts);
        Assert.Contains("next", texts);
    }

    [Fact]
    public async Task Truncation_resumes_from_start_without_replaying_stale_lines()
    {
        using var h = new TailerHarness(startAtEnd: false);
        var path = h.WriteFile("a.txt", "one\r\ntwo\r\n");
        h.Tailer.Start();
        await h.WaitUntil(() => h.Lines.Count >= 2, AlmostForever);

        File.WriteAllText(path, "fresh\r\n");
        await h.WaitUntil(() => h.Lines.Count >= 3 && h.Lines[^1].Text == "fresh", AlmostForever);

        var texts = h.Lines.Select(l => l.Text).ToList();
        Assert.Equal(1, texts.Count(t => t == "one"));
        Assert.Equal(1, texts.Count(t => t == "two"));
        Assert.Contains("fresh", texts);
    }

    [Fact]
    public async Task Starts_idle_when_directory_missing_then_follows_created_file()
    {
        using var h = new TailerHarness(startAtEnd: false, directoryExists: false);
        h.Tailer.Start();
        await Task.Delay(100);
        Assert.Empty(h.Lines);

        Directory.CreateDirectory(h.Dir);
        File.WriteAllText(Path.Combine(h.Dir, "a.txt"), "first\r\n");
        await h.WaitUntil(() => h.Lines.Count >= 1, AlmostForever);

        Assert.Contains("first", h.Lines.Select(l => l.Text));
    }
}
