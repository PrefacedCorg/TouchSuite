# TouchErase · 手掌擦矫正 Demo

一个 WPF 桌面 Demo，用于验证"手掌擦"所需的量：**屏幕物理尺寸校准**、**手掌面积**、**驱动上报的手掌接触面积**，并提供**实时面积擦预览**（按多大、擦多大）。

核心难点不在算法，而在**没有可靠的"手掌物理面积"定值**，以及**接触尺寸能否从设备拿到**。本项目把相关通路全部打通并做成可诊断、可回退。

## 功能

三步主流程（对应界面中部标签页 ①②③）：

1. **屏幕物理尺寸校准**：读显示器 EDID 得到物理毫米尺寸；读不到可手填对角线英寸。所有物理换算（mm/px、mm/DIU）都由它推导。
2. **手掌面积**：在画布上描一圈手掌轮廓 → 去抖（Douglas-Peucker）→ 同时给出**描摹闭合曲线面积**与**凸包面积**（cm²），并给出两者比值用于判断轮廓凹凸。
3. **手掌接触面积**：把整只手（含手指）按在触按区，直接**读驱动上报的接触尺寸**并换算成 mm² 实时显示——不推算、不聚类。

附带：

- **实时面积擦预览**（第 ③ 页）：读驱动上报的接触尺寸，用「固定倍率 + 中值滤波」实时画出与接触面积等大的擦除区（默认矩形，可切正圆/等面积）。
- **引导模式**：启动自动进入分步引导，高亮当前要操作的控件；右上角「引导」可随时重看。
- **HID 触摸诊断**：列出每个触摸设备是否声明接触尺寸（只读，不打开设备）。
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

> 想从零跑一遍全流程？见 [TESTING.md](TESTING.md)（含"没触摸屏用注入自测"和"真机按手掌"两条路）。

## 界面导览

- **顶部深色条**：显示当前校准结果（来源、物理尺寸、分辨率、mm/px、mm/DIU），右侧有「引导」按钮。
- **右侧面板**：
  - 屏幕校准（EDID）：对角线输入 + 「应用」、「重新读取 EDID」、「EDID 尺寸推算方式」下拉。
  - ① 手掌尺寸（面积 a）：描一圈「计算手掌面积（凸包+Shoelace）」/「清除描摹」，**或**直接输入「手宽 × 手长」cm。
  - ② 手掌接触面积：显示当前接触尺寸，以及本次按压的峰值（手掌接触面积取峰值，而非松手时的值）。
  - 日志：「记录到日志文件」「逐帧记录 HID Bounds」开关、「打开日志文件夹」「HID 触摸诊断（写日志）」。
- **中部标签页**：
  - ① 描摹手掌轮廓：顶部横尺 + 左侧竖尺（由 EDID 物理尺寸换算，可用真尺子核对），下方 InkCanvas。
  - ② 手掌接触面积：深色触按区，把手掌按上去即显示当前接触尺寸与本次按压峰值（手掌面积取峰值）。
  - ③ 面积擦预览：左预览区 + 右参数栏（接触来源下拉：自适应自动锁定 / 原始HID / WM_POINTER / WPF、HID 计数标定、手动比例与注入自测、擦除形状、「自动倍率」开关 +「手动倍率」滑块、「随压力变化」开关、清除预览）。
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
- 第 ③ 页「接触来源」下拉决定用哪一路：默认**自适应**——先观察哪一路**连续给出稳定可用**的读数（≥300ms），随后**锁定**它，期间忽略其他来源（不再乱切、滤波窗也不会被反复清空）；锁定来源静默超过 1.5s（抬手/失效）才解锁重识别。也可手动指定只用 原始HID / WM_POINTER / WPF。下拉框下方实时列出三路各自的原始值。

**擦除区大小（两种模式）**

- **随压力变化**（默认，勾选开关）：擦除区面积 = 接触面积 b × 倍率²；按越重越大，即"按多大、擦多大"。
- **固定为手掌面积**（取消勾选）：擦除区面积恒等于 ① 描摹出的**手掌面积 a**（位置仍跟随按压处，尺寸不随压力变）。
- **形状（长宽比）**：优先用 ① 页设定的**手掌长宽比**（手输"手宽×手长"，或描摹轮廓的外接矩形）→ 擦除区**朝向由你输入的尺寸决定**，与驱动上报的 W/H 朝哪无关；未设定时退回②页按压峰值的比例。
- **倍率：自动 / 手动分开**。默认**自动**：`k = √(a / b)`（a = ①手掌面积，b = ②页按压峰值），实时自动计算、**不用滑块、不设上限、无需点击**——按下时擦除区面积 ≈ a，同时仍随压力等比变化。取消「自动倍率」后才用**手动倍率**滑块。

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
- 内容：启动/退出与系统信息、校准全过程、描摹点数、面积结果、HID 能力扫描、逐帧 HID Bounds（限流）、三路来源各自原始值、未处理异常堆栈。
- 写文件带缓冲，每秒落盘一次 + 退出时落盘，逐帧日志有限流，不拖慢 UI。

