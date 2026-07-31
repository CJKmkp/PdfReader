# 构建 PdfReader 插件并把产物复制到宿主插件目录，便于本地调试。
# 用法： .\build-and-run.ps1 [-HostDir "e:\ICC CE\ICC CE main\community\bin\x64\Debug\net6.0-windows10.0.19041.0"]
param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$HostDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$csproj = Join-Path $root "PdfReader.csproj"

Write-Host "构建 $Configuration|$Platform ..."
dotnet build $csproj -c $Configuration -p:Platform=$Platform
if ($LASTEXITCODE -ne 0) { throw "构建失败。" }

$outDir = Join-Path $root "bin\$Platform\$Configuration\net6.0-windows10.0.19041.0"
if (-not (Test-Path $outDir)) {
    $outDir = Join-Path $root "bin\$Configuration\net6.0-windows10.0.19041.0"
}

if ([string]::IsNullOrWhiteSpace($HostDir)) {
    Write-Host "构建产物位于：$outDir"
    Write-Host "未指定 -HostDir，跳过复制。用 pack.ps1 生成 .icpx 后从插件市场安装即可。"
    return
}

$pluginDest = Join-Path $HostDir "Plugins\com.icc.pdf-reader"
if (-not (Test-Path $pluginDest)) { New-Item -ItemType Directory -Path $pluginDest -Force | Out-Null }

Copy-Item (Join-Path $root "manifest.json") $pluginDest -Force
Copy-Item (Join-Path $outDir "PdfReader.dll") $pluginDest -Force
Copy-Item (Join-Path $outDir "PdfReader.deps.json") $pluginDest -Force
Write-Host "已复制到：$pluginDest"
