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

    /// <summary>
    /// ID2D1RenderTarget::DrawLine（返回 void）。strokeStyle 传 NULL = 平头端点。
    /// <para>用它画笔迹主体而不是盖一串圆：一条线只被"覆盖一次"，抗锯齿边缘的覆盖率是正确的；
    /// 盖圆时相邻圆的重叠会把边缘的半覆盖像素反复合成成接近全黑，边缘梯度塌陷成台阶（锯齿）。</para>
    /// <para>参数按 x64 ABI：D2D1_POINT_2F 是 8 字节 POD → 走**整数**寄存器，所以这里收两个 ulong
    /// （低 32 位 X、高 32 位 Y）；strokeStyle 必须显式传 NULL，否则 D2D 会去读调用者的影子空间。</para>
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void DrawLine(
        IntPtr self, ulong p0, ulong p1, IntPtr brush, float strokeWidth, IntPtr strokeStyle);

    /// <summary>两个 float 打包成一个 64 位整数（低 32 位 = X）。</summary>
    public static ulong PackPoint(float x, float y) =>
        BitConverter.SingleToUInt32Bits(x) | ((ulong)BitConverter.SingleToUInt32Bits(y) << 32);

    /// <summary>ID2D1Factory::CreateStrokeStyle（返回 HRESULT）。用来做圆头/圆角端点，否则相邻段会留缺口。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int FactoryCreateStrokeStyle(
        IntPtr self, ref Gfx.D2D1_STROKE_STYLE_PROPERTIES props, IntPtr dashes, uint dashesCount,
        out IntPtr strokeStyle);

    /// <summary>ID2D1RenderTarget::SetTransform（返回 void）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void SetTransform(IntPtr self, ref Gfx.D2D1_MATRIX_3X2_F transform);

    /// <summary>ID2D1RenderTarget::SetDpi（返回 void）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void SetDpi(IntPtr self, float dpiX, float dpiY);

    /// <summary>ID2D1RenderTarget::PushAxisAlignedClip（返回 void）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void PushClip(IntPtr self, ref Gfx.D2D1_RECT_F clipRect, int antialiasMode);

    /// <summary>ID2D1RenderTarget::Clear（返回 void）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Clear(IntPtr self, ref Gfx.D2D1_COLOR_F color);

    /// <summary>ID2D1Factory1::CreateDevice（返回 HRESULT）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int FactoryCreateDevice(IntPtr self, IntPtr dxgiDevice, out IntPtr d2dDevice);

    /// <summary>IUnknown::QueryInterface（槽位 0）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int QueryInterface(IntPtr self, ref Guid iid, out IntPtr ppv);

    /// <summary>无参、返回 void 的方法（例如 ID2D1RenderTarget::PopAxisAlignedClip）。</summary>
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
