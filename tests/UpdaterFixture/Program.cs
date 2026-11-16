using System.Reflection;
using System.Text.Json;

// Isolated noninteractive test application: never reads the real game's settings or logs.
var root = Environment.GetEnvironmentVariable("TARKOVMAPLITE_UPDATER_FIXTURE_ROOT");
if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 3;
if (args.Length == 2 && args[0] == "--hold")
{
    while (!File.Exists(args[1])) Thread.Sleep(50);
    return 0;
}
var version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(4);
if (args.Length == 3 && args[0] == "--updated-session")
{
    if (Environment.GetEnvironmentVariable("TARKOVMAPLITE_UPDATER_FIXTURE_MODE") == "exit") return 7;
    using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[1], "plan.json")));
    if (plan.RootElement.GetProperty("nonce").GetString() != args[2]) return 4;
    File.WriteAllText(Path.Combine(args[1], "healthy.json"), JsonSerializer.Serialize(new { nonce=args[2], pid=Environment.ProcessId, version }));
    File.WriteAllText(Path.Combine(root, "new-started.txt"), version);
    Thread.Sleep(2000);
    return 0;
}
File.WriteAllText(Path.Combine(root, "old-started.txt"), version);
return 0;
