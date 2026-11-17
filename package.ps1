# Empaqueta la release: .\build.ps1 y después .\package.ps1  ->  dist\LectorMD-<versión>.zip
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = (Get-Item "$root\out\MdLector.exe").VersionInfo.FileVersion -replace '\.0$', ''
$stage = Join-Path $root 'dist\stage'
$zip = Join-Path $root "dist\LectorMD-$version.zip"
Remove-Item (Join-Path $root 'dist') -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item "$root\out" $stage -Recurse
Copy-Item "$root\install.ps1", "$root\Instalar.cmd", "$root\LICENSE" $stage
# Rutas con "/" dentro del zip (PowerShell 5 escribe "\", que rompe otros descompresores)
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
$base = (Get-Item $stage).FullName.TrimEnd('\') + '\'
foreach ($f in Get-ChildItem $base -Recurse -File) {
  $name = $f.FullName.Substring($base.Length).Replace('\', '/')
  [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $name, 'Optimal') | Out-Null
}
$archive.Dispose()
Remove-Item $stage -Recurse -Force
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Write-Host "$zip`nSHA-256: $sha"
