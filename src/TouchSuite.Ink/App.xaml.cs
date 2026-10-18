using System.IO;
using System.Windows;

namespace TouchSuite.Ink;

public partial class App : Application
{
    /// <summary>--selftest：启动后自动推一条测试笔画然后退出，用来验证 DComp 通路（不碰真实输入）。</summary>
    public static bool SelfTest { get; private set; }

    /// <summary>--input=wpf|raw|pointer|combo：启动时用哪个输入源（默认 wpf，保持原行为）。</summary>
    internal static InkInputMode StartupInput { get; private set; } = InkInputMode.Wpf;

    /// <summary>--inputprobe：只测 RawInput 注册与收帧（不算绘制），5 秒后打印直方图并退出。</summary>
    public static bool InputProbe { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        SelfTest = e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));
        InputProbe = e.Args.Any(a => string.Equals(a, "--inputprobe", StringComparison.OrdinalIgnoreCase));
        foreach (string a in e.Args)
        {
            const string prefix = "--input=";
            if (!a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            StartupInput = a[prefix.Length..].ToLowerInvariant() switch
            {
                "raw" or "rawhid" or "rawinput" => InkInputMode.RawHid,
                "pointer" or "wmpointer" or "wm_pointer" => InkInputMode.WmPointer,
                "combo" => InkInputMode.Combo,
                _ => InkInputMode.Wpf,
            };
        }

        Log.Write($"===== TouchSuite.Ink 启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        Log.Write($"系统: {Environment.OSVersion}  .NET {Environment.Version}  64位={Environment.Is64BitProcess}");
        Log.Write($"参数: [{string.Join(' ', e.Args)}]  selftest={SelfTest} input={InkInputModes.Label(StartupInput)} inputprobe={InputProbe}");
        Log.Write($"日志文件: {Log.FilePath}");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"未处理异常：{args.Exception}");
            args.Handled = true;
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Write("===== 退出 =====");
        base.OnExit(e);
    }
}
