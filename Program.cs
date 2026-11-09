// ============================================================================
// Program.cs — 程序入口
//
// 这里只有三件事：应用 WinForms 全局配置（ApplicationConfiguration.Initialize 会把
// csproj 里的 HighDpiMode=PerMonitorV2、视觉样式等应用上）、开主窗口、进消息循环。
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
        private static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MapForm());
        }
    }
}
