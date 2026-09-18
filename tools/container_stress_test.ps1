param(
    [Parameter(Mandatory = $true)]
    [string]$Image,

    [string]$Context = "",
    [string]$Prefix = "stress",
    [int]$Max = 1000,
    [int]$BatchSize = 10,
    [ValidateSet("create", "run")]
    [string]$Mode = "create",
    [double]$Cpus = 0.5,
    [int]$MemoryMb = 128,
    [switch]$Privileged,
    [switch]$HostNetwork,
    [string]$Volume = "",
    [int]$PauseMs = 200,
    [switch]$Cleanup,
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $ReportPath = Join-Path (Get-Location) ("container_stress_{0}.csv" -f $stamp)
}

function Invoke-Docker {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Args
    )

    $fullArgs = @()
    if (-not [string]::IsNullOrWhiteSpace($Context)) {
        $fullArgs += @("--context", $Context)
    }
    $fullArgs += $Args

    $output = & docker @fullArgs 2>&1
    $code = $LASTEXITCODE
    return [pscustomobject]@{
        ExitCode = $code
        Output = ($output -join "`n")
    }
}

function Get-DockerValue {
    param([string[]]$Args)

    $result = Invoke-Docker -Args $Args
    if ($result.ExitCode -ne 0) {
        return ""
    }
    if ($null -eq $result.Output) {
        return ""
    }
    return $result.Output.Trim()
}

function Write-ReportRow {
    param(
        [int]$Attempt,
        [string]$Name,
        [string]$Status,
        [int]$TotalStressContainers,
        [string]$Message
    )

    $row = [pscustomobject]@{
        Time = (Get-Date).ToString("s")
        Attempt = $Attempt
        Name = $Name
        Mode = $Mode
        Status = $Status
        TotalStressContainers = $TotalStressContainers
        RunningContainers = Get-DockerValue -Args @("ps", "-q")
        AllContainers = Get-DockerValue -Args @("ps", "-aq")
        Images = Get-DockerValue -Args @("images", "-q")
        DockerDisk = Get-DockerValue -Args @("system", "df", "--format", "table {{.Type}}\t{{.TotalCount}}\t{{.Size}}\t{{.Reclaimable}}")
        Message = $Message
    }

    $exists = Test-Path -LiteralPath $ReportPath
    $row | Export-Csv -LiteralPath $ReportPath -Append:$exists -NoTypeInformation -Encoding UTF8
}

Write-Host "Container stress test started"
Write-Host ("Image: {0}" -f $Image)
Write-Host ("Mode : {0}" -f $Mode)
Write-Host ("Max  : {0}" -f $Max)
Write-Host ("CSV  : {0}" -f $ReportPath)

$baseArgs = @()
if ($Mode -eq "run") {
    $baseArgs += @("run", "-d")
} else {
    $baseArgs += @("create")
}

if ($Privileged) {
    $baseArgs += "--privileged"
}
if ($HostNetwork) {
    $baseArgs += "--net=host"
}
if (-not [string]::IsNullOrWhiteSpace($Volume)) {
    $baseArgs += @("-v", $Volume)
}
if ($Cpus -gt 0) {
    $baseArgs += @("--cpus", $Cpus.ToString("0.##", [Globalization.CultureInfo]::InvariantCulture))
}
if ($MemoryMb -gt 0) {
    $baseArgs += @("--memory", ("{0}m" -f $MemoryMb))
}

$created = New-Object System.Collections.Generic.List[string]
$failed = $false

for ($i = 1; $i -le $Max; $i++) {
    $name = "{0}-{1:D5}" -f $Prefix, $i
    $args = @($baseArgs)
    $args += @("--name", $name)
    $args += $Image

    $result = Invoke-Docker -Args $args
    if ($result.ExitCode -eq 0) {
        $created.Add($name)
        if (($i % $BatchSize) -eq 0 -or $i -eq 1) {
            Write-Host ("OK {0}/{1}, latest={2}" -f $i, $Max, $name)
            Write-ReportRow -Attempt $i -Name $name -Status "OK" -TotalStressContainers $created.Count -Message $result.Output
        }
    } else {
        $failed = $true
        Write-Host ("FAILED at {0}, name={1}" -f $i, $name)
        Write-Host $result.Output
        Write-ReportRow -Attempt $i -Name $name -Status "FAILED" -TotalStressContainers $created.Count -Message $result.Output
        break
    }

    if ($PauseMs -gt 0) {
        Start-Sleep -Milliseconds $PauseMs
    }
}

Write-ReportRow -Attempt $created.Count -Name "" -Status "SUMMARY" -TotalStressContainers $created.Count -Message ("created={0}; failed={1}" -f $created.Count, $failed)
Write-Host ("Done. Created containers: {0}" -f $created.Count)
Write-Host ("Report file: {0}" -f $ReportPath)

if ($Cleanup) {
    Write-Host "Cleaning up stress test containers..."
    for ($i = 0; $i -lt $created.Count; $i += 50) {
        $end = [Math]::Min($i + 49, $created.Count - 1)
        $chunk = @($created[$i..$end])
        if ($chunk.Count -gt 0) {
            [void](Invoke-Docker -Args (@("rm", "-f") + $chunk))
        }
    }
    Write-Host "Cleanup done."
}
