// ---------------------------------------------------------------------------
// net462 兼容层 —— 只在 .NET Framework 目标下生效。
//
// 为了让工具能在 Windows 7/8.1/10 上不装 .NET 运行时就能跑（Win10 1607+ 自带
// .NET Framework 4.6.2；Win7/8.1 需要装一次 4.6.2 安装包），HidDump 目标改为 net462。
// 但 net462 缺两样东西，这里补齐：
//   1) 编译器要求的标记类型：record 的 IsExternalInit、required 的 RequiredMemberAttribute 等
//      —— Roslyn 认"用户自己定义的"同名类型，无需运行时支持；
//   2) .NET Core 才有的少量 API：Environment.TickCount64、Math.Clamp、数组 Range 切片。
// ---------------------------------------------------------------------------
#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    /// <summary>编译 record / init 访问器所需（net462 无此类型）。</summary>
    internal static class IsExternalInit { }

    /// <summary>编译 required 成员所需。</summary>
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    /// <summary>编译 required 成员所需（配合 RequiredMemberAttribute）。</summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);

        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;

        public string FeatureName { get; }
        public bool IsOptional { get; init; }
    }

    /// <summary>编译带 required 成员的构造函数所需。</summary>
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}
#endif

namespace TouchSuite.HidDump
{
    /// <summary>net462 上缺失的少量 API（.NET Core 独有）的替代实现。</summary>
    internal static class Compat
    {
        private static readonly System.Diagnostics.Stopwatch Sw = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>单调递增毫秒数 —— 替代 <c>Environment.TickCount64</c>（net462 只有会回绕的 int TickCount）。</summary>
        public static long NowMs() => Sw.ElapsedMilliseconds;

        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>取数组前 count 字节 —— 替代 <c>arr[..count]</c>（Range 切片 .NET Core 才有）。</summary>
        public static byte[] Head(byte[] src, int count)
        {
            var r = new byte[count];
            Array.Copy(src, r, count);
            return r;
        }
    }
}
