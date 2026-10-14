using Microsoft.Win32;

namespace TouchSuite.HidTouchBridge;

/// <summary>
/// 经典 WISP 触摸开关：<c>HKCU\Software\Microsoft\Wisp\Touch\TouchGate</c>（DWORD，32 位）。
/// 值 <c>0</c> = 关闭「原有的触摸屏功能」，<c>1</c>（默认）= 打开。
/// <para>
/// 关闭它之后，本机那块不符合 Windows 规范的触摸屏不再产生触摸输入，
/// 于是可以只保留本程序用 user32 合成指针注入的、符合规范的那一路触摸。
/// </para>
/// </summary>
internal static class TouchGate
{
    private const string KeyPath = @"Software\Microsoft\Wisp\Touch";
    private const string ValueName = "TouchGate";

    /// <summary>读当前值；键/值不存在返回 null。</summary>
    public static int? Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
        object? value = key?.GetValue(ValueName);
        return value is int i ? i : null;
    }

    /// <summary>写值（自动新建键），强制写入 DWORD32。</summary>
    public static void Write(int value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException($"无法打开注册表键 HKCU\\{KeyPath}");
        key.SetValue(ValueName, value, RegistryValueKind.DWord);
    }

    /// <summary>把触摸关掉（TouchGate=0）。返回原值（供退出时恢复）。</summary>
    public static int? Disable()
    {
        int? old = Read();
        Write(0);
        return old;
    }

    /// <summary>恢复原值；原值不存在则删除本程序写入的值。</summary>
    public static void Restore(int? old)
    {
        if (old is int v)
        {
            Write(v);
        }
        else
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
