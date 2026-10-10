using System.Windows;
using System.Windows.Threading;

namespace TouchSuite.App;

public partial class App : Application
{
    public App()
    {
        Log.Init();
        Log.Info("向导启动");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            if (a.ExceptionObject is Exception ex)
                Log.Error("未处理异常（AppDomain）", ex);
            else
                Log.Error("未处理异常（AppDomain）: " + (a.ExceptionObject?.ToString() ?? ""));
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("向导退出");
        Log.Shutdown();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("未处理异常（UI 线程）", e.Exception);
        MessageBox.Show(e.Exception.ToString(), "TouchSuite 校准向导 出错了", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
