# Ink 输入层重做：RawInput + WM_POINTER（工具栏可切换输入源）

## Context（为什么做这件事）

用户最初对 `TouchSuite.Ink` 的输入提出的是「C# 五步骨架：从注册到解析」，技术路径为 **RawInput 抓全量 + WM_POINTER 做应用层**，要求 P/Invoke、不依赖第三方库。

但当前 Ink 并未落实这条：`src/TouchSuite.Ink` 里零 RawInput / WM_POINTER 代码，输入全部来自 WPF 的 `Mouse` / `Stylus` / `Touch` 事件（`MainWindow.xaml` 的 Window 属性 + `MainWindow.xaml.cs` 的 `OnMouse*` / `OnStylus*` / `OnTouch*`）。后果有两个：

1. 拿不到**接触尺寸 / 朝向**（WPF 的 Touch 通道拿不到，Stylus 通道又会把触摸提升掉）；
2. WPF 会**合并采样**（所以现在不得不在 `StylusPointCollection` 里逐条回放）。

目标：按用户原话补上 RawInput + WM_POINTER 两条原始通路，并在工具栏加下拉框在 `WPF 事件 / RawInput / WM_POINTER / 组合` 之间切换，**默认仍为 WPF 事件**（不破坏现有行为，随时可回退）。

## 已定决策

| # | 决策 |
|---|---|
| 1 | 下拉框 4 项：`WPF 事件`（默认）/ `RawInput` / `WM_POINTER` / `组合`（RawInput 主 + POINTER 兜底） |
| 2 | 接触尺寸（`0x0D:0x48/0x49`、`rcContact`）**只进日志与状态栏**，`InkSample` 不改 → 零渲染影响 |
| 3 | 多屏：先按主屏 Clamp（墨迹层维持只铺主屏，多屏支持另开一项） |
| 4 | **触笔仍走 WPF Stylus**：RawInput 只注册 `0x0D` 触摸 usage，WM_POINTER 只处理 `PT_TOUCH`（`PT_PEN` 不会被提升成 Touch，因此不会重复） |
| 5 | 互斥靠**应用层门控**：`RegisterPointerInputTarget` 需要 UIAccess 特权（须签名+装到受信目录），普通 App 直接 `ERROR_ACCESS_DENIED`，已查官方文档排除 |

## 架构：三源归一到同一组落点函数

不另开绘制通路。新增的输入源只负责"产生 `pointerId / 坐标 / 压感`"，最终统一调用**现有**的 `PointerDown` / `PointerMove` / `PointerUp`（`MainWindow.xaml.cs` L279-L317），这样橡皮分支（L284-L289）、`strokeId == 0` 约定（L286）、`_strokeOfPointer`（L38）全部原样复用。

通道开关表（`_wpfChannelActive` 一个布尔即可驱动）：

| 模式 | Mouse | Stylus | Touch | RawInput | WM_POINTER |
|---|---|---|---|---|---|
| WPF | ✔ | ✔ | ✔（保留 `_sawStylus` 规则） | ✘ | ✘ |
| RawInput | ✔（需过滤提升） | ✘（只记丢弃计数） | ✘ | ✔ | ✘ |
| WM_POINTER | ✔（需过滤提升） | ✘（只记丢弃计数） | ✘ | ✘ | ✔ 仅 `PT_TOUCH` |
| 组合 | ✔（需过滤提升） | ✘（只记丢弃计数） | ✘ | ✔ 主 | ✔ 兜底 |

两条新发现的必做门控（都来自本次调研，不门住会出怪现象）：

- **`_annotating` 门**：`RIDEV_INPUTSINK` 让 Ink 在**任何前台状态**下都收 `WM_INPUT`；而鼠标模式下窗口带 `WS_EX_TRANSPARENT`，WPF 通道收不到但 RawInput 照收 → 不门住会在鼠标模式下凭空画线。因此 `FeedFrame` 与 `PointerDown/Move/Up` 在 `_annotating == false` 时直接丢弃，并在切源/退出时把在场笔画全部 `End`。
- **鼠标提升过滤**：RawInput / POINTER 模式下要丢掉"由触摸提升出来的鼠标事件"，否则同一根手指画两遍。判据两层：`MouseEventArgs.StylusDevice is not null`（仓库已有同款用法，见 `src/TouchSuite.App/TouchInput.cs` L203）+ "当前有原始触点就丢鼠标" 双保险。

