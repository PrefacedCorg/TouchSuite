# TouchErase · 手掌擦矫正 Demo

一个 WPF 桌面 Demo，用于验证"手掌擦"所需的三个量：**屏幕物理尺寸校准**、**手掌面积**、**手掌遮挡半径**，并额外提供一个**实时面积擦预览**（按多大、擦多大）。

核心难点不在算法，而在**没有可靠的"手掌物理面积"定值**，以及**接触尺寸能否从设备拿到**。本项目把相关通路全部打通并做成可诊断、可回退。

## 功能

三步主流程（对应界面右侧 ①②③）：

1. **屏幕物理尺寸校准**：读显示器 EDID 得到物理毫米尺寸；读不到可手填对角线英寸。所有物理换算（mm/px、mm/DIU）都由它推导。
2. **手掌面积**：在画布上描一圈手掌轮廓 → 去抖（Douglas-Peucker）→ 同时给出**描摹闭合曲线面积**与**凸包面积**（cm²），并给出两者比值用于判断轮廓凹凸。
3. **手掌遮挡半径**：采样触点 → 按距离聚类 → MST（Kruskal）切掉离群边 → 取触点最多的连通分量 → Welzl 最小覆盖圆，得到以 mm 为单位的遮挡半径，并按物理毫米实时渲染。

附带：

- **③ 面积擦预览**（独立页）：读驱动上报的接触尺寸，用「固定倍率 + 中值滤波」实时画出与接触面积等大的擦除区（默认矩形，可切正圆/等面积）。
- **引导模式**：启动自动进入分步引导，高亮当前要操作的控件；右上角「引导」可随时重看。
- **HID 触摸诊断**：列出每个触摸设备是否声明接触尺寸，并导出到日志。
- **日志**：关键过程全部落盘，便于在没有真触摸屏时先开发、后回带日志排查。

## 环境要求

- Windows 10/11
- .NET 10 SDK（`net10.0-windows`，WPF）
- 触摸相关功能需要**能上报接触尺寸的数字化器**（见下文）

## 构建与运行

```bash
git clone https://github.com/PrefacedCorg/TouchErase.git
cd TouchErase
dotnet run
```

发布（自包含单文件，目标机无需安装 .NET 运行时）：

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## 界面导览

- **顶部深色条**：显示当前校准结果（来源、物理尺寸、分辨率、mm/px、mm/DIU），右侧有「引导」按钮。
- **右侧面板**：
  - ① 校准：对角线输入 + 「应用」、「重新读取 EDID」、「EDID 尺寸推算方式」下拉。
  - ② 手掌轮廓 → 面积：「计算手掌面积（凸包+Shoelace）」「清除描摹」。
  - ③ 触按采样 → 遮挡半径：θ（聚类阈值）、k（MST 切边数）滑块，「生成模拟触点（无触摸屏时）」「计算遮挡半径（聚类+MST+最小覆盖圆）」「清除触点」。
  - ④ 日志：「记录到日志文件」「逐帧记录 HID Bounds」开关、「打开日志文件夹」「HID 触摸诊断（写日志）」。
- **中部标签页**：
  - ① 描摹手掌轮廓：顶部横尺 + 左侧竖尺（由 EDID 物理尺寸换算，可用真尺子核对），下方 InkCanvas。
  - ② 触按采集：深色触按区，按簇分色显示触点与遮挡圆。
  - ③ 面积擦预览：左预览区 + 右参数栏（生效来源、擦除形状、倍率、自动标定倍率、清除预览）。
- **底部状态栏**：实时提示（采集点数、结果、错误等）。

## 核心原理

### 校准

- EDID 物理尺寸按字节 66/67/68 读取；多屏时按**主屏分辨率比例**挑选最匹配的一块。
- **方形像素校正**：现代面板像素必为方形，故物理宽高比应等于分辨率宽高比。若 EDID 二者不符（虚拟机/远程会话的虚拟显示器常见），提供三档推算方式：
  - 横竖都按 EDID（原样）
  - **按宽推竖（方形像素）**（默认）
  - 按竖推宽（方形像素）
- `mm/DIU = mm/px × DPI 缩放`，避免 `GetDeviceCaps` 的虚拟毫米问题。`DpiChanged` 时重算。

### 面积

- 描摹点来自 InkCanvas 的 `StrokeCollected`（鼠标/手指/笔任一输入都会触发，且 `Stroke.StylusPoints` 即原始密集点）。
- 逐笔 Douglas-Peucker 去抖（避免多笔间产生虚假连接边）→ Shoelace 求面积；凸包用于"近凸近似"。
- 两者都输出并给出比值：凸的轮廓（如圆）≈1.00；描到手指分叉的凹口会 >1。

### 遮挡半径

- `ClusterByDistance` 聚类 → `LargestComponentAfterCuts`（MST 切 k 条最长边后取**触点总数最多**的连通分量）→ `MinEnclosingCircle`（Welzl 随机增量）。
- 注意：**握手**默认 `k=1`，只切掉笔尖等离群触点、保留整只手（掌心+手指）；想只取掌心把 k 调大。
- k 的取值不可按"簇的个数"排序：掌心通常是 1 个致密大簇，手指是多个小簇，按簇数会误把手指链判成掌心，故按触点总数。

### 面积擦：接触尺寸从哪来（本项目最关键的部分）

Windows 侧有三条通路，按优先级仲裁（`原始HID > WM_POINTER > WPF`，取最近 300 ms 内活跃的最高优先来源）：

