using System.Collections;
using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using TarkovMapLite;

internal static class Program
{
    private static readonly Assembly App = typeof(MapPack).Assembly;
    private const BindingFlags Methods = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.Instance;
    private static int _checks;

    private static Type TypeOf(string name) => App.GetType("TarkovMapLite." + name)!;
    private static object? Call(Type type, object? target, string method, params object?[] args) =>
        type.GetMethod(method, Methods)!.Invoke(target, args);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }

    private static void Location(double x, double z, double y, string expected)
    {
        foreach (var key in new[] { "reserve", "reserve-tiles" })
        {
            object?[] args = { key, x, z, y, "" };
            Check((bool)Call(TypeOf("MapFloors"), null, "TryLayer", args)! &&
                (string)args[4]! == expected, $"{key}: ({x},{z},{y}) expected {expected}");
            var floor = expected switch
            {
                "Bunkers" => -1, "Second_Floor" => 1, "Third_Floor" => 2,
                "Fourth_Floor" => 3, "Fifth_Floor" => 4, _ => 0,
            };
            Check(TarkovData.FloorOfHeight(key, x, z, y) == floor, "Player floor mismatch");
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        var svg = MapPack.Find("reserve")!;
        var tiles = MapPack.Find("reserve-tiles")!;
        Check(svg.Layers.Count == 5 && tiles.Layers.Count == 5, "Both variants need six floors");
        Check(svg.Layers.Select(l => l.SvgLayer).SequenceEqual(tiles.Layers.Select(l => l.SvgLayer)),
            "Variant floors differ");
        Check(svg.Layers.Single(l => l.SvgLayer == "Bunkers").Floor == -1, "Chinese bunker label");
        Check(MapPack.Resolve("reserve", false)!.Key == "reserve", "Explicit SVG selection redirected");
        Check(MapPack.Resolve("reserve", true)!.Key == "reserve-tiles", "Default selection changed");

        Location(-120, 50, -2, "Second_Floor");
        Location(-120, 50, 0, "Third_Floor");
        Location(-120, 50, 3, "Fourth_Floor");
        Location(-120, 50, 6, "Fifth_Floor");
        Location(-8, 180, 24, "Second_Floor"); // Same floor, different terrain height.
        Location(-8, 180, 27, "Third_Floor");
        Location(-120, 50, -0.64, "Third_Floor"); // Shared boundary belongs to upper floor.
        Location(-120, 50, -10, "Bunkers");
        Location(-80, 150, -13, "Bunkers"); // D2 uses a different height threshold.
        Location(-8, 180, 17, "Bunkers"); // Dome tunnel is above most ground buildings.
        Location(250, 200, 0, ""); // Height alone must not classify an unrelated area.
        Location(-120, 50, -5, "");

        var markerType = TypeOf("MapMarkers");
        string[] MarkerLayers(MapProjection map)
        {
            var markers = Call(markerType, null, "For", map)!;
            var containers = ((IEnumerable)markers.GetType().GetProperty("Containers")!.GetValue(markers)!)
                .Cast<(PointF Point, string Kind, float Y, string? Floor)>();
            var loose = ((IEnumerable)markers.GetType().GetProperty("Loose")!.GetValue(markers)!)
                .Cast<object>().Select(p => (string?)p.GetType().GetProperty("FloorLayer")!.GetValue(p));
            return containers.Select(p => p.Floor).Concat(loose).Select(s => s ?? "MISSING").ToArray();
        }
        var floors = MarkerLayers(svg);
        Check(!floors.Contains("MISSING"), "Reserve markers were not classified");
        Check(floors.SequenceEqual(MarkerLayers(tiles)), "SVG and tiles classify markers differently");
        foreach (var layer in svg.Layers)
        {
            Check(floors.Contains(layer.SvgLayer), $"No markers visible on {layer.Name}");
            Console.WriteLine($"{layer.Name}: {floors.Count(f => f == layer.SvgLayer)} loot/container markers");
        }
        Check(!(bool)Call(TypeOf("MarkerFloors"), null, "Matches", "reserve", 0f, "", "Third_Floor")!,
            "Upper-floor loot leaked onto ground");

        var canvasType = TypeOf("MapCanvas");
        using var canvas = (Control)Activator.CreateInstance(canvasType, true)!;
        canvas.Size = new Size(1000, 850);
        canvas.CreateControl();
        string? requested = null;
        canvasType.GetEvent("FloorRequested")!.AddEventHandler(canvas, (Action<string>)(layer =>
        {
            requested = layer;
            Call(canvasType, canvas, "SetLayer", layer);
        }));

        string Render(MapProjection map, string layer, string? filename)
        {
            Call(canvasType, canvas, "SetMap", map, map.DisplayName);
            Call(canvasType, canvas, "SetLayer", layer);
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
            if (filename != null) bitmap.Save(filename, ImageFormat.Png);
            // Exclude floor buttons/status: the map content itself must change.
            using var crop = bitmap.Clone(new Rectangle(120, 20, 850, 780), PixelFormat.Format32bppArgb);
            using var stream = new MemoryStream();
            crop.Save(stream, ImageFormat.Png);
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }
        var output = args.FirstOrDefault();
        if (output != null) Directory.CreateDirectory(output);
        foreach (var map in MapPack.Maps)
        {
            var hashes = new HashSet<string>();
            Check(map.Layers.Select(l => l.Floor).Distinct().Count() == map.Layers.Count,
                $"{map.Key}: duplicate floor numbers");
            Check(map.Layers.All(l => l.Floor != 0), $"{map.Key}: ground companion advertised as extra floor");
            foreach (var layer in new[] { "" }.Concat(map.Layers.Select(l => l.SvgLayer)))
            {
                if (layer != "")
                {
                    using var resource = (Stream?)Call(TypeOf("Assets"), null, "Open", "maps/" + map.ImageFor(layer) + ".png");
                    Check(resource != null, $"{map.Key}/{layer}: missing image silently falls back to base");
                }
                var filename = output == null ? null : Path.Combine(output,
                    map.Key + "-" + (layer == "" ? "ground" : layer) + ".png");
                Check(hashes.Add(Render(map, layer, filename)), "Switching floors left identical map content");
            }
        }
        // Regression for maps with exactly ONE additional floor.
        var oneFloor = new MapProjection
        {
            Key = "reserve", ViewBox = svg.ViewBox, Bounds = svg.Bounds,
            Transform = svg.Transform, Rotation = svg.Rotation,
            Layers = new[] { svg.Layers.Single(l => l.SvgLayer == "Bunkers") },
        };
        Check(Render(oneFloor, "", null) != Render(oneFloor, "Bunkers", null), "Single extra floor not rendered");
        Call(canvasType, canvas, "SetMap", svg, "储备站");
        Call(canvasType, canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 90, 180, 0));
        Check(requested == "Bunkers", "Lower/right edge of bunker button not clickable");
        Check((int)canvasType.GetProperty("CurrentFloor")!.GetValue(canvas)! == -1, "Bunker shown as ground");
        requested = null;
        Call(canvasType, canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 90, 158, 0));
        Check(requested == null, "Button gap incorrectly selected a floor");
        Call(canvasType, canvas, "SetLayer", "missing-floor");
        Check((string)canvasType.GetProperty("CurrentLayer")!.GetValue(canvas)! == "Bunkers", "Invalid layer accepted");
        Check(MapPack.Find("interchange")!.Layers.Single(l => l.SvgLayer == "First_Floor").Floor == 1,
            "Interchange First_Floor is actually second level");
        Check(MapPack.Find("the-lab")!.Layers.Single(l => l.SvgLayer == "Technical").Floor == -1,
            "Lab technical floor is underground");
        Check(TarkovData.FloorOfHeight("the-lab", -180, -350, -2) == -1, "Lab height rule");
        Check(TarkovData.FloorOfHeight("factory", 0, 0, -2) == -1, "Factory height rule");
        Check((bool)Call(TypeOf("MarkerFloors"), null, "Matches", "customs-tiles", -5f, "Underground", "Underground_Level")!,
            "Equivalent underground IDs disagree across variants");

        // Exercise real menu state handlers without starting game-log/network polling.
        var formType = TypeOf("MapForm");
        using var form = (Form)Activator.CreateInstance(formType, true)!;
        _ = form.Handle;
        var menu = (ContextMenuStrip)formType.GetField("_layerMenu", Methods)!.GetValue(form)!;
        var button = (Control)formType.GetField("_layerButton", Methods)!.GetValue(form)!;
        var formCanvas = (Control)formType.GetField("_canvas", Methods)!.GetValue(form)!;
        var loot = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "散落物资");
        var containers = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "容器");
        Check(containers.CheckState == CheckState.Checked &&
            (bool)canvasType.GetProperty("ShowContainers")!.GetValue(formCanvas)!, "Initial container state inconsistent");
        var invalidations = 0;
        formCanvas.Invalidated += (_, _) => invalidations++;
        loot.DropDownItems.OfType<ToolStripMenuItem>().Skip(2).First().PerformClick();
        Application.DoEvents();
        Check(loot.CheckState == CheckState.Indeterminate, "Loot parent did not become partial");
        Check(button.Text == "图层 2", "Partial groups missing from layer count");
        invalidations = 0;
        containers.DropDownItems.OfType<ToolStripMenuItem>().ElementAt(1).PerformClick();
        Application.DoEvents();
        Check(containers.CheckState == CheckState.Unchecked && button.Text == "图层 1", "Hide-all state/count mismatch");
        Check(invalidations <= 1, "Batch layer changes caused multiple canvas redraws");
        containers.DropDownItems.OfType<ToolStripMenuItem>().First().PerformClick();
        Application.DoEvents();
        Check(containers.CheckState == CheckState.Checked && button.Text == "图层 2", "Show-all state/count mismatch");
        Call(formType, form, "FillMapCombo");
        var mapButton = (Control)formType.GetField("_mapButton", Methods)!.GetValue(form)!;
        var mapMenu = (ContextMenuStrip)formType.GetField("_mapMenu", Methods)!.GetValue(form)!;
        Call(mapButton.GetType(), mapButton, "OnClick", EventArgs.Empty);
        Check(mapMenu.Visible && !mapMenu.AutoClose, "Map menu did not open under explicit toggle control");
        var firstRow = mapMenu.Items[0];
        Call(mapButton.GetType(), mapButton, "OnClick", EventArgs.Empty);
        Check(!mapMenu.Visible, "Second map-button click reopened the menu");
        Call(mapButton.GetType(), mapButton, "OnClick", EventArgs.Empty);
        Check(mapMenu.Visible && ReferenceEquals(firstRow, mapMenu.Items[0]), "Menu rows were unnecessarily rebuilt");
        var escape = Message.Create(mapMenu.Handle, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero);
        Check(Application.FilterMessage(ref escape) && !mapMenu.Visible, "Escape did not close the menu");
        Call(button.GetType(), button, "OnClick", EventArgs.Empty);
        Check(menu.Visible, "Layer menu did not open");
        Call(button.GetType(), button, "OnClick", EventArgs.Empty);
        Check(!menu.Visible, "Second layer-button click did not close the menu");
        Console.WriteLine($"PASS: {_checks} checks, {MapPack.Maps.Count} maps, " +
            $"{MapPack.Maps.Sum(m => 1 + m.Layers.Count)} native floor renders, menu state/batching and single-floor regression.");
    }
}
