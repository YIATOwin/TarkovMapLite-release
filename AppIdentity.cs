namespace TarkovMapLite;

internal static class AppIdentity
{
    internal static Icon? LoadIcon()
    {
        using var stream = Assets.Open("app.ico");
        if (stream == null) return null;
        using var icon = new Icon(stream, new Size(32,32));
        return (Icon)icon.Clone();
    }
}
