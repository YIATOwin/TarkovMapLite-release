using System.Collections;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovMapLite;

internal static class PointOpacityChecks
{
    private static readonly Assembly App = typeof(MapPack).Assembly;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Type TypeOf(string name) => App.GetType("TarkovMapLite." + name)!;
    private static object? Call(string type, object? target, string method, params object?[] args) =>
        TypeOf(type).GetMethod(method, Flags)!.Invoke(target, args);

    public static int Run(string? output)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }
        float Opacity(string? layer, string selected) =>
            (float)Call("MarkerFloors", null, "OpacityForLayer", layer, selected)!;
        Check(Opacity("", "") == 1f, "Ground marker faded on ground");
        Check(Opacity("Bunkers", "Bunkers") == 1f, "Same-floor marker faded");
        Check(Opacity("Bunkers", "") == .35f && Opacity("", "Bunkers") == .35f,
            "Other-floor markers must remain visible at 35 percent");
        Check(Opacity("Underground_Level", "Underground") == 1f, "Alias floor IDs disagree");
        Check(Opacity(null, "Bunkers") == 1f, "Unknown height was guessed/dimmed");

        var map = MapPack.Find("reserve")!;
        var tiles = MapPack.Find("reserve-tiles")!;
        var world = new MapMarker { Name = "test exit", X = -120, Z = 50, Y = -10, Kind = "extract" };
        var marker = world.Project(map);
        Check(marker.Y == -10 && marker.FloorLayer == "Bunkers", "Projection lost marker height/floor");
        Check(marker.FloorLayer == world.Project(tiles).FloorLayer, "Marker floor differs across variants");
        var d2 = new MapMarker { Name = "D-2", X = -121.48, Z = 172.25, Kind = "extract" }.Project(map);
        Check(d2.FloorLayer == "Bunkers", "Offline D-2 must retain annotated underground floor");
        Check((string?)Call("PointHeights", null, "LayerAt", map, "extract", 250d, 200d, null, null) == "",
            "An outdoor point outside every layer region should resolve to ground");
        Check(Call("PointHeights", null, "LayerAt", map, "door", -120d, 50d, null, null) == null,
            "Missing height inside a multi-floor building must not be guessed");

        var table = new Dictionary<string, List<MapMarker>> { ["reserve"] = new() { world } };
        var json = (JsonObject)Call("TarkovData", null, "SerializeMarkers", table)!;
        var loaded = (Dictionary<string, List<MapMarker>>)Call("TarkovData", null, "ReadMarkers", json)!;
        Check(loaded["reserve"][0].Y == -10, "Cache round-trip lost world height");
        using var source = JsonDocument.Parse("{\"name\":\"test\",\"position\":{\"x\":0,\"y\":0,\"z\":0}}");
        var live = (MapMarker)Call("TarkovData", null, "ReadMarker", source.RootElement, "extract", new Dictionary<string, string>())!;
        Check(live.Y == 0, "Zero world height must not be treated as missing");

        var pair = Call("MapDoors", null, "For", map)!;
        var switches = (IList)pair.GetType().GetField("Item2")!.GetValue(pair)!;
        Check(switches.Count == 3 && switches.Cast<object>().All(p => p.GetType().GetProperty("FloorLayer")!.GetValue(p) != null),
            "Reserve switch snapshots did not resolve real heights");
        var underground = switches.Cast<object>().First(p => (string?)p.GetType().GetProperty("FloorLayer")!.GetValue(p) == "Bunkers");
        underground.GetType().GetProperty("Screen")!.SetValue(underground, new PointF(120, 120));
        var selectedSwitch = (IList)Activator.CreateInstance(switches.GetType())!;
        selectedSwitch.Add(underground);

        using var canvas = (Control)Activator.CreateInstance(TypeOf("MapCanvas"), true)!;
        canvas.Size = new Size(600, 300);
        Call("MapCanvas", canvas, "SetMap", map, "储备站");
        TypeOf("MapCanvas").GetProperty("Switches")!.SetValue(canvas, selectedSwitch);
        d2.Screen = new PointF(120, 120);
        TypeOf("MapCanvas").GetProperty("Extracts")!.SetValue(canvas, new List<MapMarker> { d2 });

        long AlphaSum(Bitmap image, Rectangle bounds)
        {
            long sum = 0;
            for (var y = bounds.Top; y < bounds.Bottom; y++)
                for (var x = bounds.Left; x < bounds.Right; x++) sum += image.GetPixel(x, y).A;
            return sum;
        }
        foreach (var method in new[] { "DrawMarkers", "DrawSwitchPoints" })
        {
            var sums = new List<(long Icon, long Label)>();
            foreach (var layer in new[] { "Bunkers", "" })
            {
                Call("MapCanvas", canvas, "SetLayer", layer);
                using var bitmap = new Bitmap(600, 300, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bitmap))
                    Call("MapCanvas", canvas, method, g, 1f, PointF.Empty);
                sums.Add((AlphaSum(bitmap, new Rectangle(110, 108, 17, 24)),
                    AlphaSum(bitmap, new Rectangle(128, 105, 440, 35))));
                if (output != null) bitmap.Save(Path.Combine(output, method + (layer == "" ? "-other.png" : "-same.png")));
            }
            Check(sums[1].Icon > 0 && sums[1].Icon < sums[0].Icon * .7, method + ": other-floor icon hidden or opaque");
            Check(sums[1].Label > 0 && sums[1].Label < sums[0].Label * .7, method + ": label/background did not fade");
        }

        var mobile = Activator.CreateInstance(TypeOf("MobileState"), true)!;
        void Set(string property, object value) => TypeOf("MobileState").GetProperty(property)!.SetValue(mobile, value);
        float MobileOpacity()
        {
            Call("MobileState", mobile, "Flush");
            using var doc = JsonDocument.Parse((string)TypeOf("MobileState").GetProperty("Json")!.GetValue(mobile)!);
            return doc.RootElement.GetProperty("marks")[0].GetProperty("a").GetSingle();
        }
        Set("Map", map); Set("Layer", ""); Set("Extracts", new List<MapMarker> { d2 });
        Check(MobileOpacity() == .35f, "Mobile extract opacity differs from desktop");
        Set("Layer", "Bunkers");
        Check(MobileOpacity() == 1f, "Mobile same-floor opacity not restored");
        Set("Layer", "");
        Set("Extracts", new List<MapMarker> { new() { Name = "test", X = 20, Z = 20, Kind = "extract", Screen = new PointF(120, 120) } });
        Check(MobileOpacity() == 1f, "Unknown mobile floor should remain readable");
        Set("Extracts", new List<MapMarker> { new() { Name = "test", X = 20, Z = 20, Kind = "extract", Screen = new PointF(120, 120), FloorLayer = "Bunkers" } });
        Check(MobileOpacity() == .35f, "Late height metadata did not refresh mobile state");
        Console.WriteLine($"PASS: {checks} marker opacity, offline switch/D-2, cache and mobile checks.");
        return checks;
    }
}
