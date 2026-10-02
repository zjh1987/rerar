using System;
using System.Reflection;

namespace Rerar
{
    // 程序入口：GUI / CLI 双模式分发。
    // 本任务只建立 CLI 骨架；完整 CLI 参数与界面由后续任务接入。
    internal static class Program
    {
        // CLI 契约：--cli --selftest 打印 version=<版本号> 并以 0 退出。
        private static int Main(string[] args)
        {
            bool cli = Array.IndexOf(args, "--cli") >= 0;
            bool selftest = Array.IndexOf(args, "--selftest") >= 0;

            if (cli && selftest)
            {
                Console.WriteLine("version=" + Assembly.GetExecutingAssembly().GetName().Version);
            }

            // 无参数（/target:winexe 下的常态）暂直接返回 0。
            return 0;
        }
    }
}
