using System.IO;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace FishWord.App;

public partial class App : Application
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FishWord", "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常捕获 + 运行日志，便于排查闪退/崩溃
        DispatcherUnhandledException += (_, args) =>
        {
            WriteLog("UI 线程未处理异常", args.Exception);
            args.Handled = true; // 阻止闪退：该次操作失败，应用继续运行（日志已记录）
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteLog("非 UI 线程未处理异常", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteLog("未观察的任务异常", args.Exception);
            args.SetObserved();
        };
    }

    private static void WriteLog(string category, Exception? ex)
    {
        if (ex is null)
            return;

        try
        {
            Directory.CreateDirectory(LogDir);
            string path = Path.Combine(LogDir, "error.log");
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{category}]{Environment.NewLine}{ex}{Environment.NewLine}";
            File.AppendAllText(path, line);
        }
        catch
        {
            // 写日志本身失败则忽略，避免二次崩溃
        }
    }
}
