// ---------------------------------------------------------------------------
// net462 兼容层 —— 只在 .NET Framework 目标下生效（与 TouchSuite.HidDump 同一套做法）。
//
// net462 缺两样东西，这里补齐：
//   1) 编译器要求的标记类型：record 的 IsExternalInit 等 —— Roslyn 认"用户自己定义的"
//      同名类型，无需运行时支持；
//   2) .NET Core 才有的少量 API：Environment.TickCount64、Math.Clamp。
// ---------------------------------------------------------------------------
#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    /// <summary>编译 record / init 访问器所需（net462 无此类型）。</summary>
    internal static class IsExternalInit { }
}
#endif

namespace TouchSuite.HidTouchBridge
{
    /// <summary>net462 上缺失的少量 API（.NET Core 独有）的替代实现。</summary>
    internal static class Compat
    {
        private static readonly System.Diagnostics.Stopwatch Sw = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>单调递增毫秒数 —— 替代 <c>Environment.TickCount64</c>（net462 只有会回绕的 int TickCount）。</summary>
        public static long NowMs() => Sw.ElapsedMilliseconds;

        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
