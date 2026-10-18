using System.Runtime.InteropServices;

namespace TouchSuite.Ink;

/// <summary>
/// D3D11 + Direct2D + DirectComposition 的最小 interop 集合。
///
/// <para><b>槽位怎么来的</b>：去把官方头文件（winsdk d2d1.h / d2d1_1.h / dcomp.h）取下来逐行数出来的，
/// 不是凭记忆。行业通用的两条规则：① vtable 里基接口的方法一定排在前（COM 不变量）；
/// ② **同名重载各占一个独立槽位**。所以 ID2D1RenderTarget 的方法从槽位 4 开始
/// （0/1/2 = IUnknown，3 = ID2D1Resource::GetFactory）。</para>
///
/// <para>本文件只保留真正调用到的接口。其余接口（IDCompositionVisual / Target / Surface、
/// ID2D1RenderTarget）走裸指针 + vtable 槽位调用，见 <see cref="Vtbl"/> —— 因为它们的 IID
/// 实测拿不到（QI 直接 E_NOINTERFACE），而槽位下标是稳定可核对的。</para>
/// </summary>
internal static class Gfx
{
    // ================= D3D11 =================

    public const int D3D_DRIVER_TYPE_HARDWARE = 1;
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    public const uint D3D11_SDK_VERSION = 7;

    public static readonly int[] FeatureLevels =
    {
        0xB100, // 11_1
        0xB000, // 11_0
        0xA100, // 10_1
    };

    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int D3D11CreateDevice(
        IntPtr adapter, int driverType, IntPtr software, uint flags,
        int[]? featureLevels, uint featureLevelCount, uint sdkVersion,
        out ID3D11Device device, out int featureLevel, out IntPtr deviceContext);

