@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dpn0.ps1" %*
set "exitCode=%errorlevel%"
echo.
echo 启动脚本已结束，按任意键关闭窗口...
pause >nul
exit /b %exitCode%
