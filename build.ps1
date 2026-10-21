$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist"
$output = Join-Path $dist "Deskflow增强控制台.exe"
$tempOutput = Join-Path $dist "_deskflow_companion_build.exe"
$resource = Join-Path $root "assets\mac-transfer.html"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $csc)) {
    throw "找不到 .NET Framework C# 编译器：$csc"
}
if (-not (Test-Path -LiteralPath $resource)) {
    throw "找不到传输页面资源：$resource"
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root "src") -Filter "*.cs" | Sort-Object Name | ForEach-Object { $_.FullName })

& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    /out:$tempOutput `
    /reference:System.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Core.dll `
    "/resource:$resource,DeskflowCompanion.mac-transfer.html" `
    $sources

if ($LASTEXITCODE -ne 0) {
    throw "编译失败，退出码：$LASTEXITCODE"
}

[System.IO.File]::Copy($tempOutput, $output, $true)
Remove-Item -Force -LiteralPath $tempOutput

Write-Host "已生成：$output"
