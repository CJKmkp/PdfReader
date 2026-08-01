# 打包 PdfReader 插件为 .icpx（ZIP）包。
# .icpx 内容：manifest.json、PdfReader.dll、PdfReader.deps.json
# 文件名必须与 PluginIndex 里 downloadPath 声明的一致（插件 id），否则市场下载会 404。
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

$manifest = Join-Path $root "manifest.json"

$staging = Join-Path $env:TEMP ("pdfreader_pack_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    Copy-Item $manifest (Join-Path $staging "manifest.json") -Force

    # 插件自带全部运行时依赖（iNKORE 三件套、SDK、DI 等），随 .icpx 分发。
    # 这些 DLL 由 CopyLocalLockFileAssemblies=true 复制到输出目录。
    $runtimeFiles = Get-ChildItem $outDir -File | Where-Object { $_.Extension -in ".dll", ".deps.json" }
    if (-not $runtimeFiles) { throw "输出目录没有可打包的 DLL：$outDir" }
    foreach ($f in $runtimeFiles) {
        Copy-Item $f.FullName (Join-Path $staging $f.Name) -Force
    }

    $icpx = Join-Path $root "com.icc.pdf-reader.icpx"
    if (Test-Path $icpx) { Remove-Item $icpx -Force }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $icpx)
    Write-Host "已生成：$icpx"
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
