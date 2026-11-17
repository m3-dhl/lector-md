# Compila Lector MD en .\out. Solo necesita Windows 10/11 (csc de .NET Framework 4.x y tar vienen de serie).
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$deps = Join-Path $root 'deps'
$out  = Join-Path $root 'out'

# --- dependencias: versión fija + SHA-512 oficial (npm dist.integrity / NuGet packageHash) ---
$packages = @(
  @{ Name = 'marked'
     Url  = 'https://registry.npmjs.org/marked/-/marked-18.2.1.tgz'
     Hash = 'ii4bxCTCqqwpT86XZWgJNs+ZnAIZksmLGYMViWA7PDIXYIeNo1x6cDdX3JOip9NM/cI6p8z57FxliACboTruMA==' }
  @{ Name = 'dompurify'
     Url  = 'https://registry.npmjs.org/dompurify/-/dompurify-3.4.18.tgz'
     Hash = 'ofe3wI/uNm68HInABYFJYqQxRw1EaKAjNdrFVKqjUY+UZurvNd2L/HuQe097s/4RivWiZyP6tT7aoT8atjfhwg==' }
  @{ Name = 'highlightjs'
     Url  = 'https://registry.npmjs.org/@highlightjs/cdn-assets/-/cdn-assets-11.11.1.tgz'
     Hash = 'VEPdHzwelZ12hEX18BHduqxMZGolcUsrbeokHYxOUIm8X2+M7nx5QPtPeQgRxR9XjhdLv4/7DD5BWOlSrJ3k7Q==' }
  @{ Name = 'webview2'
     Url  = 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.2903.40/microsoft.web.webview2.1.0.2903.40.nupkg'
     Hash = 'pDLSrpyma3xyOGXlw3Aowy7XceBdeJbukZzKLVdPCFvyfnn8PrCLylMn6c456ja0TcOOal8IdQCVONDRU2OGkw==' }
)
New-Item -ItemType Directory -Force $deps | Out-Null
foreach ($p in $packages) {
  $dir = Join-Path $deps $p.Name
  if (Test-Path (Join-Path $dir '.ok')) { continue }
  Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
  $file = Join-Path $deps ($p.Name + '.download')
  Write-Host "Descargando $($p.Name)..."
  Invoke-WebRequest $p.Url -OutFile $file -UseBasicParsing
  $sha = [Security.Cryptography.SHA512]::Create()
  $actual = [Convert]::ToBase64String($sha.ComputeHash([IO.File]::ReadAllBytes($file)))
  if ($actual -ne $p.Hash) { Remove-Item $file; throw "Hash incorrecto en $($p.Name): la descarga no es la esperada." }
  $tmp = "$dir.tmp"
  Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Force $tmp | Out-Null
  tar -xf $file -C $tmp
  if ($LASTEXITCODE -ne 0) { throw "No se pudo extraer $($p.Name)" }
  Remove-Item $file
  New-Item -ItemType File (Join-Path $tmp '.ok') | Out-Null
  Rename-Item $tmp $dir
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
New-Item -ItemType Directory -Force "$out\licenses" | Out-Null
Copy-Item "$root\LICENSE" "$out\licenses\LectorMD.txt"
Copy-Item "$deps\marked\package\LICENSE" "$out\licenses\marked.txt"
Copy-Item "$deps\dompurify\package\LICENSE" "$out\licenses\dompurify.txt"
Copy-Item "$deps\highlightjs\package\LICENSE" "$out\licenses\highlightjs.txt"
Copy-Item "$deps\webview2\LICENSE.txt" "$out\licenses\webview2.txt"

# --- compilar ---
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /win32icon:"$src\app.ico" `
  /out:"$out\MdLector.exe" `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
  /r:"$out\Microsoft.Web.WebView2.Core.dll" /r:"$out\Microsoft.Web.WebView2.WinForms.dll" `
  "$src\MdLector.cs"
if ($LASTEXITCODE -ne 0) { throw 'Fallo de compilación' }
Write-Host "Compilado en $out"