## 文件清单

### 新增 `src/TouchSuite.Ink/Input/`

**`InkInputMode.cs`（新写，~40 行）**
`enum InkInputMode { Wpf, RawHid, WM_POINTER, Combo }` + `LabelOf()` / `ApiNameOf()`。

**`RawHidTouch.cs`（移植，源：`src/TouchSuite.App/HidReader.cs`，914 行 → 目标 ~400 行）**

必须整块搬（保留原名便于与 App 交叉比对）：

| 内容 | 源行号 |
|---|---|
| 三条硬经验注释（多子报文逐解 / 按自己的 LinkCollection 取值 / Units 取低 4 位） | L6-L19 |
| 常量与结构体（`RAWINPUTDEVICE`、展平的 `HIDP_CAPS` —— L64-L67 注明 ByValArray 在 .NET 10 会崩，别改回数组） | L22-L30, L55-L82 |
| `Cap` / `DeviceCtx`（含 `Links`、`TipSwitchLinks`、`TipEverSeen`、`LastValues`、`HadPress`、`ContactCountLink`） | L82, L84-L124 |
| `Scan()` 能力探测（`HidP_GetValueCaps` L334-L337、`HidP_GetButtonCaps` 找 `0x0D:0x42` L374-L394、`Links` 汇总 L370-L371） | L286-L441 |
| **合成指针设备适配**（`\\?\Microsoft HID RID\...` 这类 user32 注入屏：描述符写 0..32000 但报文是屏幕像素） | L255-L263, L324-L327, L354-L358 |
| 注册（`RIDEV_INPUTSINK` + `hwndTarget`；**只留 0x0D 系列**） | L443-L454 |
| WM_INPUT 解码（两次 `GetRawInputData` L489-L500、`sizeHid`/`reportCount` L509-L510、按 `i*sizeHid` 切子报文 L514-L521） | L485-L568 |
| 单槽位取值（`TryReadLink`：`0x0D:0x48/0x49`、`0x01:0x30/0x31`、`0x0D:0x30`，含 L727 读 `0x0D:0x51` Contact Identifier、L743-L753 `TipPressed`） | L700-L753 |
| 抬手判定（`ContactCountIsZero` L627-L636、`BuildLiftSample` L638-L692 的"残留幽灵槽"处理、L539-L547 的 `HadPress`） | L627-L692 |
| 设备名 / preparsed data | L868-L905 |
| P/Invoke 声明 | L237-L280 |

**不抄**（都属于 App 的"手掌判定/多屏标定"场景，Ink 不需要，抄了反而编译不过）：
- 逻辑屏分组：`GroupKey`、`ResolveGroupKey`(L198-L221)、`TryContainerId`(L157-L181)、`ExtractVidPid`(L184-L195)、`_deviceToGroup/_groupMembers/_vidPidToContainer`(L128-L130)
- 跨集合压感合并：L223-L233、L755-L810
- mm 换算与显示文本：`ToMm`/`CountsToMmScale`(L812-L833)、`ScaleText`/`PhysSuffix`/`PhysFullMm`(L835-L866) —— 这些依赖 `Precision.cs`，**该文件只在 App 工程里**。接触尺寸改走**比值法**：`Wpx = WLogical / XLogMax * 屏宽`（等价 App 的 `HidSizeScale.Stretch`）。
- `PickBest` 的"只取面积最大那根"语义(L570-L625) ← **本方案最关键的改动**：Ink 要**全量**，改为收集本帧所有 `Down == true` 的槽位。

新对外 API：

