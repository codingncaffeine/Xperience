using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Xperience.Core.Diagnostics;

/// <summary>Where the record of a session goes. Writing never throws and never waits for the disk.</summary>
public interface ILog
{
    /// <summary>Adds one line: what part of the program it is about, and what happened.</summary>
    void Write(string area, string text);
}

/// <summary>A record nobody keeps, for tests and for when the file cannot be written.</summary>
public sealed class NoLog : ILog
{
    public static readonly NoLog Instance = new();

    public void Write(string area, string text)
    {
    }
}

/// <summary>
/// The record of what the tablet reported and what was done with it: one line per event, in a file in the
/// account's state folder. It is there so that a session at the tablet can be checked afterwards from what was
/// measured instead of from memory. Runs are appended one after another; when the file has grown large it is
/// set aside as the previous one and a new one begun. Lines are handed to a thread of their own, so nothing
/// that reports to the log ever waits for the disk.
/// </summary>
public sealed class SessionLog : ILog, IDisposable
{
    /// <summary>A file larger than this is set aside when the next run starts.</summary>
    public const long RotateAt = 4 * 1024 * 1024;

    /// <summary>One run writes no more than this; past it the record stops, so a stuck key cannot fill a disk.</summary>
    public const long LargestRun = 16 * 1024 * 1024;

    readonly BlockingCollection<(string? Line, ManualResetEventSlim? Reached)> _lines = new(8192);
    readonly StreamWriter _file;
    readonly Thread _writer;
    readonly Func<DateTimeOffset> _clock;
    long _written;
    int _dropped;
    bool _full;

    SessionLog(string path, StreamWriter file, Func<DateTimeOffset> clock)
    {
        Path = path;
        _file = file;
        _clock = clock;
        _writer = new Thread(Run) { IsBackground = true, Name = "session log" };
        _writer.Start();
    }

    public string Path { get; }

    /// <summary>Opens the record in a folder, creating it; null when it cannot be written there.</summary>
    public static SessionLog? Open(string folder, Func<DateTimeOffset>? clock = null)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string path = System.IO.Path.Combine(folder, "xperience.log");
            if (File.Exists(path) && new FileInfo(path).Length > RotateAt)
                File.Move(path, System.IO.Path.Combine(folder, "xperience.previous.log"), overwrite: true);
            var file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            return new SessionLog(path, file, clock ?? (() => DateTimeOffset.Now));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string area, string text)
    {
        // Line breaks inside a message would make one event look like several.
        string line = string.Create(CultureInfo.InvariantCulture, $"{_clock():yyyy-MM-dd HH:mm:ss.fff}  {area,-8} {text.ReplaceLineEndings(" | ")}");
        if (!_lines.IsAddingCompleted && !_lines.TryAdd((line, null)))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>Returns once everything written so far is in the file.</summary>
    public void Settle()
    {
        using var reached = new ManualResetEventSlim();
        try
        {
            _lines.Add((null, reached));
        }
        catch (InvalidOperationException)
        {
            return;
        }
        reached.Wait(TimeSpan.FromSeconds(5));
    }

    void Run()
    {
        try
        {
            foreach (var (line, reached) in _lines.GetConsumingEnumerable())
            {
                if (line != null && !_full)
                {
                    int dropped = Interlocked.Exchange(ref _dropped, 0);
                    if (dropped > 0)
                        Put($"{_clock():yyyy-MM-dd HH:mm:ss.fff}  log      {dropped} lines were left out: they came faster than the disk took them");
                    Put(line);
                    if (_written > LargestRun)
                    {
                        _full = true;
                        Put($"{_clock():yyyy-MM-dd HH:mm:ss.fff}  log      this run's record is full and stops here");
                    }
                }
                if (reached != null || _lines.Count == 0)
                    _file.Flush();
                reached?.Set();
            }
            _file.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The disk is full or gone: the record ends, the program goes on.
        }
    }

    void Put(string line)
    {
        _file.WriteLine(line);
        _written += line.Length + 1;
    }

    public void Dispose()
    {
        _lines.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        try
        {
            _file.Dispose();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }
}
