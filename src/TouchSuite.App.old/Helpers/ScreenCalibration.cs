using System.Runtime.InteropServices;

namespace TouchSuite.App.Old.Helpers;

/// <summary>EDID 物理尺寸的推算方式。</summary>
public enum EdidSizeMode
{
    /// <summary>横竖都按 EDID 原报值（可能两轴 mm/px 不等）。</summary>
    UseEdid,
    /// <summary>信宽度，按分辨率比例推高度（方形像素）。</summary>
    TrustWidth,
    /// <summary>信高度，按分辨率比例推宽度（方形像素）。</summary>
    TrustHeight,
}

/// <summary>
/// 屏幕物理尺寸校准结果。核心常量只有一个：屏幕物理毫米尺寸，
/// 其余（mm/px、mm/DIU）都由它推导。
/// </summary>
public sealed class ScreenCalibration
{
    public double ScreenWidthMm { get; init; }
    public double ScreenHeightMm { get; init; }
    public int ResX { get; init; }
    public int ResY { get; init; }

    /// <summary>物理像素 -> 毫米 的比例（X 方向）。</summary>
    public double MmPerPxX { get; init; }
    public double MmPerPxY { get; init; }

    /// <summary>数据来源描述（EDID / 手动对角线）。</summary>
    public string Source { get; init; } = "";

    /// <summary>对角线英寸（仅手动回退时有意义）。</summary>
    public double DiagonalInch { get; init; }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    public static (int x, int y) GetPrimaryScreenPixels()
        => (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));

    /// <summary>
    /// EDID 成功路径。按 <paramref name="mode"/> 决定如何得到物理宽高：
    /// 现代平板面板像素必为方形，若 EDID 物理比例与分辨率比例不符（虚拟机/远程会话常见），
    /// 可按宽度反推高度、或按高度反推宽度，保证像素方形。
    /// </summary>
    public static ScreenCalibration FromEdid(double widthMm, double heightMm,
        EdidSizeMode mode = EdidSizeMode.TrustWidth)
    {
        var (resX, resY) = GetPrimaryScreenPixels();
        if (resX <= 0) resX = 1920;
        if (resY <= 0) resY = 1080;

        double resAspect = (double)resX / resY;

        double w, h;
        switch (mode)
        {
            case EdidSizeMode.UseEdid:
                w = widthMm;
                h = heightMm;
                break;
            case EdidSizeMode.TrustHeight:
                h = heightMm;
                w = heightMm * resAspect;
                break;
            default: // TrustWidth
                w = widthMm;
                h = widthMm / resAspect;
                break;
        }

        string source = mode switch
        {
            EdidSizeMode.UseEdid => $"EDID 原样（报 {widthMm:0}x{heightMm:0}mm）",
            EdidSizeMode.TrustHeight => $"EDID 按竖推宽（{w:0}x{h:0}mm）",
            _ => $"EDID 按宽推竖（{w:0}x{h:0}mm）",
        };

        return new ScreenCalibration
        {
            ScreenWidthMm = w,
            ScreenHeightMm = h,
            ResX = resX,
            ResY = resY,
            MmPerPxX = w / resX,
            MmPerPxY = h / resY,
            Source = source,
        };
    }

    /// <summary>
    /// EDID 失败回退：用户填对角线英寸 + 宽高比反推物理宽高。
    /// </summary>
    public static ScreenCalibration FromDiagonal(double diagInch, double resX, double resY,
        double aspectW = 16, double aspectH = 9)
    {
        double diagMm = diagInch * 25.4;
        double k = Math.Sqrt(aspectW * aspectW + aspectH * aspectH);
        double wMm = diagMm * aspectW / k;
        double hMm = diagMm * aspectH / k;

        return new ScreenCalibration
        {
            ScreenWidthMm = wMm,
            ScreenHeightMm = hMm,
            ResX = (int)resX,
            ResY = (int)resY,
            MmPerPxX = wMm / resX,
            MmPerPxY = hMm / resY,
            Source = $"手动对角线 {diagInch:0.##}\"",
            DiagonalInch = diagInch,
        };
    }
}