```csharp
internal static class RawHidTouch
{
    public static bool Register(IntPtr hwnd, out int win32Error);
    public static bool Unregister();                                    // RIDEV_REMOVE 且 hwndTarget=NULL（文档要求）
    public static List<HidDeviceInfo> Scan();

    public sealed record RawContact(int Link, int ContactId, bool Down,
        int XLogical, int YLogical, int XLogMax, int YLogMax,
        int WLogical, int HLogical, double? Pressure01);
    public sealed record RawFrame(int SubIndex, int SubCount, bool CountZero, IReadOnlyList<RawContact> Contacts);

    public static bool TryReadFrames(IntPtr lParam, out List<RawFrame> frames);   // 替代 App 的 TryHandleWmInput
    public static bool TryBeginAutoRescan();
}
```

与 App 的差异只有三处：① 不再 `PickBest`，遍历 `ctx.Links` 收集**所有**按下槽位；② **每个子报文各自产出一个 `RawFrame`**（对应"历史采样逐条回放"）；③ 把 `ContactCountIsZero` 作为 `RawFrame.CountZero` 带出来给跟踪器判"整体抬手"。

**`PointerTouchFrame.cs`（移植 + 升级，源：`src/TouchSuite.App.old/Helpers/PointerTouch.cs`，101 行 → 目标 ~250 行）**

整份抄结构体与单点 API：`POINT`/`RECT`(L23-L37，`Width/Height/IsValid` 很有用)、`POINTER_INFO`(L39-L58)、`POINTER_TOUCH_INFO`(L60-L70)、`PT_TOUCH/PT_PEN/POINTER_FLAG_INCONTACT/TOUCH_MASK_CONTACTAREA`(L17-L20)、`GetPointerType`/`GetPointerTouchInfo`(L74-L78)、`GetPointerId`(L84，**wParam 低 16 位**)、`TryRead`(L87-L100)。配套用法参考 `src/TouchSuite.App.old/MainWindow.xaml.cs` L130-L175 与 L252-L298。

必须升级成**帧 API**（单点 API 不够用）：

```csharp
[DllImport("user32.dll", SetLastError = true)]
private static extern bool GetPointerFrameTouchInfo(uint pointerId, ref uint pointerCount, [Out] POINTER_TOUCH_INFO[] touchInfo);
[DllImport("user32.dll", SetLastError = true)]
private static extern bool GetPointerFrameTouchInfoHistory(uint pointerId, ref uint entriesCount, ref uint pointerCount, [Out] POINTER_TOUCH_INFO[] touchInfo);
[DllImport("user32.dll", SetLastError = true)]
private static extern bool SkipPointerFrameMessages(uint pointerId);

public const int WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247,
                 WM_POINTERENTER = 0x0249, WM_POINTERLEAVE = 0x024A, WM_POINTERCAPTURECHANGED = 0x024C;

public sealed record PointerContact(uint PointerId, int Xpx, int Ypx, bool InContact,
    uint Pressure1024, RECT Contact, uint Orientation, uint FrameId);
public static bool TryReadFrame(uint pointerId, out List<PointerContact> contacts, out bool isTouch, out int win32Error);
public static bool TryReadFrameHistory(uint pointerId, out List<List<PointerContact>> frames, out int win32Error);
```

升级理由（官方文档依据）：帧内所有指针是同一设备同一时刻、应整帧当一次输入；帧只含同一窗口拥有的指针；处理完整帧后要 `SkipPointerFrameMessages` 跳过本帧剩余消息，否则同一帧会被派发 N 次。

**时序约束（写进注释）**：必须在 hook 内、同一 UI 线程、`GetMessage` 返回该消息后立即调用（帧信息与调用线程最近检索的消息绑定）。因此 **WM_POINTER 解码必须留在 UI 线程**。

错误码分流：`ERROR_ACCESS_DENIED` → hook 挂错窗口（必须挂 WPF 主窗口，不能挂 `InkSurfaceWindow`，后者 `WS_EX_TRANSPARENT` + `NOACTIVATE`）；`ERROR_NO_DATA` → 帧已被消费/过期，**回退单点 `GetPointerTouchInfo`**，不能静默丢样本。