## 已知限制

- **绝对值依赖设备**：部分驱动只上报固定标称值（不随压力变），甚至完全不报 Width/Height；此时软件层无法获取接触面积，只能退化为固定标称尺寸。
- **接触尺寸可能没有物理单位**：实测 ELAN2097 触摸硬件确实声明了 Width/Height，但 `PhysicalMax=0`、`Units=0`，只给**逻辑计数**（0..255），无法自动换算成毫米。此时用第 ③ 页的「标定 HID 计数→mm」：先在第 ① 页量出手掌面积，再回到第 ③ 页用整只手按一下，程序按 `等效直径 = 2√(A/π)` 与「手掌峰值计数」算出 `mm/计数`，之后原始 HID 就能给出物理尺寸。
- **个别驱动声明的单位与实际尺度不符**：可用第 ③ 页的「倍率」做现场微调。
- **包围矩形非真实几何面积**：`Width × Height` 是接触包围盒，斜按/不规则接触会高估。
- 大接触（手掌）可能被 Windows 判定为 palm 而不下发；某些硬件/驱动下事件根本到不了应用。
- **触摸屏上电时序**：设备初始化完成前可能以"引导用 PID"（如 `0xFFFF`/`0xFFFE`）出现、且尚未暴露触摸 collection，此时程序启动扫描会漏掉它。程序在收到"不认识设备句柄"的原始输入时会**自动重扫能力表自愈**（无需手动点「HID 诊断」）。

## CI / 发版

两个工作流，职责分开：

- `.github/workflows/build.yml` —— **提交即编译**。push 到 `main` / PR 时执行 `restore` + `build -c Release`，不产出安装包、不发布。
- `.github/workflows/release.yml` —— **手动发版**。GitHub → Actions → `release` → `Run workflow`，填版本号（**留空**则读取 `TouchErase.csproj` 的 `<Version>`），可选预发布。流程：`restore` → `build` → `publish`（自包含单文件 win-x64）→ 打包 zip → 上传 artifact → 自动打 tag `vX.Y.Z` 并创建 **GitHub Release**。

注意：

- 发版是**手动**触发的，推 tag **不会**自动发版。
- 若目标 tag 或 Release 已存在，创建会失败；请换版本号，或先删除同名 Release。

## 校准向导（独立程序 TouchErase.Calibrator）

面向**任意触摸屏**的分步校准向导，产出该设备的屏幕物理尺寸与「手掌擦 / 书写」判定阈值：

```bash
dotnet run --project Calibrator/TouchErase.Calibrator.csproj
```

流程：读 EDID（**只取当前在用的显示器**，多块时可下拉改选；切勿直接扫 `Enum\DISPLAY`，那里含已拔除的显示器会挑错）→ 横/竖 **10cm 实尺核对**（标尺每 1mm 一格，每 1cm 加粗标注）据此定推算方式（都准=各按各，只横准=按宽推竖，只竖准=按竖推宽）→ **左描摹 / 右填数**得手掌宽高与面积 → **手掌按一下**得驱动上报面积（支持压感则记压感阈值；可多次取中值）→ **手指按一下**得面积 → **阈值 =（手掌 + 手指）÷ 2** → **面积擦预览**（**整套搬自主程序第③页**：接触来源下拉（自适应自动锁定 / 原始HID / WM_POINTER / WPF）+ 三路明细、HID 计数标定、手动比例 + 注入自测、擦除形状（矩形/正圆等面积）、**「倍率」「面积阈值」「压感阈值」三个 GroupBox**、随压力变化、清除预览）。

