# TouchSuite

把一台**安卓平板**变成 Windows 的触摸屏，并测量/校准触摸所需的物理量（屏幕 mm、手掌面积、驱动上报的接触面积与压感）。

```
┌──────────────┐   TCP 9000   ┌───────────────────┐   user32 合成指针   ┌─────────┐
│ 安卓端 App    │ ───────────► │ TouchSuite.Receiver│ ───────────────► │ Windows │
│ (平板触摸)    │              │  (PC 接收/注入)     │  或 --vhid 驱动    │  桌面   │
└──────────────┘              └───────────────────┘                    └─────────┘
```

## 仓库组成

| 目录 | 组件 | 说明 |
|---|---|---|
| [src/TouchSuite.App](src/TouchSuite.App) | **主项目**：触摸量校准向导（WPF，.NET 10） | 7 步引导，量出屏幕物理尺寸、手掌/手指接触面积与压感，结果存 `calibration.json` |
| [src/TouchSuite.Receiver](src/TouchSuite.Receiver) | Windows 接收端（控制台，.NET 9） | 收平板触摸帧并注入桌面；`--vhid` 时喂给虚拟 HID 触摸屏驱动 |
| [android/](android) | 安卓端 App | 把平板触摸面通过网络发给 PC |
| [driver/](driver) | 虚拟 HID 触摸屏驱动（VHF，C） | 装上后 Windows 才认成"真的触摸屏"，接触面积/压感才有意义 |
| [src/TouchSuite.App.old](src/TouchSuite.App.old) | 旧主项目（历史 Demo） | 之前的手掌擦 Demo，一般不用；其 `README.md`/`TESTING.md` 内容仍是旧名 TouchErase |

> 为什么需要驱动：`user32` 合成指针注入的触摸**不带接触面积**，也基本不吃压感。要让应用看到真实的面积/压感，得走虚拟 HID 触摸屏（`--vhid`）。

## 快速上手

1. **编译**
   - C#（3 个项目）：`dotnet build TouchSuite.sln -c Release`（需 .NET 10 SDK）
   - 安卓：`cd android && gradlew assembleDebug`（需 JDK 17）
   - 驱动：见下文「驱动」小节
2. **开 Receiver**（PC 端，建议管理员）：`TouchSuite.Receiver.exe --vhid --show-input -v`
3. **平板**填 PC 的 IP 和端口（默认 9000），点连接。
4. 触摸平板，PC 上就跟着动。需要面积/压感就装驱动并用 `--vhid`。

---

## TouchSuite.Receiver

控制台程序，监听 TCP，把平板发来的触摸帧注入本机。

- 必须运行在**交互式桌面会话**（已登录、未锁屏），否则注入初始化会失败。
- 用 `--vhid` 时建议**以管理员身份运行**（要访问驱动设备）。
- 运行时输出会同时写到 Console 和 exe 同级的 `TouchSuite.Receiver.log`，排查问题直接把日志发出来即可。

### 参数

`TouchSuite.Receiver.exe --help` 也可查看。全部参数如下：

| 参数 | 说明 |
|---|---|
| `--port <n>` | 监听端口（默认 **9000**） |
| `--monitor <n>` | 目标显示器序号（默认 0，启动时会列出所有显示器） |
| `--fit <mode>` | 映射模式 `stretch` \| `fit` \| `cover`（默认 `stretch`） |
| `--feedback <mode>` | 触摸视觉反馈 `default` \| `indirect` \| `none`（默认 `default`） |
| `--max-contacts <n>` | 最大同时接触点（默认 10） |
| `--fixed-pressure <0..1>` | 强制固定压感，忽略平板上报 |
| `--contact-fallback <px>` | 平板未上报接触尺寸时的回退直径（像素，默认关闭） |
| `-v`, `--verbose` | 打印每秒统计 |
| `--self-test` | 离线自检协议解析与坐标映射（**不注入触摸**） |
| `--ipv4-only` | 只监听 IPv4（默认双栈 IPv6 + IPv4） |
| `--legacy-touch-api` | 强制用旧版 `InjectTouchInput`（默认用新版合成指针 API） |
| `--contact-scale <x>` | 接触面积放大倍数（默认 1.0，如 2 表示按 2 倍放大） |
| `--pressure-max <x>` | 平板原始压力的满量程（默认 14；填 0 则原始值直通不归一） |
| `--pressure-floor <v>` | 映射后的输出下限 0~1024（默认 256，即 0~14 → 256~1024） |
| `--input-type <t>` | 注入类型 `auto` \| `touch` \| `pen`（默认 auto：手指→触摸、笔→笔） |
| `--show-input` | 实时打印收到的压力/面积（排查压感是否有变化） |
| `--no-overlay` | 关闭屏幕左上角的调试小窗（**默认开启**） |
| `--contact-area <m>` | 接触面积来源 `auto` \| `size`（按平板 size 定面积，W×H=size）\| `major`（用 major/minor 包围盒） |
| `--no-inject` | 只算不注入（不碰桌面，用于核对坐标/面积/压力） |
| `--vhid` | 改用本仓库的虚拟 HID 触摸屏驱动（VHF）而非 user32 合成指针 |
| `--screen-mm <mm>` | 目标屏幕物理宽度（毫米），用于把接触尺寸换算成毫米（默认按 96DPI 估） |
| `-h`, `--help` | 显示帮助 |

### 常用组合