**`ContactTracker.cs`（新写，~180 行）—— 手指身份 + 帧合成**

按用户已记录的硬经验实现，不推翻：

```
身份（RawInput）：本设备所有触点 Cid != 0 → 身份 = Cid(0x0D:0x51)
                  否则（如 Microsoft HID RID 屏 CID 恒 0）→ 最近邻跟踪分配 StableId
身份（WM_POINTER）：pointerId（Windows 保证稳定，无需跟踪）
LinkCollection/槽位只当"运输容器"，绝不进 _strokeOfPointer 的键（第 1 号坑：10 指标称但只有 6 通道交替上报）
```

最近邻跟踪参数（先按这些值起步，全部打到日志便于调）：`MatchRadiusPx = 0.06 × min(屏宽,屏高)`（1920 宽 → ≈115px）、`HoldFrames = 3`、`TtlMs = 120`。

规则（每条对应一条已记录教训）：

1. 贪心匹配：新触点找未占用轨道中距离最小且 `< MatchRadiusPx` 的；找不到就分配新 `StableId`（从 1 起递增，**不能与 `MousePointerId = 0` 冲突**，L33 已占用 0）。
2. 抬手判定不许只看"本帧没有"（通道交替会让某帧少报手指）：取 `RawFrame.CountZero`（`0x0D:0x54` 掉到 0）**或** 连续 `HoldFrames` 帧未出现且超过 `TtlMs`。没有 `0x0D:0x54` 声明的设备退回纯 TTL。
3. 末点补位：判抬手那一帧若有 `LastValues`（抄 `HidReader.cs` L642-L692 `BuildLiftSample` 语义）→ 先发 `Move`（抬手帧坐标非 0 才采用）再发 `Up`。这是"最后一点画不出来"的修复成果，不能丢。
4. 只对 `Wpx/Hpx` 做中值滤波（窗口 5），**位置绝不滤波**（第 2 号经验：位置滤波滞后 2 帧 → 末梢漏点）。Ink 现在没有滤波，保持不滤波。
5. `TryReadFrames` 返回**有序**列表，逐条按"一帧"调用 `Sync`（Down → Move → Up 顺序），**不做合并取最后一条**（Android 端"只读当前采样丢历史采样"的镜像问题）。

### 修改

**`MainWindow.xaml`** —— 在模式切换之后（L43 `ModeAnnotate` 之后、L44 `Divider` 之前）插入，照现有 `Divider` 风格：

```xml
<Border Style="{StaticResource Divider}" />
<TextBlock Text="输入源" Foreground="#CCFFFFFF" VerticalAlignment="Center" Margin="4,0,6,0" FontSize="13" />
<ComboBox x:Name="InputSourceCombo" Width="150" VerticalAlignment="Center" FontSize="13"
          SelectedIndex="0" SelectionChanged="OnInputSourceChanged">
  <ComboBoxItem Content="WPF 事件（现状）" />
  <ComboBoxItem Content="RawInput 原始 HID" />
  <ComboBoxItem Content="WM_POINTER 帧 API" />
  <ComboBoxItem Content="组合 RawInput+POINTER" />
</ComboBox>
```

注意：**不要套 `ToolToggle` 的 `Foreground="White"`**（弹出列表是系统白底，白字看不见），保持系统默认配色。

**`MainWindow.xaml.cs`** ——

