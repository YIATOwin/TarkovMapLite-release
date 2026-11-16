using System.Diagnostics;
using TarkovMapLite;

internal static class UpdateIntegrationChecks
{
    public static int Run(string updater, string oldFixture, string newFixture, string output)
    {
        var context = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
        try { return RunAsync(Path.GetFullPath(updater), Path.GetFullPath(oldFixture), Path.GetFullPath(newFixture), Path.GetFullPath(output)).GetAwaiter().GetResult(); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }
    private static async Task<int> RunAsync(string updater, string oldFixture, string newFixture, string output)
    {
        var checks=0;
        void Check(bool ok,string label) {if(!ok)throw new Exception(label);checks++;}
        var rootBefore=Environment.GetEnvironmentVariable("TARKOVMAPLITE_UPDATE_ROOT");
        var modeBefore=Environment.GetEnvironmentVariable("TARKOVMAPLITE_UPDATER_FIXTURE_MODE");
        var fixtureBefore=Environment.GetEnvironmentVariable("TARKOVMAPLITE_UPDATER_FIXTURE_ROOT");
        Directory.CreateDirectory(output);
        try
        {
            foreach(var mode in new[]{"healthy","exit","cancel"})
            {
                var directory=Path.Combine(output,mode+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
                var updateRoot=Path.Combine(directory,"cache");
                Environment.SetEnvironmentVariable("TARKOVMAPLITE_UPDATE_ROOT",updateRoot);
                Environment.SetEnvironmentVariable("TARKOVMAPLITE_UPDATER_FIXTURE_ROOT",directory);
                Environment.SetEnvironmentVariable("TARKOVMAPLITE_UPDATER_FIXTURE_MODE",mode);
                var target=Path.Combine(directory,"地图 app $ ' space.exe");File.Copy(oldFixture,target);
                var settings=Path.Combine(directory,"settings.json");File.WriteAllText(settings,"keep-user-preferences");
                var releaseParent=Path.Combine(directory,"release-parent");
                var parentInfo=new ProcessStartInfo(target){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=directory};
                parentInfo.ArgumentList.Add("--hold");parentInfo.ArgumentList.Add(releaseParent);
                using var parent=Process.Start(parentInfo)!;
                UpdateTicket? ticket=null;Process? helper=null;
                try
                {
                    var session=UpdateFiles.CreateSession();var payload=Path.Combine(session,"payload.exe");File.Copy(newFixture,payload);
                    var offer=new ReleaseOffer("v9.9.9",new Version(9,9,9,0),"integration",new Uri(AppUpdate.ReleasePage),new Uri("https://github.com/"+AppUpdate.Repository+"/releases/download/v9.9.9/TarkovMapLite.exe"),
                        new FileInfo(payload).Length,UpdateFiles.Hash(payload),null);
                    var download=new UpdateDownload(session,payload,offer,offer.Sha256!);
                    ticket=UpdateInstaller.Prepare(download,target,parent,updater);
                    await UpdateInstaller.StartHelperAsync(ticket,default);
                    var ready=UpdateFiles.ReadJson<UpdateSignal>(Path.Combine(session,"ready.json"))!;
                    helper=Process.GetProcessById(ready.Pid);
                    Check(!parent.HasExited && UpdateFiles.Hash(target)==ticket.Plan.OldHash,"Helper replaced a running parent");
                    if(mode=="cancel")
                    {
                        ticket.Cancel();await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                        Check(!parent.HasExited && UpdateFiles.Hash(target)==ticket.Plan.OldHash,"Cancelled helper changed the executable");
                        ticket=UpdateInstaller.Prepare(download,target,parent,updater);
                        Check(!File.Exists(Path.Combine(session,"cancel"))&&!File.Exists(Path.Combine(session,"ready.json")),"Retry inherited stale helper signals");
                        helper.Dispose();
                        await UpdateInstaller.StartHelperAsync(ticket,default);
                        helper=Process.GetProcessById(UpdateFiles.ReadJson<UpdateSignal>(Path.Combine(session,"ready.json"))!.Pid);
                        Check(!helper.HasExited&&!parent.HasExited,"Cancelled update could not be retried");
                        ticket.Cancel();await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                        File.WriteAllText(releaseParent,"");await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    else
                    {
                        File.WriteAllText(releaseParent,"");await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
                        var result=UpdateFiles.ReadJson<UpdateOutcome>(Path.Combine(session,"result.json"));
                        Check(result?.Status==(mode=="healthy"?"success":"rolledback"),"Worker did not finish the expected transaction: "+result?.Message);
                        Check(UpdateFiles.Hash(target)==(mode=="healthy"?ticket.Plan.NewHash:ticket.Plan.OldHash),"Worker left wrong executable bytes");
                        if(mode=="healthy")
                        {
                            Check(File.Exists(result!.Backup)&&UpdateFiles.Hash(result.Backup)==ticket.Plan.OldHash,"Successful install did not preserve previous version");
                            Check(File.ReadAllText(Path.Combine(directory,"new-started.txt"))=="9.9.9.0","Verified new process did not launch");
                        }
                        else
                        {
                            var watch=Stopwatch.StartNew();
                            while(!File.Exists(Path.Combine(directory,"old-started.txt"))&&watch.Elapsed<TimeSpan.FromSeconds(10))await Task.Delay(100);
                            Check(File.ReadAllText(Path.Combine(directory,"old-started.txt"))=="9.9.8.0","Rollback did not restart the original program");
                        }
                    }
                    Check(File.ReadAllText(settings)=="keep-user-preferences","Update touched unrelated user settings");
                }
                finally
                {
                    if(!parent.HasExited){File.WriteAllText(releaseParent,"");await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}
                    if(helper is{HasExited:false}){ticket?.Cancel();await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}
                    helper?.Dispose();
                }
                Console.WriteLine("PASS: real updater process "+mode+" (isolated fixture)");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TARKOVMAPLITE_UPDATE_ROOT",rootBefore);
            Environment.SetEnvironmentVariable("TARKOVMAPLITE_UPDATER_FIXTURE_MODE",modeBefore);
            Environment.SetEnvironmentVariable("TARKOVMAPLITE_UPDATER_FIXTURE_ROOT",fixtureBefore);
        }
        Console.WriteLine($"PASS: {checks} published updater parent/child handshake, replacement, launch rollback, cancellation, Unicode paths and settings checks.");
        return checks;
    }
}