> **三个 GroupBox 都是"自动值 + 微调滑块"**（不再用勾选框切换）：
> - **倍率**：自动 = √(手掌面积 a ÷ 本页按压峰值 b)，自动计算；滑块在其**基础上再乘**（0.5–1.5，默认 1.00）→ 生效倍率 = 自动 × 微调；
> - **面积阈值（手掌擦 / 书写）**：自动 = (手掌按压 + 手指按压) ÷ 2，来自第 5/6 步实测；滑块微调；
> - **压感阈值**：自动 = 手掌按压时的压感；滑块微调。
>
> 三处都实时显示 `自动 → 微调 → 生效`。保存 `calibration.json` 时**同时保留自动值与微调后的生效值**。

> 后端已按文件拆分，方便维护：引擎在 `Calibrator/Eraser/EraserEngine.cs`（来源仲裁、中值滤波、倍率、形状、大小计算，**纯逻辑无 UI**），枚举在 `Eraser/ContactTypes.cs`，界面在 `Eraser/EraserPreviewPage.xaml(.cs)`；主窗口只负责把标定量（mm/DIU、手掌面积与长宽比）喂进去、并把触摸样本转交。

> **手动标定兜底**：若横竖都不准、或 EDID 读失败，第 3 步会露出「手动标定」面板 —— 方式①用真尺子**在屏幕上拖出一条 10cm 线段**（横竖都行，画斜了自动摆正到较长那一边），程序据此推得 mm/px 并给一个**微调滑块**（对着尺子拨到红虚线与 10cm 对齐后「应用此标定」）；方式②直接填对角线英寸。两者都按方形像素推物理宽高。

> **高精度模式**（顶部开关，默认开）：后端**全程双精度、不做任何取整**——手掌/手指面积不再取整到整数 mm²，HID 换算走全量程线性映射 `fraction=(logical−LogicalMin)/(LogicalMax−LogicalMin)`、`physical=PhysicalMin+fraction×(PhysicalMax−PhysicalMin)`，并按 `UnitsExp/Units` 换算；显示按**有效数字自适应**给出小数位（约 7 位有效数字，保留尾零，便于直接看出设备分辨率，例如 `0.01234560 mm`）。关闭后回到旧行为：后端取整、显示固定 1 位小数。三路来源的明细行会直接打出 `W=原始计数/上限 → 全精度 mm`。

结果可保存为 `calibration.json`（exe 同级，不可写则退回 `%LOCALAPPDATA%\TouchErase`）。

> 说明：该程序是**从零写的独立工程**（不引用主程序 Helpers），触摸读取用 RawInput 解 HID 的 Width/Height/TipPressure，WPF `TouchPoint` 兜底。

## 目录结构

```
TouchErase/
├─ .github/workflows/build.yml    CI：提交即编译
├─ .github/workflows/release.yml  CI：手动发版（publish + Release）
├─ app.manifest                  PerMonitorV2 DPI 声明
├─ App.xaml / App.xaml.cs        应用入口、全局异常兜底
├─ MainWindow.xaml / .cs         界面、引导模式、各业务逻辑
├─ Calibrator/                   独立「校准向导」工程（TouchErase.Calibrator）
│  ├─ MainWindow.xaml / .cs     分步引导壳（EDID → 标尺核对 → 手掌尺寸 → 手掌/手指按压 → 结果）
│  ├─ TouchInput.cs             触摸读取（RawInput 解 HID + WM_POINTER 兜底 + WPF 兜底 + 计数→mm 标定）
│  ├─ Edid.cs / ScreenCalibration.cs   EDID 与物理尺寸模型
│  ├─ CalibrationResult.cs      结果模型（可存 calibration.json）
│  └─ Eraser/                   ← 面积擦「后端」拆分在此，逻辑与界面分离
│     ├─ ContactTypes.cs        接触来源 / 来源模式 / 形状 等枚举
│     ├─ EraserEngine.cs        引擎：来源仲裁（自适应锁定）+ 中值滤波 + 倍率 + 形状 + 大小计算（纯逻辑，无 UI）
│     └─ EraserPreviewPage.xaml / .cs   第③页整套界面（预览区 + 全部参数控件）
└─ Helpers/
   ├─ EdidReader.cs              SetupAPI 读 EDID
   ├─ ScreenCalibration.cs       物理尺寸模型、推算方式、对角线回退
   ├─ Geometry2D.cs              去抖 / 凸包 / Shoelace
   ├─ Log.cs                     轻量文件日志
   ├─ PointerTouch.cs            WM_POINTER 直读 rcContact
   └─ RawHidScan.cs              Raw Input 枚举 + HID 能力探测 + WM_INPUT 解码
```
