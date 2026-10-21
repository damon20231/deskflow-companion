$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root "src\DeskflowCompanion.cs"
$dist = Join-Path $root "dist"
$output = Join-Path $dist "Deskflow增强控制台.exe"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $csc)) {
    throw "找不到 .NET Framework C# 编译器：$csc"
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    /out:$output `
    /reference:System.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Core.dll `
    $source

if ($LASTEXITCODE -ne 0) {
    throw "编译失败，退出码：$LASTEXITCODE"
}

Write-Host "已生成：$output"
