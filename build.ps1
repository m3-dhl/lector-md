# Compila Lector MD en .\out. Solo necesita Windows 10/11 (csc de .NET Framework 4.x y tar vienen de serie).
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$deps = Join-Path $root 'deps'
$out  = Join-Path $root 'out'

# --- dependencias (versiones fijas, se descargan una vez) ---
$packages = @{
  'marked'      = 'https://registry.npmjs.org/marked/-/marked-18.2.1.tgz'
  'dompurify'   = 'https://registry.npmjs.org/dompurify/-/dompurify-3.4.18.tgz'
  'highlightjs' = 'https://registry.npmjs.org/@highlightjs/cdn-assets/-/cdn-assets-11.11.1.tgz'
  'webview2'    = 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.2903.40/microsoft.web.webview2.1.0.2903.40.nupkg'
}
foreach ($p in $packages.GetEnumerator()) {
  $dir = Join-Path $deps $p.Key
  if (Test-Path $dir) { continue }
  New-Item -ItemType Directory -Force $dir | Out-Null
  $file = Join-Path $deps ($p.Key + '.archive')
  Write-Host "Descargando $($p.Key)..."
  Invoke-WebRequest $p.Value -OutFile $file -UseBasicParsing
  tar -xf $file -C $dir
  if ($LASTEXITCODE -ne 0) { throw "No se pudo extraer $($p.Key)" }
  Remove-Item $file
}

# --- salida ---
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$out\web" | Out-Null
Copy-Item "$src\web\*" "$out\web\"
Copy-Item "$deps\marked\package\lib\marked.umd.js" "$out\web\"
Copy-Item "$deps\dompurify\package\dist\purify.min.js" "$out\web\"
Copy-Item "$deps\highlightjs\package\highlight.min.js" "$out\web\"
Copy-Item "$deps\highlightjs\package\styles\github.min.css" "$out\web\hl-light.css"
Copy-Item "$deps\highlightjs\package\styles\github-dark.min.css" "$out\web\hl-dark.css"
Copy-Item "$deps\webview2\lib\net462\Microsoft.Web.WebView2.Core.dll" $out
Copy-Item "$deps\webview2\lib\net462\Microsoft.Web.WebView2.WinForms.dll" $out
Copy-Item "$deps\webview2\runtimes\win-x64\native\WebView2Loader.dll" $out
Copy-Item "$src\app.ico" $out

# --- compilar ---
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /win32icon:"$src\app.ico" `
  /out:"$out\MdLector.exe" `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
  /r:"$out\Microsoft.Web.WebView2.Core.dll" /r:"$out\Microsoft.Web.WebView2.WinForms.dll" `
  "$src\MdLector.cs"
if ($LASTEXITCODE -ne 0) { throw 'Fallo de compilación' }
Write-Host "Compilado en $out"
