using System.Reflection;
using System.Runtime.InteropServices;
using TarkovMapLite;

internal static class MapBehaviorChecks
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static;
    private static Type TypeOf(string name) => typeof(MapPack).Assembly.GetType("TarkovMapLite." + name)!;
    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static T Property<T>(object target, string name) =>
        (T)target.GetType().GetProperty(name, Flags)!.GetValue(target)!;
    private static Form NewForm()
    {
        var form = (Form)Activator.CreateInstance(TypeOf("MapForm"), true)!;
        _ = form.Handle;
        Call(form, "FillMapCombo");
        return form;
    }
    private static ToolStripMenuItem Menu(Form form, string name) =>
        Field<ContextMenuStrip>(form, "_layerMenu").Items.OfType<ToolStripMenuItem>().Single(i => i.Text == name);

    public static int Run()
    {
        var checks = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            checks++;
        }

        // Real screenshot fixes must be projected into each selected variant, not only the SVG entry.
        PlayerLocator.SetSetting("view", "");
        using (var form = NewForm())
        {
            var player = Field<PlayerLocator>(form, "_player");
            var canvas = Field<Control>(form, "_canvas");
            var fix = new PlayerLocator.Fix(-120, -2, 50, .6, .8, DateTime.Now, "test.png");
            typeof(PlayerLocator).GetProperty("Current")!.SetValue(player, fix);
            foreach (var variant in MapPack.Maps.Where(m => !string.IsNullOrEmpty(m.VariantOf)))
            {
                var basis = MapPack.Find(variant.VariantOf)!;
                Check(basis.Data == variant.Data, "Variants must share logical map data");
                foreach (var raid in new[] { basis, variant })
                {
                    SetField(form, "_raidMapKey", raid.Key);
                    foreach (var viewing in new[] { variant, basis, variant })
                    {
                        Call(form, "SelectMap", viewing.Key, false);
                        Call(form, "UpdatePlayerMarker");
                        var expected = MapPack.ToImage(viewing, fix.X, fix.Z);
                        var forward = MapPack.ToImage(viewing, fix.X + fix.ForwardX * 3, fix.Z + fix.ForwardZ * 3);
                        var position = Property<PointF>(canvas, "PlayerPosition");
                        var direction = Property<PointF>(canvas, "PlayerForward");
                        Check(Property<string>(canvas, "PlayerMapKey") == viewing.Key,
                            $"Player was hidden on {viewing.Key} with raid key {raid.Key}");
                        Check(Math.Abs(position.X - expected.X) < .02 && Math.Abs(position.Y - expected.Y) < .02,
                            $"Player used wrong projection on {viewing.Key}");
                        Check(Math.Abs(direction.X - (forward.X - expected.X)) < .02 &&
                              Math.Abs(direction.Y - (forward.Y - expected.Y)) < .02,
                            "Heading must follow the selected projection");
                    }
                }
            }
            SetField(form, "_raidMapKey", "reserve");
            Call(form, "SelectMap", "customs", false);
            Check(Property<string>(canvas, "PlayerMapKey") == "reserve", "Player leaked onto an unrelated map");
            typeof(PlayerLocator).GetProperty("Current")!.SetValue(player, null);
            SetField(form, "_testFix", (-120d, -2d, 50d, .6d, .8d));
            Call(form, "UpdatePlayerMarker");
            Check(Property<string>(canvas, "PlayerMapKey") == "customs", "Test screenshot projection changed");
        }

        // Persist via actual button/menu handlers and recreate the form from the saved file.
        PlayerLocator.SetSetting("view", "");
        PlayerLocator.SetSetting("unrelated", "preserve-me");
        string[] visibility = ["ShowExtracts", "ShowLabels", "ShowBtr", "ShowLoot", "ShowContainers",
            "ShowSpawns", "ShowHazards", "ShowStatics", "ShowDoors", "ShowSwitches", "OnlyActive"];
        Dictionary<string, bool> expectedVisibility;
        int[] hiddenLoot;
        string[] hiddenContainers;
        using (var form = NewForm())
        {
            Call(Field<Control>(form, "_onlyActive"), "OnClick", EventArgs.Empty);
            Call(Field<Control>(form, "_showExtracts"), "OnClick", EventArgs.Empty);
            Call(Field<Control>(form, "_noZone"), "OnClick", EventArgs.Empty);
            foreach (var name in new[] { "地名", "BTR 站点", "出生点", "危险区", "固定武器", "门锁（要钥匙）", "闸点（拉闸）" })
                Menu(form, name).PerformClick();
            Menu(form, "散落物资").DropDownItems.OfType<ToolStripMenuItem>().Skip(2).First().PerformClick();
            Menu(form, "容器").DropDownItems.OfType<ToolStripMenuItem>().ElementAt(1).PerformClick();
            Application.DoEvents();
            Call(form, "SelectMap", "reserve-tiles", false);
            var canvas = Field<Control>(form, "_canvas");
            Call(canvas, "SetLayer", "Third_Floor");
            Call(form, "SaveViewSettings");
            expectedVisibility = visibility.ToDictionary(name => name, name => Property<bool>(canvas, name));
            hiddenLoot = Property<HashSet<int>>(canvas, "HiddenLootKinds").Order().ToArray();
            hiddenContainers = Property<HashSet<string>>(canvas, "HiddenContainerKinds").Order().ToArray();
        }
        Check(PlayerLocator.GetSetting("unrelated") == "preserve-me", "Saving view settings removed an existing setting");
        using (var restored = NewForm())
        {
            Call(restored, "ApplyMap", false);
            Call(restored, "RestoreMapLayer");
            var canvas = Field<Control>(restored, "_canvas");
            foreach (var name in visibility)
                Check(Property<bool>(canvas, name) == expectedVisibility[name], $"{name} did not survive restart");
            Check(Property<HashSet<int>>(canvas, "HiddenLootKinds").Order().SequenceEqual(hiddenLoot), "Loot categories reset");
            Check(Property<HashSet<string>>(canvas, "HiddenContainerKinds").Order().SequenceEqual(hiddenContainers), "Container categories reset");
            Check(Menu(restored, "容器").CheckState == CheckState.Unchecked, "Hidden group menu was re-enabled");
            Check(Menu(restored, "散落物资").CheckState == CheckState.Indeterminate, "Partial group menu state was lost");
            Check(Menu(restored, "地名").Checked && Menu(restored, "门锁（要钥匙）").Checked, "Restored menu differs from canvas");
            Check(!Property<bool>(Field<Control>(restored, "_showExtracts"), "Active"), "Extract button differs from canvas");
            Check(Property<bool>(Field<Control>(restored, "_noZone"), "Active"), "Any-region filter reset");
            Check(Property<MapProjection>(canvas, "Map").Key == "reserve-tiles", "Last map variant reset");
            Check(Property<string>(canvas, "CurrentLayer") == "Third_Floor", "Last floor reset");
            Call(restored, "ApplyMap", true);
            Check(Property<string>(canvas, "CurrentLayer") == "Third_Floor", "Data refresh discarded the restored floor");
            Menu(restored, "散落物资").DropDownItems.OfType<ToolStripMenuItem>().ElementAt(1).PerformClick();
            // Re-enable only one container; the parent must restore as partial after another restart.
            Menu(restored, "容器").DropDownItems.OfType<ToolStripMenuItem>().Skip(2).First().PerformClick();
            Application.DoEvents();
            Call(restored, "SaveViewSettings");
        }
        using (var restored = NewForm())
        {
            Check(Menu(restored, "散落物资").CheckState == CheckState.Unchecked, "Hide-all loot reset");
            Check(Menu(restored, "容器").CheckState == CheckState.Indeterminate, "Partial container group reset");
        }

        PlayerLocator.SetSetting("view", "{broken");
        using (var fallback = NewForm())
        {
            Check(Menu(fallback, "容器").Checked, "Corrupt preferences did not fall back to defaults");
            Check(Field<RichTextBox>(fallback, "_detail").ScrollBars == RichTextBoxScrollBars.Vertical,
                "Task description still disables native scrolling");
        }
        PlayerLocator.SetSetting("view", "{\"HiddenLootKinds\":null,\"HiddenContainerKinds\":null,\"MapKey\":\"removed-map\"}");
        using (var fallback = NewForm())
        {
            Check(Property<MapProjection>(fallback, "CurrentMap").Key == "customs", "Removed map did not fall back safely");
        }

        // Exercise native RichEdit scrolling and hover forwarding while another control retains focus.
        using (var host = new Form { ClientSize = new Size(420, 280), StartPosition = FormStartPosition.Manual,
                   Location = Screen.PrimaryScreen!.WorkingArea.Location + new Size(20, 20) })
        using (var detail = (RichTextBox)Activator.CreateInstance(TypeOf("TaskDescriptionBox"), true)!)
        using (var other = new TextBox { Bounds = new Rectangle(10, 10, 330, 25) })
        {
            detail.Bounds = new Rectangle(10, 50, 340, 150);
            detail.Text = string.Join(Environment.NewLine, Enumerable.Range(1, 120).Select(i => $"任务目标 {i}: 测试滚轮滚动"));
            host.Controls.Add(detail);
            host.Controls.Add(other);
            host.Show();
            Application.DoEvents();
            detail.Select(0, 0);
            detail.ScrollToCaret();
            other.Focus();
            Application.DoEvents();
            var screen = detail.PointToScreen(new Point(40, 40));
            IntPtr Pack(Point point) => (IntPtr)unchecked((int)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16)));
            Message Wheel(IntPtr target, int delta, Point point) =>
                Message.Create(target, 0x020A, (IntPtr)(delta << 16), Pack(point));
            var before = SendMessage(detail.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32();
            var down = Wheel(other.Handle, -120, screen);
            Check(Application.FilterMessage(ref down), "Hover wheel was not routed to task description");
            var after = SendMessage(detail.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32();
            Check(after > before, "Long task description did not scroll down");
            Check(other.Focused && !detail.Focused, "Scrolling stole keyboard focus");
            var up = Wheel(other.Handle, 120, screen);
            Check(Application.FilterMessage(ref up), "Upward wheel was not routed");
            Check(SendMessage(detail.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() < after,
                "Task description did not scroll up");
            var outside = Wheel(other.Handle, -120, other.PointToScreen(new Point(10, 10)));
            Check(!Application.FilterMessage(ref outside), "Description intercepted another control's wheel");
            var direct = Wheel(detail.Handle, -120, screen);
            Check(!Application.FilterMessage(ref direct), "Native wheel would have been processed twice");
            var startLine = SendMessage(detail.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32();
            SendMessage(detail.Handle, direct.Msg, direct.WParam, direct.LParam);
            Check(SendMessage(detail.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() > startLine,
                "Direct wheel delivery did not scroll");
            detail.Select(0, 0);
            detail.ScrollToCaret();
            var half = Wheel(other.Handle, -60, screen);
            Check(Application.FilterMessage(ref half), "High-resolution wheel was not routed");
            Check(SendMessage(detail.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() == 0,
                "A half-notch should wait for the remaining delta");
            var secondHalf = Wheel(other.Handle, -60, screen);
            Check(Application.FilterMessage(ref secondHalf), "Second high-resolution wheel delta was lost");
            Check(SendMessage(detail.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() > 0,
                "High-resolution wheel deltas were not accumulated");
            using (var overlay = new Panel { Bounds = detail.Bounds })
            {
                host.Controls.Add(overlay);
                overlay.BringToFront();
                Application.DoEvents();
                var covered = Wheel(other.Handle, -120, screen);
                Check(!Application.FilterMessage(ref covered), "Description stole wheel input through an overlapping control");
            }
            host.Close();
        }
        PlayerLocator.SetSetting("view", "");
        Console.WriteLine($"PASS: {checks} player variant, restart preferences and task wheel checks");
        return checks;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
