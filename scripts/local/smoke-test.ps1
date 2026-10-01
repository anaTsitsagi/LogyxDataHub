<#
.SYNOPSIS
    End-to-end smoke test of a running DataHub: invitation (API) -> link and OTP (portal, code read from
    Mailpit) -> chunked upload -> worker -> status -> reports. Exits 0 if every check passes, 1 otherwise.
    Defaults target the local cluster (scripts\local\k8s-up.ps1); for `dotnet run` pass the localhost URLs.
    Needs the Local environment (POST /dev/token) and Mailpit. The ZIP is an ORIS company folder zipped
    outside the repository (real customer data: never commit it; .local\ is gitignored).
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\local\smoke-test.ps1 -ZipPath .local\test-data\HIRO.zip
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\local\smoke-test.ps1 -ZipPath .local\test-data\HIRO.zip `
        -Api https://localhost:7174 -Portal https://localhost:7049 -Mailpit http://localhost:8025 -Seq http://localhost:5341
#>
param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [string]$Api = "https://api.datahub.localtest.me",
    [string]$Portal = "https://datahub.localtest.me",
    [string]$Mailpit = "http://mailpit.localtest.me",
    # Optional: also check that the link token never reached Seq.
    [string]$Seq = "http://seq.localtest.me",
    [string]$FromDate = "2024-01-01",
    [string]$ToDate = "2024-12-31",
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # PowerShell 5.1's progress bar slows uploads down a lot
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Local certificates are self-signed (k8s-up.ps1) or the dev certificate; skip validation only for local hosts.
$localHosts = @($Api, $Portal) | ForEach-Object { ([Uri]$_).Host } | Where-Object { $_ -eq "localhost" -or $_ -like "*.localtest.me" }
if ($localHosts.Count -ne 2) { throw "Refusing to run against non-local hosts ($Api, $Portal): the smoke test uses dev tokens." }
# A compiled callback: a PowerShell scriptblock fails when .NET calls it on a thread without a runspace.
if (-not ("SmokeTest.TrustAll" -as [type])) {
    Add-Type -TypeDefinition @"
namespace SmokeTest {
    public static class TrustAll {
        public static void Install() {
            System.Net.ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, e) => true;
        }
    }
}
"@
}
[SmokeTest.TrustAll]::Install()

$zip = (Resolve-Path $ZipPath).Path
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "  PASS  $name $detail" } else { Write-Host "  FAIL  $name $detail" -ForegroundColor Red; $failures.Add($name) }
}
function Get-Status([scriptblock]$request) {
    try { (& $request).StatusCode } catch [Net.WebException] { [int]$_.Exception.Response.StatusCode }
}

$suffix = Get-Random -Minimum 10000000 -Maximum 99999999
$companyCode = "4$suffix"
$email = "smoke-$suffix@example.ge"
$started = [DateTime]::UtcNow
Write-Host "Smoke test: company $companyCode, $([Math]::Round((Get-Item $zip).Length / 1MB, 1)) MB ZIP"

# --- 1. API: token and invitation ----------------------------------------------
$token = (Invoke-RestMethod -Method Post -Uri "$Api/dev/token" -ContentType "application/json" -Body "{}").access_token
$auth = @{ Authorization = "Bearer $token" }
$invitation = Invoke-RestMethod -Method Post -Uri "$Api/invitations" -Headers $auth -ContentType "application/json" `
    -Body (@{ companyCode = $companyCode; companyName = "Smoke test $suffix"; email = $email; channels = @("email") } | ConvertTo-Json)
Check "invitation created" ($invitation.deliveredChannels -contains "email")
$linkPath = ([Uri]$invitation.link).AbsolutePath
$linkToken = $linkPath.Substring(3)

# --- 2. Portal: open the link, request the code, read it from Mailpit, verify --
function Get-FormToken([string]$html) {
    $m = [regex]::Match($html, 'name="__RequestVerificationToken" type="hidden" value="([^"]+)"')
    if (-not $m.Success) { throw "No antiforgery token on the page" }
    $m.Groups[1].Value
}
$web = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$page = Invoke-WebRequest -UseBasicParsing -WebSession $web -Uri "$Portal$linkPath"
$page = Invoke-WebRequest -UseBasicParsing -WebSession $web -Method Post -Uri "$Portal$linkPath" `
    -Body @{ CompanyCode = $companyCode; Email = $email; __RequestVerificationToken = (Get-FormToken $page.Content) }
$verifyUri = $page.BaseResponse.ResponseUri.AbsolutePath
Check "code requested" ($verifyUri -like "/verify/*") $verifyUri

$code = $null
for ($i = 0; $i -lt 30 -and -not $code; $i++) {
    $messages = (Invoke-RestMethod -Uri "$Mailpit/api/v1/messages?limit=50").messages |
        Where-Object { ($_.To | ForEach-Object { $_.Address }) -contains $email -and $_.Subject -like "*Verification code*" }
    foreach ($m in $messages) {
        $text = (Invoke-RestMethod -Uri "$Mailpit/api/v1/message/$($m.ID)").Text
        $match = [regex]::Match($text, '\b\d{6}\b')
        if ($match.Success) { $code = $match.Value; break }
    }
    if (-not $code) { Start-Sleep 1 }
}
Check "OTP email received" ([bool]$code)
if (-not $code) { exit 1 }

$page = Invoke-WebRequest -UseBasicParsing -WebSession $web -Method Post -Uri "$Portal$verifyUri" `
    -Body @{ token = $linkToken; code = $code; __RequestVerificationToken = (Get-FormToken $page.Content) }
