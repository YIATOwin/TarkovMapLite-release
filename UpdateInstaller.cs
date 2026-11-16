using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace TarkovMapLite;

internal static class UpdateFiles
{
    public static string Root => Path.GetFullPath(Environment.GetEnvironmentVariable("TARKOVMAPLITE_UPDATE_ROOT") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TarkovMapLite", "Updates"));
    public static string CreateSession()
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }
    public static void ValidateSession(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(full), Root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out _) ||
            (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新临时目录不符合要求。");
    }
    public static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    public static void VerifyExecutable(string path, Version version)
    {
        using (var stream = File.OpenRead(path))
        using (var reader = new PEReader(stream))
            if (reader.PEHeaders.CoffHeader.Machine != Machine.Amd64 || reader.PEHeaders.PEHeader == null)
                throw new InvalidDataException("更新文件不是 Windows x64 程序。");
        var info = FileVersionInfo.GetVersionInfo(path);
        var actual = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        if (info.ProductName != "TarkovMapLite" || actual != AppUpdate.Normalize(version))
            throw new InvalidDataException("更新文件的产品或版本与发布信息不一致。");
    }
    public static void WriteJson<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, AppUpdate.Json));
        File.Move(temporary, path, true);
    }
    public static T? ReadJson<T>(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), AppUpdate.Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }
    public static void CleanSession(string directory)
    {
        try
        {
            ValidateSession(directory);
            foreach (var name in new[] { "payload.part", "payload.exe", "updater.exe", "plan.json", "ready.json", "healthy.json", "result.json", "cancel" })
            {
                foreach (var suffix in new[] { "", ".tmp" })
                    try { File.Delete(Path.Combine(directory, name + suffix)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }
    public static void CleanOldSessions()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var directory in Directory.EnumerateDirectories(Root))
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-7)) CleanSession(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal sealed record UpdatePlan(string Target, string OldHash, string OldVersion, string NewHash, string NewVersion,
    long NewSize, int ParentPid, long ParentStartedTicks, string Nonce, string HelperHash);
internal sealed record UpdateSignal(string Nonce, int Pid, string Version);
internal sealed record UpdateOutcome(string Status, string Message, string Target, string Backup, DateTimeOffset At);
internal sealed record UpdateTicket(string Directory, string PlanPath, UpdatePlan Plan)
{
    public void Cancel() { try { File.WriteAllText(Path.Combine(Directory, "cancel"), "cancel"); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

/// <summary>Small updater mode of the existing single-file exe. Only replaces its one verified target.</summary>
internal static class UpdateInstaller
{
    private static string Incoming(UpdatePlan plan, string session) => plan.Target + ".incoming." + Path.GetFileName(session);
    private static string Backup(UpdatePlan plan, string session) => plan.Target + ".backup." + Path.GetFileName(session);
    private static string Failed(UpdatePlan plan, string session) => plan.Target + ".failed." + Path.GetFileName(session);
    public static UpdateOutcome? LastOutcome() => UpdateFiles.ReadJson<UpdateOutcome>(Path.Combine(UpdateFiles.Root, "last-result.json"));

    public static UpdateTicket Prepare(UpdateDownload download, string target, Process parent, string helperSource)
    {
        UpdateFiles.ValidateSession(download.Directory);
        target = Path.GetFullPath(target);
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || target.StartsWith(UpdateFiles.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("当前运行目录不能用作更新目标，请手动下载更新。");
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("程序是文件链接，请手动下载更新。");
        var oldInfo = FileVersionInfo.GetVersionInfo(target);
        var oldVersion = new Version(oldInfo.FileMajorPart, oldInfo.FileMinorPart, oldInfo.FileBuildPart, oldInfo.FilePrivatePart);
        UpdateFiles.VerifyExecutable(target, oldVersion);
        if (download.Offer.Version <= oldVersion) throw new InvalidDataException("新版版本号不高于当前程序，未执行替换。");
        if (new FileInfo(download.Payload).Length != download.Offer.Size || UpdateFiles.Hash(download.Payload) != download.Sha256)
            throw new InvalidDataException("已下载文件发生变化，请重新下载。");
        UpdateFiles.VerifyExecutable(download.Payload, download.Offer.Version);
        var probe = target + ".write-test." + Path.GetFileName(download.Directory);
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0); stream.Flush(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new IOException("程序所在目录无法写入，请打开下载页面手动替换，或将程序移到可写目录。", ex); }
        var helper = Path.Combine(download.Directory, "updater.exe");
        File.Copy(helperSource, helper, true);
        // Copying succeeds only after a previous helper has released its executable.
        // A retry must not inherit that attempt's cancellation or startup signals.
        foreach (var name in new[] { "cancel", "ready.json", "healthy.json", "result.json" })
            File.Delete(Path.Combine(download.Directory, name));
        var plan = new UpdatePlan(target, UpdateFiles.Hash(target), oldVersion.ToString(4), download.Sha256,
            download.Offer.Version.ToString(4), download.Offer.Size, parent.Id, parent.StartTime.ToUniversalTime().Ticks,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), UpdateFiles.Hash(helper));
        var planPath = Path.Combine(download.Directory, "plan.json"); UpdateFiles.WriteJson(planPath, plan);
        return new(download.Directory, planPath, plan);
    }

    public static async Task StartHelperAsync(UpdateTicket ticket, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(Path.Combine(ticket.Directory, "updater.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = ticket.Directory };
        info.ArgumentList.Add("--apply-update"); info.ArgumentList.Add(ticket.PlanPath); info.ArgumentList.Add(ticket.Plan.Nonce);
        using var helper = Process.Start(info) ?? throw new IOException("无法启动更新助手。");
        try
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                cancel.ThrowIfCancellationRequested();
                var ready = UpdateFiles.ReadJson<UpdateSignal>(Path.Combine(ticket.Directory, "ready.json"));
                if (ready?.Nonce == ticket.Plan.Nonce && ready.Pid == helper.Id) return;
                if (helper.HasExited) throw new IOException("更新助手未能准备就绪，旧版程序仍在运行。");
                await Task.Delay(100, cancel);
            }
            throw new TimeoutException("更新助手准备超时，旧版程序仍在运行。");
        }
        catch { ticket.Cancel(); throw; }
    }

    private static UpdatePlan ReadPlan(string planPath, string nonce, bool requireHelper)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(planPath))!; UpdateFiles.ValidateSession(directory);
        if (Path.GetFileName(planPath) != "plan.json") throw new InvalidDataException("更新计划路径无效。");
        var plan = UpdateFiles.ReadJson<UpdatePlan>(planPath) ?? throw new InvalidDataException("更新计划不可读。");
        if (new[] { plan.Target, plan.OldHash, plan.OldVersion, plan.NewHash, plan.NewVersion, plan.Nonce, plan.HelperHash }.Any(string.IsNullOrEmpty))
            throw new InvalidDataException("更新计划字段不完整。");
        if (nonce.Length != 64 || plan.Nonce != nonce || plan.ParentPid <= 0 || plan.NewSize <= 0 || plan.NewSize > AppUpdate.MaximumSize)
            throw new InvalidDataException("更新计划验证失败。");
        if (Path.GetFullPath(plan.Target) != plan.Target || !plan.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            plan.Target.StartsWith(UpdateFiles.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新目标不符合要求。");
        foreach (var hash in new[] { plan.OldHash, plan.NewHash, plan.HelperHash })
            if (!System.Text.RegularExpressions.Regex.IsMatch(hash, "^[a-f0-9]{64}$")) throw new InvalidDataException("更新校验值无效。");
        if (AppUpdate.ParseVersion(plan.NewVersion) <= AppUpdate.ParseVersion(plan.OldVersion)) throw new InvalidDataException("更新版本不能回退。");
        if (requireHelper && (!string.Equals(Environment.ProcessPath, Path.Combine(directory, "updater.exe"), StringComparison.OrdinalIgnoreCase) ||
            UpdateFiles.Hash(Environment.ProcessPath!) != plan.HelperHash)) throw new InvalidDataException("更新助手身份不匹配。");
        return plan;
    }

    public static async Task<int> RunWorkerAsync(string planPath, string nonce)
    {
        string? directory = null; UpdatePlan? plan = null; var parentExited = false;
        try
        {
            plan = ReadPlan(planPath, nonce, true); directory = Path.GetDirectoryName(planPath)!;
            var lockName = "target-" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plan.Target.ToUpperInvariant()))) + ".lock";
            using var targetLock = new FileStream(Path.Combine(UpdateFiles.Root, lockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var parent = Process.GetProcessById(plan.ParentPid);
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartedTicks ||
                !string.Equals(parent.MainModule?.FileName, plan.Target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原程序进程与更新目标不匹配。");
            UpdateFiles.WriteJson(Path.Combine(directory, "ready.json"), new UpdateSignal(nonce, Environment.ProcessId, plan.NewVersion));
            var watch = Stopwatch.StartNew();
            while (!parent.HasExited)
            {
                if (File.Exists(Path.Combine(directory, "cancel"))) return 2;
                if (watch.Elapsed > TimeSpan.FromMinutes(2)) throw new TimeoutException("原程序未退出，更新已取消。");
                await Task.Delay(100);
            }
            if (File.Exists(Path.Combine(directory, "cancel"))) return 2;
            parentExited = true;
            Process? launched = null;
            try
            {
                await ApplyAsync(plan, directory, async () =>
                {
                    var info = new ProcessStartInfo(plan.Target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan.Target)! };
                    info.ArgumentList.Add("--updated-session"); info.ArgumentList.Add(directory); info.ArgumentList.Add(plan.Nonce);
                    launched = Process.Start(info) ?? throw new IOException("无法启动新版程序。");
                    var start = Stopwatch.StartNew();
                    while (start.Elapsed < TimeSpan.FromSeconds(60))
                    {
                        var signal = UpdateFiles.ReadJson<UpdateSignal>(Path.Combine(directory, "healthy.json"));
                        if (signal?.Nonce == plan.Nonce && signal.Pid == launched.Id && signal.Version == plan.NewVersion && !launched.HasExited) return true;
                        if (launched.HasExited) return false;
                        await Task.Delay(150);
                    }
                    if (!launched.HasExited) { launched.Kill(); await launched.WaitForExitAsync(); }
                    return false;
                }, () =>
                {
                    try { Process.Start(new ProcessStartInfo(plan.Target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan.Target)! })?.Dispose(); }
                    catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { }
                });
            }
            finally { launched?.Dispose(); }
            return 0;
        }
        catch (Exception ex)
        {
            if (directory != null && plan != null) SaveOutcome(directory, new("failed", "更新未完成：" + ex.Message, plan.Target, Backup(plan, directory), DateTimeOffset.Now));
            if (parentExited && plan != null)
            {
                try
                {
                    if (UpdateFiles.Hash(plan.Target) == plan.OldHash)
                        Process.Start(new ProcessStartInfo(plan.Target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan.Target)! })?.Dispose();
                }
                catch (Exception restart) when (restart is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            }
            return 1;
        }
    }

    /// <summary>Transaction boundary is independent of UI/process launching so failure paths can be exercised.</summary>
    internal static async Task ApplyAsync(UpdatePlan plan, string directory, Func<Task<bool>> launchAndConfirm, Action restartOld)
    {
        UpdateFiles.ValidateSession(directory);
        var payload = Path.Combine(directory, "payload.exe");
        var incoming = Incoming(plan, directory); var backup = Backup(plan, directory);
        if ((File.GetAttributes(plan.Target) & FileAttributes.ReparsePoint) != 0 || UpdateFiles.Hash(plan.Target) != plan.OldHash)
            throw new InvalidDataException("原程序已发生变化，更新已停止。");
        if (new FileInfo(payload).Length != plan.NewSize || UpdateFiles.Hash(payload) != plan.NewHash)
            throw new InvalidDataException("新版文件校验失败，未修改原程序。");
        UpdateFiles.VerifyExecutable(payload, AppUpdate.ParseVersion(plan.NewVersion));
        var previous = LastOutcome(); var replaced = false;
        try
        {
            using (var source = File.OpenRead(payload))
            using (var destination = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { source.CopyTo(destination); destination.Flush(true); }
            if (UpdateFiles.Hash(incoming) != plan.NewHash) throw new InvalidDataException("写入后的文件校验失败。");
            File.Replace(incoming, plan.Target, backup, true); replaced = true;
            if (!await launchAndConfirm()) throw new IOException("新版程序未能正常启动。");
            SaveOutcome(directory, new("success", "已更新至 v" + AppUpdate.DisplayVersion(AppUpdate.ParseVersion(plan.NewVersion)), plan.Target, backup, DateTimeOffset.Now));
            if (previous?.Target == plan.Target && previous.Backup != backup && IsOwnedBackup(previous.Backup, plan.Target))
                try { File.Delete(previous.Backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception error)
        {
            try
            {
                if (replaced)
                {
                    if (UpdateFiles.Hash(backup) != plan.OldHash) throw new IOException("旧版备份校验失败，请手动恢复备份。");
                    File.Replace(backup, plan.Target, Failed(plan, directory), true);
                    SaveOutcome(directory, new("rolledback", "更新失败，已恢复旧版：" + error.Message, plan.Target, "", DateTimeOffset.Now));
                }
                else SaveOutcome(directory, new("failed", "更新失败，旧版未改变：" + error.Message, plan.Target, "", DateTimeOffset.Now));
                restartOld();
            }
            catch (Exception recovery)
            { SaveOutcome(directory, new("failed", "自动恢复未完成，请手动恢复备份：" + recovery.Message, plan.Target, backup, DateTimeOffset.Now)); }
        }
        finally { try { File.Delete(incoming); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private static bool IsOwnedBackup(string path, string target) => !string.IsNullOrEmpty(path) && path.StartsWith(target + ".backup.", StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(path[(target.Length + ".backup.".Length)..], "N", out _);
    private static void SaveOutcome(string directory, UpdateOutcome result)
    {
        foreach (var path in new[] { Path.Combine(directory, "result.json"), Path.Combine(UpdateFiles.Root, "last-result.json") })
            try { UpdateFiles.WriteJson(path, result); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public static bool AcknowledgeStartup(string[] args)
    {
        try
        {
            if (args.Length != 3 || args[0] != "--updated-session") return false;
            var plan = ReadPlan(Path.Combine(args[1], "plan.json"), args[2], false);
            if (!string.Equals(Environment.ProcessPath, plan.Target, StringComparison.OrdinalIgnoreCase) ||
                AppUpdate.CurrentVersion != AppUpdate.ParseVersion(plan.NewVersion)) return false;
            UpdateFiles.WriteJson(Path.Combine(args[1], "healthy.json"), new UpdateSignal(plan.Nonce, Environment.ProcessId, plan.NewVersion));
            return true;
        }
        catch (Exception) { return false; }
    }
}
