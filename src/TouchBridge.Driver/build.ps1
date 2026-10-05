# TouchBridge 虚拟触摸屏驱动 —— 构建 / 签名 / 安装 一键脚本
#
# 用法（必须以【管理员】身份运行 PowerShell）：
#     powershell -ExecutionPolicy Bypass -File .\build.ps1
#
# 前置条件：
#   1) 已安装 Visual Studio 的「使用 C++ 的桌面开发」工作负载（只要 MSVC 工具链）
#   2) 已开启测试签名并重启过一次：
#          bcdedit /set testsigning on
#      （Secure Boot 必须处于关闭状态，否则 testsigning 无法生效）
#
# 不需要安装 WDK MSI，也不需要 VS 的「Windows 驱动工具包」组件：
#   头文件 / 库 / stampinf / inf2cat / signtool / devcon 全部取自 WDK NuGet 包
#   （Microsoft.Windows.WDK.x64 等，见 packages.config，首次联网还原到 .\packages）。

$ErrorActionPreference = 'Stop'
$here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$pkgDir = Join-Path $here 'packages'
$wdkVer = '10.0.28000.2526'
$w      = Join-Path $pkgDir "Microsoft.Windows.WDK.x64.$wdkVer"
$s      = Join-Path $pkgDir "Microsoft.Windows.SDK.CPP.$wdkVer"
$out    = Join-Path $here 'x64\Release'
$certName = 'TouchBridgeTest'

# 每次构建生成一个比上次更高的驱动版本；否则版本与已安装的相同，PnP 会拒绝更新。
function Get-TbDriverVersion {
    '1.0.{0}.{1}' -f (Get-Date).DayOfYear, (Get-Date -Format 'HHmm')
}

# ---- 0) 管理员 ------------------------------------------------------------
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份运行本脚本。'
}

$nuget = Join-Path $env:TEMP 'nuget.exe'

# ---- 1) 还原 WDK NuGet 包（首次较慢）--------------------------------------
if (-not (Test-Path (Join-Path $w 'c\Include'))) {
    if (-not (Test-Path $nuget)) {
        Write-Host '下载 nuget.exe ...'
        Invoke-WebRequest 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile $nuget
    }
    Write-Host '== nuget restore（首次会下载约 1GB）=='
    & $nuget restore (Join-Path $here 'packages.config') -PackagesDirectory $pkgDir
    if ($LASTEXITCODE -ne 0) { throw "nuget restore 失败（$LASTEXITCODE）" }
}

# ---- 2) 定位包内工具 ------------------------------------------------------
$stampinf = Join-Path $w 'c\bin\10.0.28000.0\x64\stampinf.exe'
$inf2cat  = Join-Path $w 'c\bin\10.0.28000.0\x86\Inf2Cat.exe'
$signtool = Join-Path $s 'c\bin\10.0.28000.0\x64\signtool.exe'
$devcon   = Join-Path $w 'c\tools\10.0.28000.0\x64\devcon.exe'
foreach ($t in @($stampinf, $inf2cat, $signtool, $devcon)) {
    if (-not (Test-Path $t)) { throw "包内缺少工具：$t（请重新 nuget restore）" }
}

# ---- 3) 测试证书（首次自动创建）-------------------------------------------
if (-not (Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq "CN=$certName" })) {
    Write-Host "创建测试证书 CN=$certName ..."
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=$certName" `
        -CertStoreLocation Cert:\LocalMachine\My
    $cerFile = Join-Path $here "$certName.cer"
    Export-Certificate -Cert $cert -FilePath $cerFile | Out-Null
    Import-Certificate -FilePath $cerFile -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Import-Certificate -FilePath $cerFile -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
}

# ---- 4) 编译 + 链接（cl/link，工具链来自 VS，头/库来自 NuGet 包）----------
Write-Host '== 编译 =='
& (Join-Path $here 'build-cl.cmd')
if ($LASTEXITCODE -ne 0) { throw "编译失败（$LASTEXITCODE）" }

$sys = Join-Path $out 'TouchBridgeVhid.sys'
if (-not (Test-Path $sys)) { throw "未生成 $sys" }

# ---- 5) stampinf：.inx → .inf（stampinf 原地改 .inf，故先按 .inf 名拷过去）--
Write-Host '== stampinf =='
Copy-Item (Join-Path $here 'TouchBridgeVhid.inx') (Join-Path $out 'TouchBridgeVhid.inf') -Force
& $stampinf -f (Join-Path $out 'TouchBridgeVhid.inf') -a 'x64' -k '1.15' -d '*' -v (Get-TbDriverVersion)
if ($LASTEXITCODE -ne 0) { throw "stampinf 失败（$LASTEXITCODE）" }
$inf = Join-Path $out 'TouchBridgeVhid.inf'
if (-not (Test-Path $inf)) { throw "stampinf 未生成 $inf" }

# ---- 6) inf2cat：生成 .cat ------------------------------------------------
Write-Host '== inf2cat =='
& $inf2cat /driver:$out /os:10_X64
if ($LASTEXITCODE -ne 0) { throw "inf2cat 失败（$LASTEXITCODE）" }

# ---- 7) 签名 --------------------------------------------------------------
Write-Host '== 签名 =='
& $signtool sign /v /fd SHA256 /sm /s My /n $certName $sys
if ($LASTEXITCODE -ne 0) { throw 'sys 签名失败' }
& $signtool sign /v /fd SHA256 /sm /s My /n $certName (Join-Path $out 'TouchBridgeVhid.cat')
if ($LASTEXITCODE -ne 0) { throw 'cat 签名失败' }

# ---- 8) 安装 / 更新（根枚举设备）------------------------------------------
Write-Host '== 安装/更新 =='
& $devcon find 'ROOT\TouchBridgeVhid' *>$null
if ($LASTEXITCODE -eq 0) {
    # 已有设备节点：就地更新驱动并重启设备（不新增节点，也无需重启系统）
    Write-Host '  已存在 TouchBridge 设备节点 → devcon update（就地更新）'
    & $devcon update $inf 'ROOT\TouchBridgeVhid'
} else {
    & $devcon install $inf 'ROOT\TouchBridgeVhid'
}

Write-Host ''
Write-Host '完成。检查设备（应出现“符合 HID 标准的触摸屏”，硬件 ID 含 VID_1234&PID_0001）：'
Write-Host '  Get-PnpDevice -Class HIDClass | Where-Object { $_.InstanceId -match "1234" }'