- 取窗口句柄改在 **`OnSourceInitialized`** 重写里（不是 `OnLoaded`）：`_hwnd`、`_hwndSrc = PresentationSource.FromVisual(this) as HwndSource`、`_hwndSrc.AddHook(WndProc)`；**hook 委托必须存字段**（否则被 GC 悬空，同 `InkSurfaceWindow.cs` L19 的教训）。
- `WndProc` 处理 `WM_INPUT = 0x00FF` 与 `WM_POINTER*`。
- `OnStylusDown/Move/Up`(L198-L247) 与 `OnTouchDown/Move/Up`(L253-L275) 首行加 `if (!_wpfChannelActive) { _gateDroppedStylus++; return; }`（保留 `e.Handled = true`；不摘事件——摘了也拦不住提升，还丢诊断计数）。
- `OnMouseDown/Move/Up`(L165-L196) 加：`_annotating` 门 + `e.StylusDevice is not null` 过滤 + "有原始触点就丢"双保险。
- `PointerDown/Move` 各加两枚**带默认值**的可选参数 `float contactWpx = 0, float contactHpx = 0`（现有调用点全部不受影响）。
- 新增 `FeedFrame(List<ContactEvent>)`：`Down && !_strokeOfPointer.ContainsKey(id)` → `PointerDown`；`Move` → `PointerMove`；`Up` → `PointerUp`。
- 新增 `SwitchInputSource(InkInputMode)`：① 把在场笔画全部 `End` + `_strokeOfPointer.Clear()` + `_tracker.Reset()`；② 旧源反注册（`RIDEV_REMOVE` + `hwndTarget=NULL`）；③ 新源注册并**探针**，失败则记 `Log.Write` + 下拉框回落 `Wpf` + 状态栏显示 err；④ `_wpfChannelActive = mode == Wpf`，回 WPF 时重置 `_sawStylus`；⑤ 用 `_suppressInputCombo` 抑制程序内改选项时的重入（照 `src/TouchSuite.HidDump/MainWindow.xaml.cs` L263 范式）。
- 双画自检：`PointerDown` 里加同刻断言 —— 该 `pointerId` 落笔坐标与另一条在画笔画的最新坐标距离 < 3px 且来源不同 → 记 `WARN [互斥失效]` 并拒绝第二笔。
- `UpdateStatus()`(L370-L375) 扩成一行，照 `src/TouchSuite.Receiver/Program.cs` L63-L70「显示实际生效的 API 名」：

```
{backend}　|　输入源：{LabelOf(_mode)}（{ApiNameOf(...)}）　|　{hint}　|　Ctrl+滚轮/滑块调粗细
```

`ApiNameOf` 必须是"实际生效"而非"期望"，例如 `WPF Stylus/Touch/Mouse 事件`、`HID 0x0D:0x04 RIDEV_INPUTSINK，3 个触摸屏，最近帧 2 子报文/5 触点`、`user32 GetPointerFrameTouchInfo（帧 API）PT_TOUCH`、`RawInput 注册失败 err=87 → 已回退 WPF`。

**`App.xaml.cs`**（照 L11-L18 现有 `--selftest` 解析风格）追加：`--input=wpf|raw|pointer|combo`（默认 `wpf`）、`--inputprobe`（只测不画）。

## 验证（每条都有可判定的日志）

1. **回归**：`TouchSuite.Ink.exe --selftest` 仍通过（DComp 后端名、2.5s 自动退出）→ 证明没碰渲染通路。
2. **注册探针**：`TouchSuite.Ink.exe --input=raw --inputprobe` → 日志应有：每条 usage 注册结果 + `GetLastWin32Error`、设备清单（名字 / `UsagePage:Usage` / 槽位数 / 是否声明 `0x0D:0x51/0x42/0x54/0x48/0x49`）、3 秒内 `WM_INPUT` 帧数/子报文数/触点数字方图。
3. **单指不重复**：选 RawInput 慢画一笔 → 有 `[落笔] 来源=RawInput`，同刻**不应**出现 `来源=WPF` 的落笔；`[门] 丢弃 Stylus/Mouse 事件` 计数单调增。
4. **多指（6 通道设备）**：五指同时按 → 5 个 `StableId` 互不相同；抬 1 根其余 4 根笔画不中断（`同时在场` 5→4，无误 `End`）；抬起那根有抬手补位日志。
5. **采样点数对拍**（防漏点/防合并）：Receiver(user32 注入) 同时喂 `TouchSuite.App`(RawInput 页) 与 `Ink`(RawInput 模式)，同时段采样点数应基本相等（差 <5%）。这正是 Android"只读当前采样"问题的镜像验收。
6. **WM_POINTER**：选该模式，日志应出现 `WM_POINTERDOWN(0x246)` 且 `GetPointerFrameTouchInfo` 返回 `pointerCount>=1`；注入 5 指 → `pointerCount==5` 且一次调用拿全 5 个触点（证明帧 API 生效 + `SkipPointerFrameMessages` 生效）。
7. **失败路径**：临时把注册 usage 改成不存在的 `0x0D:0x99` → 应看到 `[输入源] 启用失败 … 回退 WPF` + 下拉框自动回落 + 状态栏 err，且绘图仍可用。
8. **提升过滤实测**：把 `MouseButtonEventArgs.StylusDevice != null` 次数与"有原始触点时丢鼠标"次数**分开计数**打日志，据此决定最终保留哪条判据（不靠猜）。

