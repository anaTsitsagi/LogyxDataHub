<#
.SYNOPSIS
    Removes DataHub from the local Rancher Desktop cluster. By default the infrastructure's data volumes
    (database, queue, S3 objects, Seq) are kept, so k8s-up.ps1 brings everything back as it was.
    -Purge deletes the whole namespace, including the volumes. The generated secrets in .local\k8s stay.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\local\k8s-down.ps1          # stop, keep data
    powershell -ExecutionPolicy Bypass -File scripts\local\k8s-down.ps1 -Purge   # delete everything
#>
param(
    [switch]$Purge,
    [string]$Namespace = "datahub-local",
    [string]$Context = "rancher-desktop"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")

$current = (& kubectl config current-context).Trim()
if ($current -ne $Context) { throw "kubectl context is '$current', expected '$Context'." }

& helm uninstall datahub -n $Namespace --ignore-not-found --wait
if ($Purge) {
    & kubectl delete namespace $Namespace --ignore-not-found --wait=true
    Write-Host "Namespace $Namespace and its volumes are deleted."
}
else {
    # StatefulSet volumes (PVCs) are not deleted with their StatefulSets.
    & kubectl delete -k (Join-Path $root "deploy\local") --ignore-not-found
    Write-Host "DataHub and the infrastructure are stopped; data volumes kept (-Purge deletes them)."
}
