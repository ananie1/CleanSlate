<#
 Ручная проверка CleanSlate. Создаёт вымышленную игру TestGame:
   - папки данных в AppData\Local, Roaming, LocalLow, Saved Games, Документах;
   - «ловушку» TestGameBackup (её программа не должна выбирать автоматически);
   - каталог установки и запись в HKCU\...\Uninstall с рабочим деинсталлятором.
 Запуск:   powershell -ExecutionPolicy Bypass -File .\Create-TestGame.ps1
 Уборка:   powershell -ExecutionPolicy Bypass -File .\Create-TestGame.ps1 -Remove
#>
param([switch]$Remove)

$pub = 'TestPublisher'; $name = 'TestGame'
$docs = [Environment]::GetFolderPath('MyDocuments')
$install = Join-Path $env:LOCALAPPDATA 'CleanSlateTest\Install\TestGame'
$regKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TestGame_CleanSlate'
$regCmd = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\TestGame_CleanSlate'

$dirs = @(
  "$env:LOCALAPPDATA\$pub\$name", "$env:APPDATA\$name", "$env:USERPROFILE\AppData\LocalLow\$pub\$name",
  "$env:USERPROFILE\Saved Games\$name", "$docs\My Games\$name", "$env:LOCALAPPDATA\TestGameBackup")

if ($Remove) {
  foreach ($d in $dirs + @("$env:LOCALAPPDATA\$pub", "$env:USERPROFILE\AppData\LocalLow\$pub", "$env:LOCALAPPDATA\CleanSlateTest")) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
  }
  if (Test-Path $regKey) { Remove-Item $regKey -Recurse -Force }
  Write-Host 'Тестовые данные удалены.'; return
}

function New-Data($dir, $sizes) {
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $i = 0
  foreach ($s in $sizes) {
    $b = New-Object byte[] $s; (New-Object Random).NextBytes($b)
    [IO.File]::WriteAllBytes((Join-Path $dir "file$i.bin"), $b); $i++
  }
}

New-Data "$env:LOCALAPPDATA\$pub\$name" @(10KB, 2MB)
New-Data "$env:APPDATA\$name" @(50KB)
Set-Content "$env:APPDATA\$name\settings.ini" "InstallLocation=$install"       # второй признак принадлежности
New-Data "$env:USERPROFILE\AppData\LocalLow\$pub\$name" @(1KB)
New-Data "$env:USERPROFILE\Saved Games\$name" @(5KB)                           # только имя -> «не подтверждено»
New-Data "$docs\My Games\$name" @(300)
New-Data "$env:LOCALAPPDATA\TestGameBackup" @(123KB)                           # ловушка
New-Data $install @(20KB, 1MB)
New-Item -ItemType Directory -Force -Path "$env:LOCALAPPDATA\$pub\$name\EmptyDir" | Out-Null

New-Item -Path $regKey -Force | Out-Null
$uninst = "cmd.exe /c rmdir /s /q `"$install`" & reg delete `"$regCmd`" /f"
Set-ItemProperty $regKey DisplayName $name
Set-ItemProperty $regKey Publisher $pub
Set-ItemProperty $regKey DisplayVersion '1.0.0'
Set-ItemProperty $regKey InstallLocation $install
Set-ItemProperty $regKey UninstallString $uninst
Write-Host "Готово. В CleanSlate нажмите «Обновить список» и найдите TestGame."