## 实施顺序（每步都能跑、能回退）

1. `InkInputMode.cs` + XAML 下拉框 + `SwitchInputSource`/`UpdateStatus` 骨架（只切 WPF/占位，不动输入逻辑）→ 验证 UI 与状态栏文案。
2. `RawHidTouch.cs` 先只做 `Scan` + `Register`/`Unregister` → `--inputprobe` 验证注册与设备清单。
3. `TryReadFrames` + `ContactTracker`（只记日志、不落笔）→ 验证多指/多子报文/CountZero 的全量语义（对拍第 5 条）。
4. 接 `FeedFrame → PointerDown/Move/Up`，同时落门控与双画断言 → 验证单指不重复、多指不互相顶掉。
5. `PointerTouchFrame.cs`（帧 API + History + Skip）→ 验证第 6 条。
6. 组合模式兜底 + 全部日志/探针计数收口。

## 风险与处置

| 风险 | 判据 | 处置 |
|---|---|---|
| `RegisterRawInputDevices` 被拒（87/5） | `GetLastWin32Error()` | 核对 `Marshal.SizeOf<RAWINPUTDEVICE>()`；降级只注册 `0x0D:0x04` 重试一次；仍失败 → 回退 Wpf + 状态栏明示 err |
| hook 里设 `handled=true` 挡不住 WPF 提升 | `[门] 丢弃 Stylus 事件 N` 持续增长 | 回退纯应用层门控（本就已实现），保留计数作为"提升确实存在"的证据 |
| 重复落笔 | 3px 同刻断言 + 点数对拍 | 查 `_annotating` 门、`StylusDevice` 过滤、双保险 |
| 手指身份跳变（多指交叉） | 同一 `StableId` 坐标突跳 > MatchRadius | 调 `MatchRadiusPx`；必要时改最小总距离匹配 |
| CID=0 屏跟踪参数不合适 | 同一手指连续多笔分配新 id（`新轨道` 日志过多） | 参数化成日志可见常量，先按上面的起步值调 |
| WM_POINTER 拿不到消息 | `ERROR_ACCESS_DENIED` / `ERROR_NO_DATA` | ACCESS_DENIED → hook 挂错窗口；NO_DATA → 回退单点 API |
| **不要注册 0x01/0x02/0x06** | —— | 每设备类在进程内只能注册一个窗口，会与 WPF 自己的鼠标/键盘 raw input 互相顶掉（鼠标本来就只走 WPF 通道） |
| 多屏坐标错位 | 副屏触点画到主屏错误位置 | 本版按主屏 Clamp（已决策第 3 条） |

## 复用 vs 新写一览

- **复用/整块搬**：`HidReader.cs` 的 RawInput/HidP 链路、`PointerTouch.cs` 的结构体与单点 API、`PointerDown/Move/Up`(L279-L317)、`MakeSample`(L321-L326)、`_strokeOfPointer`(L38)、`Interop/Win32.cs` 的常量、`Log.cs`、HidDump 的 ComboBox/`_suppressCombo` 范式、Receiver 的 ApiName 显示范式。
- **新写**：`InkInputMode`、`ContactTracker`、`TryReadFrames`、帧 API + History + Skip、门控与探针计数、`FeedFrame`、下拉框处理器。
