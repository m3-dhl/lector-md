param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$dest    = Join-Path $env:LOCALAPPDATA 'Programs\MdLector'
$exe     = Join-Path $dest 'MdLector.exe'
$progId  = 'MdLector.Markdown'
$classes = 'HKCU:\Software\Classes'
$exts    = '.md', '.markdown', '.mdown', '.mkd', '.mkdn'
$lnk     = Join-Path ([Environment]::GetFolderPath('Programs')) 'Lector MD.lnk'
$uninst  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MdLector'

# Cierra las ventanas de Lector MD de este usuario como si se pulsara la X: si hay cambios sin guardar,
# la app pregunta. Si sigue abierta, se aborta en vez de perder trabajo.
function Close-Lector {
  $procs = @(Get-Process MdLector -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -ieq $exe })
  if (-not $procs.Count) { return }
  foreach ($p in $procs) { [void]$p.CloseMainWindow() }
  foreach ($p in $procs) {
    if (-not $p.WaitForExit(30000)) {
      throw 'Lector MD sigue abierto (¿cambios sin guardar?). Ciérralo y vuelve a ejecutar el instalador.'
    }
  }
}

Add-Type -Namespace Win -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, int f, System.IntPtr a, System.IntPtr b);'

if ($Uninstall) {
  Close-Lector
  foreach ($e in $exts) {
    $k = "$classes\$e"
    if ((Get-ItemProperty $k -ErrorAction SilentlyContinue).'(default)' -eq $progId) { Set-ItemProperty $k '(default)' '' }
    Remove-ItemProperty "$k\OpenWithProgids" $progId -ErrorAction SilentlyContinue
  }
  Remove-Item "$classes\$progId", "$classes\Applications\MdLector.exe", $uninst -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item $lnk -Force -ErrorAction SilentlyContinue
  Remove-Item (Join-Path $env:LOCALAPPDATA 'MdLector') -Recurse -Force -ErrorAction SilentlyContinue
  [Win.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
  # Borrar la carpeta del programa al terminar (este script vive dentro)
  Start-Process cmd.exe -WindowStyle Hidden -ArgumentList "/c timeout /t 2 >nul & rmdir /s /q `"$dest`""
  Write-Host 'Lector MD desinstalado.'
  return
}

# --- copiar archivos ---
$src = Join-Path $PSScriptRoot 'out'
Close-Lector
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$src\*" $dest -Recurse -Force
Copy-Item $PSCommandPath (Join-Path $dest 'install.ps1') -Force
# Quitar la marca de "descargado de Internet" que hereda el zip de la release
Get-ChildItem $dest -Recurse -File | Unblock-File

# --- tipo de archivo ---
$cmd = "`"$exe`" `"%1`""
New-Item "$classes\$progId\shell\open\command" -Force | Out-Null
Set-ItemProperty "$classes\$progId" '(default)' 'Documento Markdown'
Set-ItemProperty "$classes\$progId" 'FriendlyTypeName' 'Documento Markdown'
New-Item "$classes\$progId\DefaultIcon" -Force | Out-Null
Set-ItemProperty "$classes\$progId\DefaultIcon" '(default)' "`"$exe`",0"
Set-ItemProperty "$classes\$progId\shell\open" '(default)' 'Abrir con Lector MD'
Set-ItemProperty "$classes\$progId\shell\open\command" '(default)' $cmd

New-Item "$classes\Applications\MdLector.exe\shell\open\command" -Force | Out-Null
Set-ItemProperty "$classes\Applications\MdLector.exe" 'FriendlyAppName' 'Lector MD'
Set-ItemProperty "$classes\Applications\MdLector.exe\shell\open\command" '(default)' $cmd
New-Item "$classes\Applications\MdLector.exe\SupportedTypes" -Force | Out-Null

foreach ($e in $exts) {
  New-Item "$classes\$e\OpenWithProgids" -Force | Out-Null
  Set-ItemProperty "$classes\$e" '(default)' $progId
  Set-ItemProperty "$classes\$e\OpenWithProgids" $progId ([byte[]]@()) -Type Binary
  Set-ItemProperty "$classes\Applications\MdLector.exe\SupportedTypes" $e ''
  # Si el usuario había elegido otra app con "Usar siempre", esa elección manda: se quita.
  Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$e\UserChoice" -Force -ErrorAction SilentlyContinue
}

# --- acceso directo en Inicio ---
$ws = New-Object -ComObject WScript.Shell
$s = $ws.CreateShortcut($lnk)
$s.TargetPath = $exe; $s.WorkingDirectory = $dest; $s.Description = 'Lector de documentos Markdown'
$s.Save()

# --- entrada en "Aplicaciones instaladas" ---
New-Item $uninst -Force | Out-Null
$size = [int]((Get-ChildItem $dest -Recurse | Measure-Object Length -Sum).Sum / 1KB)
$version = (Get-Item $exe).VersionInfo.FileVersion -replace '\.0$', ''
$props = @{
  DisplayName = 'Lector MD'; DisplayVersion = $version; Publisher = 'Lector MD'; DisplayIcon = "`"$exe`",0"
  InstallLocation = $dest; EstimatedSize = $size; NoModify = 1; NoRepair = 1
  UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dest\install.ps1`" -Uninstall"
}
foreach ($p in $props.GetEnumerator()) {
  $type = if ($p.Value -is [int]) { 'DWord' } else { 'String' }
  New-ItemProperty $uninst $p.Key -Value $p.Value -PropertyType $type -Force | Out-Null
}

[Win.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Host "Lector MD instalado en $dest"


