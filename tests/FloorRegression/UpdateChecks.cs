using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovMapLite;

internal static class UpdateChecks
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) => reply(request, cancel);
    }
    private sealed class CancelStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { cancellation.Cancel(); return ValueTask.FromCanceled<int>(cancellation.Token); }
    }
    private static ReleaseOffer Offer(Version version, byte[] bytes) => new("v" + AppUpdate.DisplayVersion(version), version, "Changes", new Uri(AppUpdate.ReleasePage),
        new Uri("https://github.com/" + AppUpdate.Repository + "/releases/download/v" + AppUpdate.DisplayVersion(version) + "/TarkovMapLite.exe"),
        bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)), null);
    private static string FindHost()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TarkovMapLite.csproj")))
                return Path.Combine(directory.FullName, "bin", "Release", "net10.0-windows", "win-x64", "TarkovMapLite.exe");
        throw new IOException("Cannot locate built application host");
    }
    private static string ReleaseJson(string tag, byte[] bytes, string? digest = null, string? url = null, bool prerelease = false, bool draft = false, bool checksum = false) =>
        JsonSerializer.Serialize(new { tag_name = tag, prerelease, draft, body = "更新说明\n普通文本，不执行代码", assets = new object[]
        { new { name = "TarkovMapLite.exe", state = "uploaded", size = bytes.LongLength,
            digest = digest ?? "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
            browser_download_url = url ?? "https://github.com/" + AppUpdate.Repository + "/releases/download/" + tag + "/TarkovMapLite.exe" } }
            .Concat(checksum ? [new { name = "TarkovMapLite.exe.sha256", state = "uploaded", size = 90,
                browser_download_url = "https://github.com/" + AppUpdate.Repository + "/releases/download/" + tag + "/TarkovMapLite.exe.sha256" }] : []) });
    public static int Run(string? output)
    {
        var previous = Environment.GetEnvironmentVariable("TARKOVMAPLITE_UPDATE_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "TarkovMapLite-update-checks-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TARKOVMAPLITE_UPDATE_ROOT", root);
        var context = SynchronizationContext.Current;
        int checks;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            checks = NetworkAndFiles().GetAwaiter().GetResult();
            SynchronizationContext.SetSynchronizationContext(context);
            checks += UserInterface(output);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
            if (Directory.Exists(root)) foreach (var directory in Directory.EnumerateDirectories(root)) UpdateFiles.CleanSession(directory);
            Environment.SetEnvironmentVariable("TARKOVMAPLITE_UPDATE_ROOT", previous);
            PlayerLocator.SetSetting("updates", "");
        }
        Console.WriteLine($"PASS: {checks} software update versions, trusted URLs, streaming integrity/cancel, rollback, preferences and UI checks.");
        return checks;
    }
    private static async Task<int> NetworkAndFiles()
    {
        var checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }
        void Reject(Action action, string label)
        { try { action(); throw new Exception(label); } catch (InvalidDataException) { checks++; } }
        async Task RejectAsync(Func<Task> action, string label)
        { try { await action(); throw new Exception(label); } catch (Exception e) when (e is InvalidDataException or OperationCanceledException or HttpRequestException or BadImageFormatException) { checks++; } }
        var bytes = File.ReadAllBytes(FindHost());
        var current = AppUpdate.CurrentVersion;
        var old = new Version(0, 0, 0, 0); var tag = "v" + AppUpdate.DisplayVersion(current);
        Check(AppUpdate.ParseVersion("v0.4.12") > AppUpdate.ParseVersion("v0.4.9"), "Version comparison is lexical");
        Check(AppUpdate.ParseVersion("1.2.3") == AppUpdate.ParseVersion("v1.2.3.0"), "Version normalization failed");
        foreach (var bad in new[] { "v1.2.3-beta", "latest", "v01.2.3", "1.2", "v1.2.3/evil" }) Reject(() => AppUpdate.ParseVersion(bad), "Invalid tag accepted");
        var metadata = ReleaseJson(tag, bytes);
        var offer = AppUpdate.ParseRelease(metadata, old).Offer!;
        Check(offer.Size == bytes.LongLength && offer.Sha256 == UpdateFiles.Hash(FindHost()), "Release metadata parse failed");
        Check(AppUpdate.ParseRelease(metadata, current).Offer == null, "Same version offered");
        Check(AppUpdate.ParseRelease(metadata, new Version(99, 0, 0)).Offer == null, "Downgrade offered");
        Reject(() => AppUpdate.ParseRelease(ReleaseJson(tag, bytes, prerelease: true), old), "Prerelease offered as stable");
        Reject(() => AppUpdate.ParseRelease(ReleaseJson(tag, bytes, draft: true), old), "Draft offered");
        Reject(() => AppUpdate.ParseRelease(ReleaseJson(tag, bytes, digest: "sha256:invalid"), old), "Malformed digest accepted");
        foreach (var url in new[] { "https://evil.example/TarkovMapLite.exe", "http://github.com/" + AppUpdate.Repository + "/releases/download/" + tag + "/TarkovMapLite.exe",
            "https://github.com/another/project/releases/download/"+tag+"/TarkovMapLite.exe" })
            Reject(() => AppUpdate.ParseRelease(ReleaseJson(tag, bytes, url: url), old), "Foreign update URL accepted");
        Reject(() => AppUpdate.ParseRelease(ReleaseJson(tag, bytes, digest: "sha512:missing"), old), "Unverified release accepted");
        Check(UpdateSettings.ValidateProxy("http://127.0.0.1:7890").Port == 7890, "Proxy rejected");
        foreach (var proxy in new[] { "file:///c:/", "http://user:password@localhost:7890", "http://localhost:7890/path" })
            Reject(() => UpdateSettings.ValidateProxy(proxy), "Unsafe proxy accepted");
        using (var service = new AppUpdate(new(), new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metadata) }))))
            Check((await service.CheckAsync(old, default)).Offer?.Tag == tag, "HTTP version check failed");
        using (var service = new AppUpdate(new(), new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)))))
            await RejectAsync(() => service.CheckAsync(old, default), "HTTP failure not reported");
        using (var service = new AppUpdate(new(), new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (request.RequestUri.AbsoluteUri == AppUpdate.ReleasePage)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://github.com/"+AppUpdate.Repository+"/releases/tag/"+tag); return Task.FromResult(redirect);
            }
            var content = request.RequestUri.AbsolutePath.EndsWith(".sha256")
                ? (HttpContent)new StringContent(offer.Sha256+"  TarkovMapLite.exe\n") : new ByteArrayContent(bytes);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        })))
            Check((await service.CheckAsync(old, default)).Offer?.Sha256 == offer.Sha256, "API quota fallback did not verify release checksum/size");
        using (var service = new AppUpdate(new(), new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://untrusted.example/payload"); return Task.FromResult(response);
        }))) await RejectAsync(() => service.DownloadAsync(offer, null, default), "Untrusted redirect followed");
        UpdateDownload verified;
        using (var service = new AppUpdate(new(), new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }))))
            verified = await service.DownloadAsync(offer, null, default);
        Check(File.Exists(verified.Payload) && UpdateFiles.Hash(verified.Payload) == offer.Sha256, "Valid streamed executable rejected");
        var before = Directory.GetDirectories(UpdateFiles.Root).Length;
        using (var service = new AppUpdate(new(), new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }))))
            await RejectAsync(() => service.DownloadAsync(offer with { Sha256 = new string('0', 64) }, null, default), "Hash mismatch accepted");
        Check(Directory.GetDirectories(UpdateFiles.Root).Length == before, "Failed download retained partial files");
        using (var service = new AppUpdate(new(), new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[..^1]) }))))
            await RejectAsync(() => service.DownloadAsync(offer, null, default), "Truncated download accepted");
        using (var cancel = new CancellationTokenSource())
        using (var service = new AppUpdate(new(), new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new CancelStream(bytes, cancel)) }))))
            await RejectAsync(() => service.DownloadAsync(offer, null, cancel.Token), "Cancelled download completed");
        Check(Directory.GetDirectories(UpdateFiles.Root).Length == before, "Cancel retained partial files");
        var withChecksum = offer with { Sha256 = null, Checksum = new Uri(offer.Download.AbsoluteUri + ".sha256") };
        using (var service = new AppUpdate(new(), new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256") ? new StringContent(offer.Sha256 + "  TarkovMapLite.exe\n") : new ByteArrayContent(bytes) }))))
        {
            var fallback = await service.DownloadAsync(withChecksum, null, default);
            Check(fallback.Sha256 == offer.Sha256, "Checksum file fallback failed"); UpdateFiles.CleanSession(fallback.Directory);
        }
        var installFolder = Path.Combine(Path.GetDirectoryName(UpdateFiles.Root)!, "install-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(installFolder);
        var target = Path.Combine(installFolder, "地图 tool $ ' spaces.exe");
        var oldBytes = bytes.Concat(new byte[] { 0 }).ToArray(); File.WriteAllBytes(target, oldBytes);
        var settings = Path.Combine(installFolder, "user-settings.txt"); File.WriteAllText(settings, "preserve");
        var plan = new UpdatePlan(target, UpdateFiles.Hash(target), "0.0.0.0", verified.Sha256, current.ToString(4), bytes.Length, Environment.ProcessId,
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, new string('a',64), new string('b',64));
        var restarted = false;
        await UpdateInstaller.ApplyAsync(plan, verified.Directory, () => Task.FromResult(true), () => restarted = true);
        Check(UpdateFiles.Hash(target) == verified.Sha256 && !restarted, "Successful replacement failed");
        var backup = target + ".backup." + Path.GetFileName(verified.Directory);
        Check(File.ReadAllBytes(backup).SequenceEqual(oldBytes), "Old executable backup missing");
        File.WriteAllBytes(target, oldBytes); File.Delete(backup);
        await UpdateInstaller.ApplyAsync(plan, verified.Directory, () => Task.FromResult(false), () => restarted = true);
        Check(File.ReadAllBytes(target).SequenceEqual(oldBytes) && restarted, "Startup failure did not roll back and restart old");
        Check(File.ReadAllText(settings) == "preserve", "Updater modified unrelated settings");
        File.WriteAllBytes(target, oldBytes.Concat(new byte[] { 1 }).ToArray());
        await RejectAsync(() => UpdateInstaller.ApplyAsync(plan, verified.Directory, () => Task.FromResult(true), () => {}), "Changed target was overwritten");
        Check(File.ReadAllBytes(target).SequenceEqual(oldBytes.Concat(new byte[] { 1 })), "Target conflict mutated original");
        UpdateFiles.CleanSession(verified.Directory);
        return checks;
    }
    private static int UserInterface(string? output)
    {
        var checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }
        PlayerLocator.SetSetting("updates", "");
        Check(UpdateSettings.Load().AutoCheck, "First launch without update settings failed");
        PlayerLocator.SetSetting("updates", "broken json");
        Check(UpdateSettings.Load().ProxyMode == "system", "Corrupt update settings did not fall back");
        PlayerLocator.SetSetting("unrelated-updater", "keep");
        new UpdateSettings { AutoCheck = false, ProxyMode = "custom", ProxyUrl = "http://127.0.0.1:7890", SkippedVersion = "v9.9.9" }.Save();
        var saved = UpdateSettings.Load();
        Check(!saved.AutoCheck && saved.ProxyMode == "custom" && saved.SkippedVersion == "v9.9.9", "Update preferences lost");
        Check(PlayerLocator.GetSetting("unrelated-updater") == "keep", "Update preferences overwrote other settings");
        using var owner = new MapForm(); _ = owner.Handle;
        using var panel = new UpdateForm(owner); panel.Show(); Application.DoEvents();
        Check(panel.Visible && !owner.SoftwareUpdateBusy, "Opening update panel unexpectedly checked/downloaded");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        V Field<V>(string name) => (V)typeof(UpdateForm).GetField(name, flags)!.GetValue(panel)!;
        Check(!Field<Button>("_download").Enabled, "Download enabled with no verified release");
        var offer = new ReleaseOffer("v9.9.9", new Version(9,9,9,0), "更新说明\n" + string.Join('\n', Enumerable.Range(1,30).Select(i => "任务、地图与更新检查 " + i)),
            new Uri(AppUpdate.ReleasePage), new Uri("https://github.com/"+AppUpdate.Repository+"/releases/download/v9.9.9/TarkovMapLite.exe"), 131000000, new string('a',64), null);
        typeof(MapForm).GetField("_updateOffer", flags)!.SetValue(owner, offer);
        typeof(UpdateForm).GetMethod("RefreshState", flags)!.Invoke(panel, null);
        Check(Field<Button>("_download").Enabled && Field<RichTextBox>("_notes").ReadOnly, "Offer UI unavailable or notes editable");
        Field<Button>("_skip").PerformClick();
        Check(UpdateSettings.Load().SkippedVersion == "v9.9.9", "Skip version handler did not persist");
        typeof(MapForm).GetField("_updateInstalling", flags)!.SetValue(owner, true);
        typeof(UpdateForm).GetMethod("RefreshState", flags)!.Invoke(panel, null);
        Check(!Field<Button>("_later").Enabled, "Later remains actionable after restart was confirmed");
        panel.Close(); Check(panel.Visible, "Closing panel cancelled an already confirmed prepare step ambiguously");
        typeof(MapForm).GetField("_updateInstalling", flags)!.SetValue(owner, false);
        typeof(UpdateForm).GetMethod("RefreshState", flags)!.Invoke(panel, null);
        foreach (var width in new[] { 600, 500 })
        {
            panel.ClientSize = new Size(width,550); panel.PerformLayout(); Application.DoEvents();
            Check(Field<RichTextBox>("_notes").Height > 100 && Field<Button>("_download").Right <= Field<Button>("_download").Parent!.ClientSize.Width, "Update panel clips controls");
        }
        if (output != null)
        {
            Directory.CreateDirectory(output); panel.ClientSize = new Size(600,550); panel.PerformLayout();
            using var image = new Bitmap(panel.Width,panel.Height); panel.DrawToBitmap(image,new Rectangle(Point.Empty,panel.Size));
            image.Save(Path.Combine(output,"software-update-panel.png"));
        }
        return checks;
    }
}
