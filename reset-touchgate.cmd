@echo off
chcp 65001 >nul
rem ===========================================================================
rem  reset-touchgate.cmd —— 一键恢复 Windows 触摸（TouchGate）
rem
rem  背景：HKCU\Software\Microsoft\Wisp\Touch\TouchGate = 0 会**连同 user32 的
rem        注入触摸一起禁用**（实测：注入 API 全返回成功，但系统完全不动、
rem        连涟漪都没有）。而且 TouchGate 是【登录时】读取的 —— 光改注册表
rem        不注销，触摸不会回来，看起来就像"改不回去"。
rem
rem  用法：
rem      reset-touchgate.cmd         把 TouchGate 设为 1（启用触摸，推荐）
rem      reset-touchgate.cmd del     删除 TouchGate 值（= 默认，同样启用）
rem
rem  改完请【注销并重新登录】（或重启）才会生效。
rem ===========================================================================

setlocal
set "KEY=HKCU\Software\Microsoft\Wisp\Touch"

echo.
echo === 1) 结束可能还在运行的 TouchSuite.HidTouchBridge（它会自己改这个值）===
taskkill /IM TouchSuite.HidTouchBridge.exe /F >nul 2>&1
if errorlevel 1 (echo     没有在运行) else (echo     已结束)

echo.
echo === 2) 修改前的值 ===
reg query "%KEY%" /v TouchGate 2>nul
if errorlevel 1 echo     TouchGate 不存在（等同启用）

echo.
if /I "%~1"=="del" (
    echo === 3) 删除 TouchGate 值 ===
    reg delete "%KEY%" /v TouchGate /f >nul 2>&1
) else (
    echo === 3) 把 TouchGate 设为 1 ===
    reg add "%KEY%" /v TouchGate /t REG_DWORD /d 1 /f >nul 2>&1
)
if errorlevel 1 (
    echo     [失败] 写注册表被拒绝。请用**管理员**身份重新运行本脚本。
    goto :end
)

echo.
echo === 4) 修改后的值 ===
reg query "%KEY%" /v TouchGate 2>nul
if errorlevel 1 echo     TouchGate 不存在（等同启用，触摸已开）

echo.
echo ===========================================================================
echo  请【注销并重新登录】（或重启）后才会真正生效。
echo  验证：手指直接摸屏幕，应该能正常点按。
echo.
echo  注：触摸视觉反馈（涟漪）由「设置 - 蓝牙和其他设备 - 触摸 - 显示触摸
echo      视觉反馈」控制，和 TouchGate 无关（TouchUI=0 是系统默认值）。
echo ===========================================================================
echo.

:end
pause
