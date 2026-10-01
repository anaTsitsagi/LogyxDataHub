<#
.SYNOPSIS
    Stops the local infrastructure started by start-infra.ps1. Data in .local\infra is kept.
#>
$root  = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$tools = Join-Path $root '.local\tools'

$rabbit = Get-ChildItem $tools -Directory -Filter 'rabbitmq_server-*' -ErrorAction SilentlyContinue | Select-Object -Last 1
if ($rabbit -and (Get-NetTCPConnection -LocalPort 5672 -State Listen -ErrorAction SilentlyContinue)) {
    Write-Host 'Stopping RabbitMQ...'
    $env:ERLANG_HOME   = Join-Path $tools 'erlang'
    $env:RABBITMQ_BASE = Join-Path $root '.local\infra\rabbitmq'
    & "$($rabbit.FullName)\sbin\rabbitmqctl.bat" stop | Out-Null
}

# Only processes started from .local\tools are stopped.
Get-Process weed, mailpit, erl, erlsrv, epmd -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($tools, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Write-Host "Stopping $($_.ProcessName) ($($_.Id))"; Stop-Process -Id $_.Id -Force }

Write-Host 'Local infrastructure stopped.'
