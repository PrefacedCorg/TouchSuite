using System.Windows;
using System.Windows.Threading;
using TouchErase.Helpers;

namespace TouchErase;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Init();
        Log.Info("应用启动");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("应用退出");
        Log.Shutdown();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("未处理异常:\r\n" + e.Exception);
        MessageBox.Show(e.Exception.ToString(), "TouchErase 出错了", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
