namespace Pylon;

public static class VideoZoom
{
    public static int Percent(string value) => value.EndsWith('%') && int.TryParse(value.AsSpan(0, value.Length - 1), out var percent)
        ? Math.Clamp(percent, 25, 400) : 100;

    public static int Step(int percent, int delta)
    {
        var next = Math.Clamp(percent + Math.Sign(delta) * 10, 25, 400);
        return (percent < 100 && next > 100) || (percent > 100 && next < 100) ? 100 : next;
    }

    // libVLC scale is relative to source pixels, while the gesture is relative to fit.
    public static float Scale(double width, double height, double viewportWidth, double viewportHeight, double sar, double aspect, int percent)
    {
        if (width <= 0 || height <= 0 || viewportWidth <= 0 || viewportHeight <= 0) return 0;
        var displayWidth = aspect > 0 ? height * aspect : width * (sar > 0 ? sar : 1);
        return (float)(Math.Min(viewportWidth / displayWidth, viewportHeight / height) * percent / 100.0);
    }
}
