using System.Net.Sockets;
using System.Text;

namespace Xperience.App;

/// <summary>What became of a word handed to a copy that might be running.</summary>
public enum Handover
{
    /// <summary>No copy is running.</summary>
    Nobody,
    /// <summary>The running copy took the word; this one can leave.</summary>
    Taken,
    /// <summary>The running copy is another build and is ending, so that this one takes its place.</summary>
    Replacing,
}

/// <summary>
/// Keeps Xperience to one running copy per account. The first copy listens on a socket in the account's
/// runtime folder; a later one hands it a word, such as "show", and leaves. Each says which build it is: a
/// copy of another build steps aside for the newer start, so what is on screen is always the build that was
/// just started.
/// </summary>
public sealed class InstanceLink : IDisposable
{
    readonly Socket _listener;
    readonly string _path;

    InstanceLink(Socket listener, string path)
    {
        _listener = listener;
        _path = path;
    }

    /// <summary>Where the socket lives for this account; null when the account has no runtime folder.</summary>
    public static string? DefaultPath =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run ? Path.Combine(run, "xperience", "instance.sock") : null;

    /// <summary>What tells one build from another: a number the compiler gives each build.</summary>
    public static string ThisBuild { get; } = typeof(InstanceLink).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    /// <summary>Hands a word to the copy already running, and says what it answered.</summary>
    public static Handover Send(string path, string word, string build)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { ReceiveTimeout = 3000, SendTimeout = 3000 };
            socket.Connect(new UnixDomainSocketEndPoint(path));
            socket.Send(Encoding.UTF8.GetBytes($"{word} {build}\n"));
            byte[] answer = new byte[16];
            int read = socket.Receive(answer);
            return Encoding.UTF8.GetString(answer, 0, read).Trim() == "replace" ? Handover.Replacing : Handover.Taken;
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            return Handover.Nobody;
        }
    }

    /// <summary>Waits for a copy that is ending to be gone: until nobody answers on its socket any more.</summary>
    public static bool WaitUntilFree(string path, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(new UnixDomainSocketEndPoint(path));
            }
            catch (Exception e) when (e is SocketException or IOException)
            {
                return true;
            }
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>
    /// Starts listening. <paramref name="onWord"/> gets each word from a copy of this same build, on a pool
    /// thread; <paramref name="onReplaced"/> is called when a copy of another build wants this one's place.
    /// Null when the socket cannot be made, in which case this copy simply runs without one.
    /// </summary>
    public static InstanceLink? Listen(string path, string build, Action<string> onWord, Action onReplaced)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // A socket file with nobody behind it is left over from a copy that did not end cleanly.
            File.Delete(path);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
            var link = new InstanceLink(listener, path);
            _ = Task.Run(() => link.AcceptAsync(build, onWord, onReplaced));
            return link;
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    async Task AcceptAsync(string build, Action<string> onWord, Action onReplaced)
    {
        byte[] buffer = new byte[128];
        while (true)
        {
            Socket caller;
            try
            {
                caller = await _listener.AcceptAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // The listener was closed: this copy is ending.
                return;
            }
            using (caller)
            {
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    int read = await caller.ReceiveAsync(buffer, SocketFlags.None, limit.Token).ConfigureAwait(false);
                    string[] said = Encoding.UTF8.GetString(buffer, 0, read).Trim().Split(' ', 2);
                    if (said[0].Length == 0)
                        continue;
                    bool same = said.Length == 2 && said[1] == build;
                    await caller.SendAsync(Encoding.UTF8.GetBytes(same ? "ok\n" : "replace\n"), SocketFlags.None, limit.Token).ConfigureAwait(false);
                    if (same)
                        onWord(said[0]);
                    else
                        onReplaced();
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException)
                {
                    // A caller that says nothing is dropped; the next one is still served.
                }
            }
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover socket file is cleared by the next copy that starts.
        }
    }
}
