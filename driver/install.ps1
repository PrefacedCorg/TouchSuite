# TouchBridge 虚拟触摸屏驱动 —— 安装脚本（随包自包含，无需 VS / WDK）
#
# 用法（必须以【管理员】身份运行 PowerShell）：
#     powershell -ExecutionPolicy Bypass -File .\install.ps1
#
# 前置条件：
#   - Windows 10/11 x64
#   - 已开启测试签名并重启过一次： bcdedit /set testsigning on
#     （Secure Boot 必须处于关闭状态，否则 testsigning 不生效）
#
# 本脚本做两件事：
#   1) 把随包的测试证书 TouchBridgeTest.cer 导入「受信任的根」与「受信任的发布者」
#   2) 用 devcon 安装 / 就地更新 ROOT\TouchBridgeVhid 设备节点
#
# 卸载： devcon remove ROOT\TouchBridgeVhid   （或直接在设备管理器里卸载）

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---- 0) 管理员 ------------------------------------------------------------
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份运行本脚本。'
}

$inf    = Join-Path $here 'TouchBridgeVhid.inf'
$cer    = Join-Path $here 'TouchBridgeTest.cer'
$devcon = Join-Path $here 'devcon.exe'
foreach ($f in @($inf, $cer, $devcon)) {
    if (-not (Test-Path $f)) { throw "缺少文件：$f（请确认解压完整）" }
}

# ---- 1) 测试签名模式检查（仅提示，不强制）---------------------------------
try {
    $bcd = (& bcdedit /enum '{current}') -join "`n"
    if ($bcd -notmatch 'testsigning\s+Yes') {
        Write-Warning '当前似乎未开启测试签名模式。若安装后设备报「无法启动（代码 52 等）」，请执行：'
        Write-Warning '  bcdedit /set testsigning on    （Secure Boot 需关闭）并重启后重试'
    }
} catch {
    Write-Warning '无法读取 bcdedit 状态，请自行确认已开启测试签名模式。'
}

# ---- 2) 导入测试证书 ------------------------------------------------------
Write-Host '导入测试证书到 受信任的根 / 受信任的发布者 ...'
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null

# ---- 3) devcon 安装 / 更新 ------------------------------------------------
Write-Host '安装/更新驱动 ...'
& $devcon find 'ROOT\TouchBridgeVhid' *>$null
if ($LASTEXITCODE -eq 0) {
    # 已有设备节点：就地更新驱动并重启设备（不新增节点，也无需重启系统）
    Write-Host '  已存在 TouchBridge 设备节点 → devcon update（就地更新）'
    & $devcon update $inf 'ROOT\TouchBridgeVhid'
} else {
    & $devcon install $inf 'ROOT\TouchBridgeVhid'
}
if ($LASTEXITCODE -ne 0) { throw "devcon 失败（$LASTEXITCODE）" }

Write-Host ''
Write-Host '完成。检查设备（应出现“符合 HID 标准的触摸屏”，硬件 ID 含 VID_1234&PID_0001）：'
Write-Host '  Get-PnpDevice -Class HIDClass | Where-Object { $_.InstanceId -match "1234" }'