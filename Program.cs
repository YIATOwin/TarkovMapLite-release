// ============================================================================
// Program.cs — 程序入口
//
// 更新助手模式先于界面初始化运行；正常启动应用 WinForms 全局配置、开主窗口、进消息循环。
// 更新后的主窗口显示后发送启动确认，助手据此保留新版或恢复旧版。
//
// 真正的逻辑全在 MapForm。这个文件平时不用改。
// 想加崩溃日志/单实例检查，就在这里 Initialize 前后挂钩子：
//   Application.ThreadException            UI 线程未处理异常
//   AppDomain.CurrentDomain.UnhandledException  其它线程
//   Mutex                                   防重复启动（现在没做）
// ============================================================================
using System;
using System.Windows.Forms;

namespace TarkovMapLite
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--apply-update")
                return args.Length == 3 ? UpdateInstaller.RunWorkerAsync(args[1], args[2]).GetAwaiter().GetResult() : 1;
            ApplicationConfiguration.Initialize();
            using var form = new MapForm();
            form.Shown += (_, _) => form.BeginInvoke(() => { if (UpdateInstaller.AcknowledgeStartup(args)) form.SoftwareUpdateBooted(); });
            Application.Run(form);
            return 0;
        }
    }
}