```bat
:: 默认：user32 合成指针，端口 9000
TouchSuite.Receiver.exe

:: 走虚拟 HID 驱动 + 实时看压感/面积（推荐，需先装驱动）
TouchSuite.Receiver.exe --vhid --show-input -v

:: 先离线自检，确认协议/映射没问题（不注入）
TouchSuite.Receiver.exe --self-test

:: 只解析不注入，核对坐标/面积对不对，不影响桌面
TouchSuite.Receiver.exe --vhid --no-inject --show-input

:: 平板没报接触尺寸，给个回退直径，并按 2 倍放大面积
TouchSuite.Receiver.exe --vhid --contact-fallback 12 --contact-scale 2

:: 压感满量程按 1023 归一，最轻也输出 512
TouchSuite.Receiver.exe --vhid --pressure-max 1023 --pressure-floor 512

:: 指定 2 号显示器、等比缩放映射、关掉调试小窗
TouchSuite.Receiver.exe --vhid --monitor 2 --fit fit --no-overlay

:: 端口被占 / 只想走 IPv4
TouchSuite.Receiver.exe --port 9100 --ipv4-only
```

---

## 安卓端

- 安装 `app-debug.apk`（CI 产物在 `dist\TouchBridge-debug.apk`，或 Release 页下载）。
- 打开后填**电脑 IP**（IPv4 或 IPv6）与**端口**（默认 9000），点连接。
- 需要授予**悬浮窗**和**前台服务**权限（连接后靠一个悬浮层捕获全屏触摸）。
- 「模拟连接（调试用）」：不接真触摸，发模拟数据，用来验证 PC 端链路。
- 电脑 IP 不知道填什么？Receiver 启动时会把本机可用地址都列出来，照着填。

---

## 驱动

驱动是虚拟 HID 触摸屏（VHF）。装上后 Windows 会多出一个"符合 HID 标准的触摸屏"（硬件 ID 含 `VID_1234&PID_0001`），Receiver 用 `--vhid` 就能把平板触摸喂进去。

### 安装包内容

CI 的 `driver` 工作流会产出一个**自包含安装包** `TouchBridgeVhid-driver.zip`，解压后包含：

```
TouchBridgeVhid.sys      驱动本体
TouchBridgeVhid.inf      安装信息
TouchBridgeVhid.cat      目录签名
TouchBridgeTest.cer      测试证书
devcon.exe               安装工具（免装 WDK）
install.ps1              安装脚本
```

### 安装前提：允许测试签名驱动

驱动用测试证书签名，Windows 默认不加载。二选一：

**方式 A · 开启测试签名（推荐，长期有效）**

```bat
bcdedit /set testsigning on
```

然后**重启**。要求 **Secure Boot 处于关闭状态**（BIOS 里关；开着的话 `testsigning` 不生效）。开启后桌面右下角会有"测试模式"水印。

**方式 B · 临时禁用驱动签名强制（一次性，不改系统设置）**

按住 **Shift** 点「重启」→ 疑难解答 → 高级选项 → 启动设置 → 重启 → 按 **7**（或 F7）「禁用驱动程序强制签名」。

- 只对本次开机有效，**每次重启都要重来**。
- Secure Boot 开启时该菜单项通常不可用，仍需先在 BIOS 关掉 Secure Boot。

### 安装 / 卸载

解压后，**管理员**身份运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

脚本会：导入 `TouchBridgeTest.cer` 到「受信任的根」+「受信任的发布者」→ 用 `devcon` 安装或就地更新设备节点。已有旧版会自动 `update`，不用先卸载，也不用重启。

验证：

```powershell
Get-PnpDevice -Class HIDClass | Where-Object { $_.InstanceId -match "1234" }
```

卸载：

```bat
devcon remove ROOT\TouchBridgeVhid
```

### 自己编译驱动

```powershell
# 管理员 PowerShell
cd driver
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

`build.ps1` 一条龙：还原 WDK NuGet 包（首次约 1.5GB）→ 编译 → stampinf → inf2cat → 签名 → devcon 安装。需要本机有 VS 的「使用 C++ 的桌面开发」工作负载。

---

## 校准向导（TouchSuite.App）

主项目，用来把屏幕和手的物理量量出来：

1. 第 1/2 步：用真尺子核对屏幕上的横向/纵向 10cm 标尺
2. 第 3 步：选择物理尺寸推算方式（EDID / 按方形像素推算等）
3. 第 4 步：描手掌轮廓 或 直接手输「手宽 × 手长」
4. 第 5/6 步：手掌、手指各按一下，读驱动上报的接触面积与压感
5. 第 7 步：查看结果，点「保存结果到文件」

结果默认存到 exe 同级的 `calibration.json`（该目录不可写则退回 `%LOCALAPPDATA%`）。

> 第 5/6 步的接触面积/压感依赖**设备上报**：`user32` 合成的触摸（含 `--vhid` 之外的注入）通常不报接触尺寸，真触摸屏才会报。没有真触摸屏时可先用 `--vhid` 的注入做自测。

---

## 构建与发布

| 目标 | 命令 |
|---|---|
| C#（3 个项目） | `dotnet build TouchSuite.sln -c Release` |
| 安卓 APK | `cd android && gradlew assembleDebug`（产物复制到 `dist/`） |
| 驱动 | `driver\build.ps1`（管理员） |

发布用 GitHub Actions：

- **build.yml**：push / PR 到 `main` 时**按改动目录**触发对应构建（改哪个目录编哪个）。
- **release.yml**：手动触发，可**勾选**要打包的 5 个组件（App / App.old / Receiver / 安卓 / 驱动），汇总后创建 Release。
- **driver.yml**：`driver/` 有改动时自动跑，也可手动触发。

## 环境要求

- Windows 10/11
- .NET 10 SDK（C# 项目；Receiver 目标 `net9.0-windows`）
- JDK 17（编安卓）
- VS「使用 C++ 的桌面开发」+ 管理员权限（编/装驱动）