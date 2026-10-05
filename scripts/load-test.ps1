<#
.SYNOPSIS
    Load test against the local cluster (scripts\local\k8s-up.ps1): runs scripts\local\smoke-test.ps1 for each
    ZIP, -Concurrency at a time, while sampling every container's CPU and memory and the worker's /tmp use
    every ~2 s. Writes samples.csv, runs.csv and summary.txt to .local\load-test\<label>-<time>.
    Sampling uses `docker stats` inside the Rancher Desktop VM (the cluster runs on its Docker engine; it is
    faster than metrics-server's 15 s resolution, which misses short peaks).
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\load-test.ps1 -Label five-at-once -Concurrency 5 `
        -Zips ".local\test-data\HIRO.zip,.local\test-data\AILABI.zip,.local\test-data\ALTERA_SOLUSHEN.zip,.local\test-data\HIRO.zip,.local\test-data\AILABI.zip"
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\load-test.ps1 -Label idle -IdleSeconds 60    # baseline, no uploads
#>
param(
    # Comma-separated ZIP paths (one string, so it also works with powershell -File).
    [string]$Zips = "",
    [int]$Concurrency = 1,
    [string]$Label = "run",
    # Sample this long before the uploads start (and with no -Zips: the whole run, for an idle baseline).
    [int]$IdleSeconds = 10,
    [int]$TimeoutSeconds = 1800,
    [string]$Namespace = "datahub-local"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$smoke = Join-Path $root "scripts\local\smoke-test.ps1"
$out = Join-Path $root (".local\load-test\{0}-{1:yyyyMMdd-HHmmss}" -f $Label, (Get-Date))
New-Item -ItemType Directory -Force $out | Out-Null
$zipList = @($Zips.Split(",") | ForEach-Object { $_.Trim() } | Where-Object { $_ } | ForEach-Object { (Resolve-Path $_).Path })

function To-WslPath([string]$p) { "/mnt/" + $p.Substring(0, 1).ToLower() + ($p.Substring(2) -replace "\\", "/") }

# --- Sampler (runs in the Rancher Desktop VM) -----------------------------------
$stopFile = Join-Path $out "stop"
$rawFile = Join-Path $out "samples.raw"
$sampler = @'
#!/bin/sh
# Every container of the namespace: "epoch|container_pod|cpu%|mem usage / limit|tmp MiB" (tmp only for the worker).
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
out="$1"; stop="$2"; ns="$3"
while [ ! -f "$stop" ]; do
  t=$(date +%s)
  docker stats --no-stream --format '{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}' | grep "^k8s_" | grep "_${ns}_" | grep -v "^k8s_POD_" |
  while IFS='|' read -r name cpu mem; do
    tmp=""
    case "$name" in k8s_worker_*)
      dir=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/tmp"}}{{.Source}}{{end}}{{end}}' "$name" 2>/dev/null)
      [ -n "$dir" ] && tmp=$(du -sm "$dir" 2>/dev/null | cut -f1) ;;
    esac
    echo "$t|$name|$cpu|$mem|$tmp" >> "$out"
  done
done
'@
$samplerFile = Join-Path $out "sampler.sh"
[IO.File]::WriteAllText($samplerFile, $sampler.Replace("`r`n", "`n"))
$samplerProc = Start-Process wsl -ArgumentList "-d", "rancher-desktop", "sh", (To-WslPath $samplerFile), (To-WslPath $rawFile), (To-WslPath $stopFile), $Namespace `
    -WindowStyle Hidden -PassThru
Write-Host "Sampling into $out"
Start-Sleep $IdleSeconds

# --- Uploads ------------------------------------------------------------------
$runs = New-Object System.Collections.Generic.List[object]
$queue = New-Object System.Collections.Queue
$i = 0; foreach ($z in $zipList) { $queue.Enqueue([pscustomobject]@{ Index = ++$i; Zip = $z }) }
$active = @()
$started = Get-Date
while ($queue.Count -gt 0 -or $active.Count -gt 0) {
    while ($queue.Count -gt 0 -and $active.Count -lt $Concurrency) {
        $item = $queue.Dequeue()
        $job = Start-Job -ArgumentList $smoke, $item.Zip -ScriptBlock {
            param($script, $zip)
            & powershell -NoProfile -ExecutionPolicy Bypass -File $script -ZipPath $zip -NoSeqCheck -TimeoutSeconds 1500 2>&1 | Out-String
        }
        $active += [pscustomobject]@{ Job = $job; Item = $item; Start = Get-Date }
        Write-Host ("  started #{0} {1}" -f $item.Index, (Split-Path $item.Zip -Leaf))
    }
    Start-Sleep 2
    foreach ($a in @($active | Where-Object { $_.Job.State -ne "Running" })) {
        $text = Receive-Job $a.Job | Out-String
        Remove-Job $a.Job
        $active = @($active | Where-Object { $_ -ne $a })
        $upload = [regex]::Match($text, "parts uploaded \((\d+) x ([\d.]+) MB in ([\d.]+) s, ([\d.]+) MB/s\)")
        $jobSec = [regex]::Match($text, "job ([\d.]+) s")
        $run = [pscustomobject]@{
            Index = $a.Item.Index
            Zip = Split-Path $a.Item.Zip -Leaf
            SizeMB = [Math]::Round((Get-Item $a.Item.Zip).Length / 1MB, 1)
            Passed = $text -match "Smoke test passed"
            Parts = $upload.Groups[1].Value
            UploadSeconds = $upload.Groups[3].Value
            UploadMBps = $upload.Groups[4].Value
            JobSeconds = $jobSec.Groups[1].Value
            TotalSeconds = [Math]::Round(((Get-Date) - $a.Start).TotalSeconds, 1)
        }
        $runs.Add($run)
        [IO.File]::WriteAllText((Join-Path $out ("run-{0}.log" -f $a.Item.Index)), $text)
        Write-Host ("  finished #{0} {1}: passed={2} upload {3} MB/s, job {4} s" -f $run.Index, $run.Zip, $run.Passed, $run.UploadMBps, $run.JobSeconds)
    }
    if (((Get-Date) - $started).TotalSeconds -gt $TimeoutSeconds) { Write-Warning "Timeout; stopping"; $active | ForEach-Object { Stop-Job $_.Job }; break }
}
if ($zipList.Count -eq 0) { Write-Host "Idle baseline only." }
Start-Sleep 6   # catch the tail of the last job
[IO.File]::WriteAllText($stopFile, "")
$samplerProc.WaitForExit(30000) | Out-Null

# --- Summarize ----------------------------------------------------------------
function To-MiB([string]$v) {
    $m = [regex]::Match($v.Trim(), "^([\d.]+)\s*([KMG]i?B|B)")
    if (-not $m.Success) { return 0 }
    $n = [double]$m.Groups[1].Value
    switch -regex ($m.Groups[2].Value) { "^K" { $n / 1024 } "^M" { $n } "^G" { $n * 1024 } default { $n / 1048576 } }
}
$samples = Get-Content $rawFile | Where-Object { $_ } | ForEach-Object {
    $f = $_.Split("|")
    $parts = $f[1].Split("_")   # k8s_<container>_<pod>_<namespace>_<uid>_<restart>
    [pscustomobject]@{
        Time = [int64]$f[0]
        Container = $parts[1]
        Pod = $parts[2]
        CpuMilli = [Math]::Round([double]($f[2].TrimEnd("%")) * 10)
        MemMiB = [Math]::Round((To-MiB ($f[3].Split("/")[0])), 0)
        TmpMiB = if ($f[4]) { [int]$f[4] } else { $null }
    }
}
$samples | Export-Csv -NoTypeInformation (Join-Path $out "samples.csv")
$runs | Export-Csv -NoTypeInformation (Join-Path $out "runs.csv")

$t0 = ($samples | Measure-Object Time -Minimum).Minimum
$idleEnd = $t0 + $IdleSeconds
$summary = $samples | Group-Object Container | Sort-Object Name | ForEach-Object {
    $idle = @($_.Group | Where-Object { $_.Time -lt $idleEnd })
    [pscustomobject]@{
        Container = $_.Name
        IdleCpuM = if ($idle) { [Math]::Round(($idle | Measure-Object CpuMilli -Average).Average) } else { "" }
        PeakCpuM = ($_.Group | Measure-Object CpuMilli -Maximum).Maximum
        IdleMemMiB = if ($idle) { [Math]::Round(($idle | Measure-Object MemMiB -Average).Average) } else { "" }
        PeakMemMiB = ($_.Group | Measure-Object MemMiB -Maximum).Maximum
        PeakTmpMiB = ($_.Group | Where-Object { $_.TmpMiB -ne $null } | Measure-Object TmpMiB -Maximum).Maximum
    }
}
$text = @(
    "Load test '$Label': $($zipList.Count) upload(s), concurrency $Concurrency, $((Get-Date).ToString('u'))"
    "CPU in millicores (100 = 0.1 core), memory in MiB; idle = the first $IdleSeconds s before any upload."
    ($summary | Format-Table -AutoSize | Out-String)
    ($runs | Format-Table -AutoSize | Out-String)
) -join "`r`n"
[IO.File]::WriteAllText((Join-Path $out "summary.txt"), $text)
Write-Host $text
if (@($runs | Where-Object { -not $_.Passed }).Count) { Write-Host "Some runs failed; see run-*.log in $out" -ForegroundColor Red; exit 1 }
exit 0
