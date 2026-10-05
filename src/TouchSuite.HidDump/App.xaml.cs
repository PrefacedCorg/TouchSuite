using System.Windows;

namespace TouchSuite.HidDump;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Init();   // 必须在主窗口构造前（主窗口初始化期也可能出异常）
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("未处理的 UI 异常", args.Exception);
            args.Handled = true;   // 记完日志不崩，便于继续观察
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("未处理的域异常", args.ExceptionObject as Exception);
        base.OnStartup(e);
    }
}
