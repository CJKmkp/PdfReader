# 打包 PdfReader 插件为 .icpx（ZIP）包。
# .icpx 内容：manifest.json、PdfReader.dll、PdfReader.deps.json
param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$csproj = Join-Path $root "PdfReader.csproj"

Write-Host "构建 $Configuration|$Platform ..."
dotnet build $csproj -c $Configuration -p:Platform=$Platform
if ($LASTEXITCODE -ne 0) { throw "构建失败，已中止打包。" }

$outDir = Join-Path $root "bin\$Platform\$Configuration\net6.0-windows10.0.19041.0"
if (-not (Test-Path $outDir)) {
    $outDir = Join-Path $root "bin\$Configuration\net6.0-windows10.0.19041.0"
}

$required = @("PdfReader.dll", "PdfReader.deps.json")
$manifest = Join-Path $root "manifest.json"

$staging = Join-Path $env:TEMP ("pdfreader_pack_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    Copy-Item $manifest (Join-Path $staging "manifest.json") -Force
    foreach ($f in $required) {
        $src = Join-Path $outDir $f
        if (-not (Test-Path $src)) { throw "缺少构建产物：$src" }
        Copy-Item $src (Join-Path $staging $f) -Force
    }

    $icpx = Join-Path $root "PdfReader.icpx"
    if (Test-Path $icpx) { Remove-Item $icpx -Force }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $icpx)
    Write-Host "已生成：$icpx"
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
