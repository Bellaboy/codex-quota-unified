param([string]$Executable = (Join-Path $PSScriptRoot '../plugin/bin/CodexQuotaUnified.exe'),
      [string]$Report = (Join-Path $PSScriptRoot '../verification/mcp-lifecycle.json'))
$ErrorActionPreference = 'Stop'
$reportDirectory = Split-Path -Parent ([System.IO.Path]::GetFullPath($Report))
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$psi = [System.Diagnostics.ProcessStartInfo]::new([System.IO.Path]::GetFullPath($Executable))
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.ArgumentList.Add('--mcp')
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $psi
$results = @()
function Invoke-Mcp([string]$request) {
    $process.StandardInput.WriteLine($request)
    $process.StandardInput.Flush()
    $line = $process.StandardOutput.ReadLineAsync()
    if (-not $line.Wait(5000)) { throw 'MCP response timed out' }
    if ($null -eq $line.Result) { throw 'MCP ended before response' }
    return $line.Result | ConvertFrom-Json
}
try {
    if (-not $process.Start()) { throw 'Could not start plugin' }
    $errors = $process.StandardError.ReadToEndAsync()
    $init = Invoke-Mcp '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"lifecycle-test","version":"1"}}}'
    if ($init.result.protocolVersion -ne '2025-03-26' -or $init.id -ne 1) { throw 'Invalid handshake' }
    $results += 'MCP initialize passed'
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $tools = Invoke-Mcp '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
    if ($tools.result.tools[0].name -ne 'get_quota_status' -or -not $tools.result.tools[0].annotations.readOnlyHint) { throw 'Invalid tools' }
    $results += 'Read-only tool catalog passed'
    $status = Invoke-Mcp '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_quota_status","arguments":{}}}'
    if ($status.result.content[0].type -ne 'text') { throw 'Missing status result' }
    $results += 'Read-only status invocation passed'
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(5000)) { throw 'Plugin remained alive after Codex pipe EOF' }
    if ($process.ExitCode -ne 0) { throw "Unexpected plugin exit $($process.ExitCode)" }
    $results += 'STDIO EOF -> process exited0 within5s'
    $stderr = $errors.GetAwaiter().GetResult()
    if ($stderr) { throw "Plugin emitted unexpected stderr" }
    $children = @(Get-CimInstance Win32_Process -Filter ('ParentProcessId = ' + $process.Id) -ErrorAction SilentlyContinue)
    if ($children.Count -ne 0) { throw 'Plugin child process survived EOF' }
    $results += 'No surviving child process'
    @{ passed=$true; processId=$process.Id; checks=$results; executable=[System.IO.Path]::GetFullPath($Executable) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Report -Encoding utf8
    $results | Write-Output
} finally {
    if ($process.Id -and -not $process.HasExited) { $process.Kill($true) }
    $process.Dispose()
}
