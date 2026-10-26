param([Parameter(Mandatory=$true)][string]$PythonPath, [switch]$EnableRecovery)
# 输入：已编译插件和 Python 3 路径。输出：本地市场、统一运行设置和一次性去重账本迁移。
# 不修改 Codex 主程序，不设置 Windows 登录启动，不覆盖已有统一插件恢复状态。
$ErrorActionPreference='Stop'
if (-not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) { throw 'Python 路径不存在。' }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'plugin/bin/CodexQuotaUnified.exe'))) { throw '请先编译插件。' }
@{python=[IO.Path]::GetFullPath($PythonPath)} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'plugin/runtime.json') -Encoding utf8
$data=Join-Path $env:LOCALAPPDATA 'CodexQuotaUnified'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$settings=Join-Path $data 'recovery-settings.json'
if (-not (Test-Path -LiteralPath $settings)) {
    @{enabled=$EnableRecovery.IsPresent;generation=[guid]::NewGuid().ToString()} | ConvertTo-Json | Set-Content -LiteralPath $settings -Encoding utf8
} elseif ($EnableRecovery) {
    $current=Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json -AsHashtable
    if (-not $current.enabled) {
        $current.enabled=$true
        $current.generation=[guid]::NewGuid().ToString()
        $current | ConvertTo-Json | Set-Content -LiteralPath $settings -Encoding utf8
    }
}
$state=Join-Path $data 'recovery-state.json'
if (-not (Test-Path -LiteralPath $state)) {
    $old=Join-Path $env:LOCALAPPDATA 'CodexQuotaBattery/recovery-state.json'
    $dispatched=@{}
    if (Test-Path -LiteralPath $old) { $dispatched=(Get-Content -LiteralPath $old -Raw | ConvertFrom-Json -AsHashtable).dispatched }
    @{watched=@{};pending=@{};dispatched=$dispatched} | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $state -Encoding utf8
}
codex plugin marketplace add $PSScriptRoot --json
if ($LASTEXITCODE -ne 0) { throw '统一插件市场注册失败。' }
codex plugin add 'codex-quota-unified@codex-unified-tools' --json
if ($LASTEXITCODE -ne 0) { throw '统一插件安装失败。' }
Write-Output '已安装 Codex 额度与恢复。额度每30秒检测，已保留旧去重记录。'