Check "verified, upload page" ($page.BaseResponse.ResponseUri.AbsolutePath -eq "/upload")
$csrf = [regex]::Match($page.Content, 'name="csrf-token" content="([^"]+)"').Groups[1].Value

# --- 3. Chunked upload ---------------------------------------------------------
$headers = @{ "X-CSRF-TOKEN" = $csrf }
$bytes = [IO.File]::ReadAllBytes($zip)
$session = Invoke-RestMethod -WebSession $web -Method Post -Uri "$Portal/portal-api/uploads" -Headers $headers -ContentType "application/json" `
    -Body (@{ type = "orisDatabase"; fileName = [IO.Path]::GetFileName($zip); sizeBytes = $bytes.Length } | ConvertTo-Json)
$partsOk = $true
for ($n = 1; $n -le $session.partCount; $n++) {
    $offset = ($n - 1) * $session.chunkSizeBytes
    $length = [Math]::Min($session.chunkSizeBytes, $bytes.Length - $offset)
    $chunk = New-Object byte[] $length
    [Array]::Copy($bytes, $offset, $chunk, 0, $length)
    $status = Get-Status { Invoke-WebRequest -UseBasicParsing -WebSession $web -Method Put -Headers $headers -ContentType "application/octet-stream" `
        -Uri "$Portal/portal-api/uploads/$($session.uploadId)/parts/$n" -Body $chunk }
    if ($status -ne 204) { $partsOk = $false }
}
Check "parts uploaded" $partsOk "($($session.partCount) x $([Math]::Round($session.chunkSizeBytes / 1MB, 1)) MB)"
$sha = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLower()
$job = Invoke-RestMethod -WebSession $web -Method Post -Uri "$Portal/portal-api/uploads/$($session.uploadId)/complete" -Headers $headers `
    -ContentType "application/json" -Body (@{ sha256 = $sha } | ConvertTo-Json)
Check "upload completed, job queued" ([bool]$job.jobId)

# --- 4. Worker -----------------------------------------------------------------
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
do {
    Start-Sleep 2
    $state = Invoke-RestMethod -Uri "$Api/invitations/$($invitation.invitationId)" -Headers $auth
} while ($state.processingStatus -notin @("succeeded", "failed") -and [DateTime]::UtcNow -lt $deadline)
$seconds = [Math]::Round(([DateTime]::UtcNow - $started).TotalSeconds)
Check "job processed" ($state.processingStatus -eq "succeeded") "(status $($state.status)/$($state.processingStatus), $($state.processingErrorCode), ${seconds}s since start)"

# --- 5. Reports ----------------------------------------------------------------
$tenantHeaders = $auth + @{ "X-Tenant-Id" = $invitation.tenantId }
foreach ($report in "journal-entries", "turnover-register", "balance-sheet") {
    $status = Get-Status { Invoke-WebRequest -UseBasicParsing -Headers $tenantHeaders -Uri "$Api/reports/${report}?fromDate=$FromDate&toDate=$ToDate" }
    Check "report $report" ($status -eq 200) "($status)"
}
$status = Get-Status { Invoke-WebRequest -UseBasicParsing -Headers ($auth + @{ "X-Tenant-Id" = [Guid]::NewGuid().ToString() }) -Uri "$Api/reports/journal-entries?fromDate=$FromDate&toDate=$ToDate" }
Check "unknown tenant is 404" ($status -eq 404) "($status)"

# --- 6. Seq: the link token must never be logged -------------------------------
if ($Seq) {
    Start-Sleep 5   # let the exporters flush
    $since = $started.ToString("o")
    $events = Invoke-WebRequest -UseBasicParsing -Uri "$Seq/api/events?count=5000&fromDateUtc=$since"
    Check "Seq received events" ($events.Content.Length -gt 2)
    Check "link token not in Seq" (-not $events.Content.Contains($linkToken))
}

Write-Host ""
if ($failures.Count) { Write-Host "SMOKE TEST FAILED: $($failures -join ', ')" -ForegroundColor Red; exit 1 }
Write-Host "Smoke test passed." -ForegroundColor Green
exit 0
