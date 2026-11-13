@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -Command "$phone = Read-Host 'iPhone Wi-Fi IPv4 address'; $ip = $null; if ([Net.IPAddress]::TryParse($phone, [ref]$ip) -and $ip.AddressFamily -eq 'InterNetwork') { Start-Process -FilePath '.\AirCursorWindows.exe' -ArgumentList '--phone', $ip.ToString() } else { Write-Host 'Invalid IPv4 address'; exit 1 }"
if errorlevel 1 pause
