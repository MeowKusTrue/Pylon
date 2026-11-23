namespace Pylon;

/// <summary>Bytes every download has moved so far: a progress line reads its speed from here.</summary>
public static class Transfer
{
    static long _bytes;
    public static void Add(long n) => Interlocked.Add(ref _bytes, n);
    public static long Bytes => Interlocked.Read(ref _bytes);

    public static string Size(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} {L.T("MB")}" : $"{bytes / 1024.0:0} {L.T("KB")}";

    public static string Left(double seconds) => L.F("about {0} left", seconds < 60 ? L.F("{0} s", (int)seconds + 1) : L.F("{0} min", (int)(seconds / 60) + 1));
}

/// <summary>
/// Speed and time left for one long operation, from the last 10 s of its progress: "12.3 MB/s · about 20 s left".
/// The speed shows only while bytes move (a conversion has none, a download does).
/// </summary>
public sealed class Meter
{
    readonly Queue<(long Ms, long Bytes, double P)> _samples = new();
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public string Line(double p)
    {
        var now = _clock.ElapsedMilliseconds;
        _samples.Enqueue((now, Transfer.Bytes, p));
        while (_samples.Count > 2 && now - _samples.Peek().Ms > 10_000) _samples.Dequeue();
        var first = _samples.Peek();
        var seconds = (now - first.Ms) / 1000.0;
        if (seconds < 1) return "";
        var parts = new List<string>();
        var speed = (Transfer.Bytes - first.Bytes) / seconds;
        if (speed > 1024) parts.Add($"{Transfer.Size((long)speed)}/{L.T("s")}");
        var rate = (p - first.P) / seconds;
        if (rate > 0 && p < 1) parts.Add(Transfer.Left((1 - p) / rate));
        return string.Join(" · ", parts);
    }
}
