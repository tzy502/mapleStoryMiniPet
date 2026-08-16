# MiniPet Windows 构建与发布脚本（在 Windows 上运行）
# 用法：
#   .\scripts\build-win.ps1            # 仅编译 + 测试
#   .\scripts\build-win.ps1 -Publish   # 编译 + 测试 + self-contained win-x64 发布
#
# 前提：已安装 .NET 9 SDK（https://dotnet.microsoft.com/download）
param(
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot

Write-Host "=== MiniPet Windows 构建 ===" -ForegroundColor Cyan
Write-Host "工作目录: $Root"

# 0) 前置检查
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host "[错误] 未找到 dotnet，请先安装 .NET 9 SDK: https://dotnet.microsoft.com/download" -ForegroundColor Red
    exit 1
}
Write-Host "dotnet: $($dotnet.Source)"

# 1) Debug 编译（0 错误）
#    -p:UsedAvaloniaProducts= 关闭 Avalonia 的 used-products 静态分析（本项目未声明该属性，
#    空值可避免个别环境下的构建期分析告警/失败；语义与 macOS 验收命令一致）
Write-Host "`n[1/3] dotnet build -c Debug ..." -ForegroundColor Yellow
dotnet build "$Root\MiniPet\MiniPet.csproj" -c Debug -p:UsedAvaloniaProducts=
if ($LASTEXITCODE -ne 0) { Write-Host "[错误] 编译失败" -ForegroundColor Red; exit $LASTEXITCODE }

# 2) 测试
Write-Host "`n[2/3] dotnet test ..." -ForegroundColor Yellow
dotnet test "$Root\MiniPet.Tests\MiniPet.Tests.csproj" -c Debug --no-build
if ($LASTEXITCODE -ne 0) { Write-Host "[错误] 测试失败" -ForegroundColor Red; exit $LASTEXITCODE }

if ($Publish) {
    # 3) self-contained win-x64 发布（含 .NET 运行时，目标机免装 .NET）
    Write-Host "`n[3/3] dotnet publish -r win-x64 --self-contained ..." -ForegroundColor Yellow
    dotnet publish "$Root\MiniPet\MiniPet.csproj" -c Release -r win-x64 --self-contained true -p:UsedAvaloniaProducts=
    if ($LASTEXITCODE -ne 0) { Write-Host "[错误] 发布失败" -ForegroundColor Red; exit $LASTEXITCODE }
    $publishDir = "$Root\MiniPet\bin\Release\net9.0\win-x64\publish"
    Write-Host "`n发布完成: $publishDir" -ForegroundColor Green
    Write-Host "运行: $publishDir\MiniPet.exe（self-contained，免装 .NET）" -ForegroundColor Green
}
else {
    Write-Host "`n构建 + 测试通过。加 -Publish 参数执行 self-contained win-x64 发布。" -ForegroundColor Green
}