    /// <summary>只用来 QI 出 IDXGIDevice，不调用它的任何方法 → 空接口是安全的。</summary>
    [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ID3D11Device { }

    /// <summary>同上，只作为 DCompositionCreateDevice 的入参（QI 已验证可用）。</summary>
    [ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIDevice { }

    // ================= DXGI / D3D11 资源 IID（取自 dxgi.h / d3d11.h 原文）=================

    /// <summary>dxgi.h：IID_IDXGISurface。BeginDraw 传它就能换出一块可写的 DXGI 表面。</summary>
    public static readonly Guid IID_IDXGISurface = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");

    /// <summary>d3d11.h：IID_ID3D11Texture2D。上面那块 DXGI 表面 QI 出来的纹理，可直接喂 UpdateSubresource。</summary>
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_BOX
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    /// <summary>
    /// ID3D11DeviceContext 槽位表（按 d3d11.h 顺序数出来；基接口 ID3D11DeviceChild 占 3..6，
    /// 所以自身方法从 7 起，该接口无重载，顺排即可）。目前只用到 UpdateSubresource 与 Flush。
    /// </summary>
    public static class Ctx
    {
        public const int UpdateSubresource = 48;   // 注意返回 void，不是 HRESULT
        public const int Flush = 111;              // 也返回 void
    }

    // ================= D2D1 结构 =================

    [StructLayout(LayoutKind.Sequential)]
    public struct D2D1_COLOR_F
    {
        public float R, G, B, A;
        public D2D1_COLOR_F(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D2D1_POINT_2F
    {
        public float X, Y;
        public D2D1_POINT_2F(float x, float y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D2D1_ELLIPSE
    {
        public D2D1_POINT_2F Point;
        public float RadiusX, RadiusY;
        public D2D1_ELLIPSE(float cx, float cy, float rx, float ry)
        {
            Point = new D2D1_POINT_2F(cx, cy);
            RadiusX = rx;
            RadiusY = ry;
        }
    }

    // ================= D2D1 IID（取自 d2d1.h / d2d1_1.h 原文）=================

    // 注意后缀是 001143a055f9，不是 001e4f142187（后者是 DirectWrite 那一族的）。
    public static readonly Guid IID_ID2D1RenderTarget = new("2cd90694-12e2-11dc-9fed-001143a055f9");
    public static readonly Guid IID_ID2D1Resource = new("2cd90691-12e2-11dc-9fed-001143a055f9");
    public static readonly Guid IID_ID2D1SolidColorBrush = new("2cd906a9-12e2-11dc-9fed-001143a055f9");
    public static readonly Guid IID_ID2D1DeviceContext = new("e8f7fe7a-191c-466d-ad95-975678bda998");
    public static readonly Guid IID_ID2D1Device = new("47dd575d-ac05-4cdd-8049-9b02cd16f44c");
    public static readonly Guid IID_ID2D1Factory1 = new("bb12d362-daee-4b9a-aa1d-14ba401cfa1f");

    /// <summary>
    /// ID2D1RenderTarget 的槽位表（按 d2d1.h 顺序数出来；自身方法从 4 开始）。
    /// 只列到会用到的为止。⚠ 改这个表前先重新数头文件，重载各占一个槽位。
    /// </summary>
    public static class Rt
    {
        public const int CreateBitmap = 4;
        public const int CreateBitmapFromWicBitmap = 5;
        public const int CreateSharedBitmap = 6;
        public const int CreateBitmapBrush = 7;
        public const int CreateSolidColorBrush = 8;
        public const int CreateGradientStopCollection = 9;
        public const int CreateLinearGradientBrush = 10;
        public const int CreateRadialGradientBrush = 11;
        public const int CreateCompatibleRenderTarget = 12;
        public const int CreateLayer = 13;
        public const int CreateMesh = 14;
        public const int DrawLine = 15;
        public const int DrawRectangle = 16;
        public const int FillRectangle = 17;
        public const int DrawRoundedRectangle = 18;
        public const int FillRoundedRectangle = 19;
        public const int DrawEllipse = 20;
        public const int FillEllipse = 21;
        public const int DrawGeometry = 22;
        public const int FillGeometry = 23;
        public const int SetTransform = 30;
        public const int Clear = 47;
        public const int BeginDraw = 48;
        public const int EndDraw = 49;
    }

    public static readonly int SlotRtCreateSolidColorBrush = Rt.CreateSolidColorBrush;
    public static readonly int SlotRtFillEllipse = Rt.FillEllipse;
    public static readonly int SlotRtSetTransform = Rt.SetTransform;

    /// <summary>
    /// BeginDraw 必须显式传 IID，而且传哪个 IID 直接决定后面走哪条绘制通路：
    /// IID_IDXGISurface → 换出的是可写 DXGI 表面，用 D3D11 上传（首选，低延迟）；
    /// IID_ID2D1DeviceContext / IID_ID2D1RenderTarget → 拿到 D2D 渲染目标，走印章式绘制（备选）。
    /// 按顺序试，命中即定通路。
    /// </summary>
    public static readonly (Guid Iid, bool IsD3D)[] BeginDrawCandidates =
    {
        (IID_IDXGISurface, true),
        (IID_ID2D1DeviceContext, false),
        (IID_ID2D1RenderTarget, false),
    };

    // ================= DirectComposition =================

    public const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    public const int DXGI_ALPHA_MODE_PREMULTIPLIED = 1;

    public static readonly Guid IID_IDCompositionDevice = new("c37ea93a-e7aa-450d-b16f-9746cb0407f3");

    [DllImport("dcomp.dll", PreserveSig = true)]
    public static extern int DCompositionCreateDevice(IDXGIDevice dxgiDevice, ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object device);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    }

    /// <summary>
    /// IDCompositionDevice：只声明到 CreateSurface 为止（Commit=1 … CreateSurface=6）。
    /// Create* 的 out 参数故意声明成 IntPtr，后续用槽位调用，避开那三个接口的 IID 问题。
    /// </summary>
    [ComImport, Guid("c37ea93a-e7aa-450d-b16f-9746cb0407f3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDCompositionDevice
    {
        [PreserveSig] int Commit();                                                              // 1
        [PreserveSig] int WaitForCommitCompletion();                                             // 2
        [PreserveSig] int _GetFrameStatistics(IntPtr statistics);                                // 3
        [PreserveSig] int CreateTargetForHwnd(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target); // 4
        [PreserveSig] int CreateVisual(out IntPtr visual);                                       // 5
        [PreserveSig] int CreateSurface(uint width, uint height, int pixelFormat, int alphaMode, out IntPtr surface); // 6
    }

    // DComp 侧槽位（0/1/2 = IUnknown）：
    //   IDCompositionTarget（只有 SetRoot 一个方法）→ 3
    //   IDCompositionVisual 按重载逐个数：SetOffsetX(anim)=3, SetOffsetX(float)=4, SetOffsetY(anim)=5,
    //     SetOffsetY(float)=6, SetTransform(matrix)=7, SetTransform(transform)=8, SetTransformParent=9,
    //     SetEffect=10, SetBitmapInterpolationMode=11, SetBorderMode=12, SetClip(rect)=13, SetClip(clip)=14,
    //     SetContent=15, AddVisual=16 …
    //   IDCompositionSurface（5 个方法，无重载）→ BeginDraw=3, EndDraw=4
    public const int SlotTargetSetRoot = 3;
    public const int SlotVisualSetContent = 15;
    public const int SlotSurfaceBeginDraw = 3;
    public const int SlotSurfaceEndDraw = 4;
}
