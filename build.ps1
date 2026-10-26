param([string]$Dotnet='dotnet')
# 输入：.NET 10 SDK；输出：plugin/bin 的 Windows x64 自包含程序，不包含 Python。
$ErrorActionPreference='Stop'
& $Dotnet publish (Join-Path $PSScriptRoot 'src/CodexStatusbar/CodexStatusbar.csproj') -c Release -r win-x64 --self-contained true --nologo -o (Join-Path $PSScriptRoot 'plugin/bin')
if($LASTEXITCODE -ne 0){throw '插件构建失败。'}
