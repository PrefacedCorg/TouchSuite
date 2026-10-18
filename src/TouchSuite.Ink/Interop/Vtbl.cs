using System.Runtime.InteropServices;

namespace TouchSuite.Ink;

/// <summary>
/// COM 槽位直调。
///
/// <para>有些接口（例如 IDCompositionVisual）的 IID 没法在这里核对，用 <c>[ComImport]</c> 声明时
/// 返回对象的 QueryInterface 会直接 E_NOINTERFACE 而拿不到接口。这时就退回最底层的做法：
/// 只拿 IUnknown 裸指针，按 vtable 下标直接取函数指针调用 —— 只依赖「方法在 vtable 里的序号」，
/// 而这个序号是稳定且可以对照头文件数出来的。</para>
///
/// <para>槽位编号规则：IUnknown 占 0/1/2，之后每个接口的方法按声明顺序依次排下去；
/// 派生接口的方法排在基接口所有方法之后。所以只要数到你要调用的那个方法为止。</para>
/// </summary>
internal static class Vtbl
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int D0(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int D1(IntPtr self, IntPtr a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DWithGuidRef(IntPtr self, IntPtr updateRect, ref Guid iid, out IntPtr updateObject, out Gfx.POINT offset);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int CreateBrush(IntPtr self, ref Gfx.D2D1_COLOR_F color, IntPtr props, out IntPtr brush);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int FillEllipse(IntPtr self, ref Gfx.D2D1_ELLIPSE ellipse, IntPtr brush);

    /// <summary>IUnknown::QueryInterface（槽位 0）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int QueryInterface(IntPtr self, ref Guid iid, out IntPtr ppv);

    /// <summary>ID3D11DeviceContext::UpdateSubresource（返回 void）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void UpdateSubresource(
        IntPtr self, IntPtr dstResource, uint dstSubresource, IntPtr pDstBox, IntPtr pSrcData, uint srcRowPitch, uint srcDepthPitch);

    /// <summary>无参、返回 void 的方法（例如 ID3D11DeviceContext::Flush）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void V0(IntPtr self);

    /// <summary>IUnknown::Release（槽位 2）。</summary>
    public static void Release(IntPtr punk)
    {
        if (punk == IntPtr.Zero)
            return;
        D0 release = Slot<D0>(punk, 2);
        release(punk);
    }

    /// <summary>取 vtable 第 index 个槽位（0 = QueryInterface，1 = AddRef，2 = Release）并转成委托。</summary>
    public static T Slot<T>(IntPtr punk, int index) where T : Delegate
    {
        if (punk == IntPtr.Zero)
            throw new ArgumentNullException(nameof(punk));
        IntPtr vtbl = Marshal.ReadIntPtr(punk);
        IntPtr fn = Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
        if (fn == IntPtr.Zero)
            throw new InvalidOperationException($"vtable 槽位 {index} 为空");
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }
}