| 来源 | 取值 | 说明 |
|---|---|---|
| **原始 HID（最高优先）** | Raw Input 枚举设备 + `HidP_GetUsageValue`，从 `WM_INPUT` 报文里直接解码 Digitizer **Width (0x48) / Height (0x49)**，按 HID 单位换算成 mm | 绕开 Windows 的 rcContact 映射，**系统给 0 也能拿到** |
| WM_POINTER | `POINTER_TOUCH_INFO.rcContact`（屏幕像素） | 需 `touchMask` 带 `TOUCH_MASK_CONTACTAREA` |
| WPF | `TouchPoint.Bounds`（DIP） | 走 WM_TOUCH |

判定规则：

- 原始 HID 是设备上报的真值，**不套 mm 阈值**；WM_POINTER / WPF 套阈值（`MinContactMm = 1.0` mm）以过滤"只报位置"的占位值。
- 中值滤波（最近 5 次）保证稳定不乱跳；固定倍率保证等比；换来源时清空滤波窗。
- 第 ③ 页「生效来源」会显示当前数值来自哪一路，并列出三路各自的原始值。

### 关于"手掌平均面积能不能设成定值"

不建议。文献里**投影面积**有数据（成人手掌不含指约 75–87 cm²），但**按压接触面积**取决于压力、姿势、个体，跨度很大（等效直径可达 64–117 mm），没有可靠定值；只适合作为校验窗口（如 `[40 mm, 60 mm]`）。因此本项目一律**现场标定**而非写死常量。

### 为什么注入的触摸没有接触尺寸

接触尺寸是否上报，取决于**设备 HID 描述符有没有声明 Width/Height**：

- Windows 触屏 HID 要求中 **Width (0x48) / Height (0x49) 是"可选"**，必需的是 Contact ID / X / Y / Tip / Contact Count，所以大量设备不报。
- 远程控制类工具（如 UU远程）走的是 `HID_DEVICE_SYSTEM_VHF`（Virtual HID Framework）合成触摸设备，**它本身不声明 Width/Height**，因此注入的触摸永远没有接触尺寸。
- 真触摸屏通常走类似于 `VIRTUAL_DIGITIZER` 的数字化器，会声明 Width/Height。
- 用「HID 触摸诊断（写日志）」可一眼看清：`Width(0x48)=True/False`。

## 日志

- 默认写在 **exe 同级 `logs\`**；若该目录不可写则回退到 `%LOCALAPPDATA%\TouchErase\logs`。
- 内容：启动/退出与系统信息、校准全过程、描摹点数、面积结果、HID 能力扫描、逐帧 HID Bounds（限流）、三路来源各自原始值、聚类结果、未处理异常堆栈。
- 写文件带缓冲，每秒落盘一次 + 退出时落盘，逐帧日志有限流，不拖慢 UI。

## 已知限制

- **绝对值依赖设备**：部分驱动只上报固定标称值（不随压力变），甚至完全不报 Width/Height；此时软件层无法获取接触面积，只能退化为固定标称尺寸。
- **描述符声明的单位可能不诚实**：个别驱动的 Width/Height 数值尺度与声明的单位不符，需要用「倍率」做现场校正。
- **包围矩形非真实几何面积**：`Width × Height` 是接触包围盒，斜按/不规则接触会高估。
- 大接触（手掌）可能被 Windows 判定为 palm 而不下发；某些硬件/驱动下事件根本到不了应用。
- `HidDescriptor` 的接口 + IOCTL 兜底通路在部分设备上返回 `ERROR_INVALID_FUNCTION`，属已知次要问题；主路径（Raw Input + `HidP_*`）不受影响。

## CI / 发版

两个工作流，职责分开：

- `.github/workflows/build.yml` —— **提交即编译**。push 到 `main` / PR 时执行 `restore` + `build -c Release`，不产出安装包、不发布。
- `.github/workflows/release.yml` —— **手动发版**。GitHub → Actions → `release` → `Run workflow`，填版本号（**留空**则读取 `TouchErase.csproj` 的 `<Version>`），可选预发布。流程：`restore` → `build` → `publish`（自包含单文件 win-x64）→ 打包 zip → 上传 artifact → 自动打 tag `vX.Y.Z` 并创建 **GitHub Release**。

注意：

- 发版是**手动**触发的，推 tag **不会**自动发版。
- 若目标 tag 或 Release 已存在，创建会失败；请换版本号，或先删除同名 Release。

## 目录结构

```
TouchErase/
├─ .github/workflows/build.yml    CI：提交即编译
├─ .github/workflows/release.yml  CI：手动发版（publish + Release）
├─ app.manifest                  PerMonitorV2 DPI 声明
├─ App.xaml / App.xaml.cs        应用入口、全局异常兜底
├─ MainWindow.xaml / .cs         界面、引导模式、各业务逻辑
└─ Helpers/
   ├─ EdidReader.cs              SetupAPI 读 EDID
   ├─ ScreenCalibration.cs       物理尺寸模型、推算方式、对角线回退
   ├─ Geometry2D.cs              去抖 / 凸包 / Shoelace / 聚类 / MST / 最小覆盖圆
   ├─ Log.cs                     轻量文件日志
   ├─ PointerTouch.cs            WM_POINTER 直读 rcContact
   └─ RawHidScan.cs              Raw Input 枚举 + HID 能力探测 + WM_INPUT 解码
```
