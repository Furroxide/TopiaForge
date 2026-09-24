# Local-only admission and serialization; never a release qualification gate.
Set-StrictMode -Version Latest

function Read-SandboxCiDocument {
    param([Parameter(Mandatory)][string]$Path,
        [ValidateRange(1, 4194304)][int]$MaximumBytes = 65536,
        [ValidateRange(1, 30000)][int]$MaximumArrayLength = 32)
    Assert-SandboxCiPhysicalPath -Path $Path -File
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $buffer = [byte[]]::new($MaximumBytes + 1)
        $count = 0
        while ($count -lt $buffer.Length) {
            $read = $stream.Read($buffer, $count, $buffer.Length - $count)
            if ($read -eq 0) { break }
            $count += $read
        }
        if ($count -eq 0 -or $count -gt $MaximumBytes) { throw 'Sandbox CI document exceeds its byte bound.' }
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($buffer, 0, $count)
        $options = [Text.Json.JsonDocumentOptions]::new()
        $options.MaxDepth = 12
        $document = [Text.Json.JsonDocument]::Parse($text, $options)
        try { Assert-SandboxCiJsonTree $document.RootElement $MaximumArrayLength }
        finally { $document.Dispose() }
        return ConvertFrom-Json -InputObject $text -AsHashtable -DateKind String
    }
    finally { $stream.Dispose() }
}

function Assert-SandboxCiJsonTree {
    param([Text.Json.JsonElement]$Element, [int]$MaximumArrayLength = 32)
    switch ($Element.ValueKind.ToString()) {
        'Object' {
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($property in $Element.EnumerateObject()) {
                if (!$names.Add($property.Name) -or $property.Name.Length -gt 80) {
                    throw 'Duplicate or oversized Sandbox CI property.'
                }
                Assert-SandboxCiJsonTree $property.Value $MaximumArrayLength
            }
        }
        'Array' {
            if ($Element.GetArrayLength() -gt $MaximumArrayLength) { throw 'Oversized Sandbox CI array.' }
            foreach ($item in $Element.EnumerateArray()) { Assert-SandboxCiJsonTree $item $MaximumArrayLength }
        }
        'String' {
            if ($Element.GetString().Length -gt 2048) { throw 'Oversized Sandbox CI text.' }
        }
    }
}

function Assert-SandboxCiFields {
    param([Collections.IDictionary]$Value, [string[]]$Fields)
    if ($null -eq $Value -or $Value.Count -ne $Fields.Count -or
        @($Value.Keys | Where-Object { $_ -cnotin $Fields }).Count -ne 0) {
        throw 'Sandbox CI document has missing or unknown properties.'
    }
}

function Assert-SandboxCiText {
    param($Value, [int]$Maximum = 1024)
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value) -or
        $Value.Length -gt $Maximum -or $Value -match '[\x00-\x1f\x7f]') {
        throw 'Sandbox CI text must be bounded and nonempty.'
    }
}

function Assert-SandboxCiPhysicalPath {
    param([string]$Path, [switch]$File)
    if (![IO.Path]::IsPathFullyQualified($Path)) { throw 'Sandbox CI requires absolute paths.' }
    if ($IsWindows -and ($Path -cnotmatch '^[A-Za-z]:[\\/]' -or
        $Path.Substring(2).Contains(':') -or $Path -match '[. ]([\\/]|$)')) {
        throw 'Sandbox CI refuses device, UNC, alternate-stream and trailing-dot/space aliases.'
    }
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.Equals($Path, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Sandbox CI requires canonical paths without relative components.'
    }
    $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if (($File -and $item -isnot [IO.FileInfo]) -or (!$File -and $item -isnot [IO.DirectoryInfo])) {
        throw 'Sandbox CI path has the wrong filesystem kind.'
    }
    $ancestor = $item
    while ($null -ne $ancestor) {
        if (($ancestor.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Device)) -ne 0) {
            throw 'Sandbox CI refuses linked or device paths.'
        }
        $ancestor = if ($ancestor -is [IO.FileInfo]) { $ancestor.Directory } else { $ancestor.Parent }
    }
}

function Assert-SandboxCiAdmission {
    param(
        [Collections.IDictionary]$Record,
        [ValidateSet('editor', 'game')][string]$Lane,
        [string]$SourceRevision,
        [string]$WorkspaceRoot,
        [string]$MachineName,
        [string]$UserSid,
        [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow,
        [string]$SourceWorkspaceSha256
    )
    $fields = @('schemaVersion', 'kind', 'lane', 'sourceRevision',
        'operatorId', 'reviewReference', 'reservationId', 'notBeforeUtc', 'expiresUtc',
        'machineName', 'userSid', 'workspaceRoot', 'privateRoot', 'devices',
        'recoveryReference', 'baselineReviewReference')
    if ($SourceWorkspaceSha256) {
        $fields += 'sourceWorkspaceSha256'
        if ($Lane -cne 'game' -or $SourceWorkspaceSha256 -cnotmatch '^[0-9a-f]{64}$' -or
            $Record.sourceWorkspaceSha256 -cne $SourceWorkspaceSha256) {
            throw 'Sandbox reviewed source snapshot digest does not match.'
        }
    }
    Assert-SandboxCiFields $Record $fields
    if ($Record.schemaVersion -isnot [long] -and $Record.schemaVersion -isnot [int]) {
        throw 'Sandbox CI schema version must be an integer.'
    }
    if ($Record.schemaVersion -ne 1 -or $Record.kind -cne 'sandbox-local-lane-admission-v1' -or
        $Record.lane -cne $Lane -or $Record.sourceRevision -cnotmatch '^[0-9a-f]{40}$' -or
        $Record.sourceRevision -cne $SourceRevision) {
        throw 'Sandbox CI admission lane, version or reviewed source does not match.'
    }
    foreach ($key in @('operatorId', 'reviewReference', 'recoveryReference', 'baselineReviewReference')) {
        Assert-SandboxCiText $Record[$key]
    }
    if ($Record.reservationId -isnot [string] -or $Record.reservationId -cnotmatch '^[a-z][a-z0-9-]{0,79}$') {
        throw 'Sandbox CI reservation ID is invalid.'
    }
    foreach ($key in @('machineName', 'userSid', 'workspaceRoot', 'privateRoot')) {
        Assert-SandboxCiText $Record[$key]
    }
    if ($Record.machineName -cne $MachineName -or $Record.userSid -cne $UserSid -or
        !$Record.workspaceRoot.Equals($WorkspaceRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Sandbox CI admission does not match measured host, account or workspace.'
    }
    foreach ($key in @('notBeforeUtc', 'expiresUtc')) {
        if ($Record[$key] -isnot [string] -or
            $Record[$key] -cnotmatch '^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$') {
            throw 'Sandbox CI reservation times must be UTC whole-second timestamps.'
        }
    }
    $start = [DateTimeOffset]::ParseExact($Record.notBeforeUtc, 'yyyy-MM-ddTHH:mm:ssZ',
        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
    $end = [DateTimeOffset]::ParseExact($Record.expiresUtc, 'yyyy-MM-ddTHH:mm:ssZ',
        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
    if ($end -le $start -or ($end - $start).TotalHours -gt 4 -or $Now -lt $start -or $Now -ge $end) {
        throw 'Sandbox CI device reservation is inactive or exceeds four hours.'
    }
    if ($Record.devices -isnot [array] -or $Record.devices.Count -lt 3 -or $Record.devices.Count -gt 16) {
        throw 'Sandbox CI admission requires a bounded device inventory.'
    }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($device in $Record.devices) {
        Assert-SandboxCiFields $device @('kind', 'id')
        if ($device.kind -cnotin @('display', 'keyboard', 'mouse', 'audio-output', 'microphone', 'gamepad') -or
            !$names.Add($device.kind)) { throw 'Sandbox CI device kind is unknown or repeated.' }
        Assert-SandboxCiText $device.id 256
    }
    foreach ($required in @('display', 'keyboard', 'mouse')) {
        if (!$names.Contains($required)) { throw 'Sandbox CI input/display reservation is incomplete.' }
    }
    if ($Lane -ceq 'game' -and !$names.Contains('audio-output')) {
        throw 'Sandbox game admission requires the approved audio-output device.'
    }
}

function Get-SandboxCiLocalAdmission {
    param([string]$AdmissionRecord, [ValidateSet('editor', 'game')][string]$Lane, [string]$WorkspaceRoot,
        [string]$SourceWorkspace)
    if (!$IsWindows -or $env:GITHUB_ACTIONS -ceq 'true') {
        throw 'Licensed Sandbox lanes run only on an admitted local Windows workstation, outside GitHub Actions.'
    }
    Assert-SandboxCiPhysicalPath $WorkspaceRoot
    $record = Read-SandboxCiDocument $AdmissionRecord
    $snapshotDigest = ''
    if ($SourceWorkspace) {
        if ($Lane -cne 'game') { throw 'Editor admission requires its reviewed Git checkout.' }
        $snapshot = Read-SandboxCiDocument $SourceWorkspace -MaximumBytes 4194304 -MaximumArrayLength 30000
        Assert-SandboxCiFields $snapshot @('schemaVersion', 'kind', 'sourceRevision', 'dirty', 'statusSha256', 'diffSha256', 'files')
        if ($snapshot.schemaVersion -isnot [long] -and $snapshot.schemaVersion -isnot [int]) { throw 'Source snapshot version must be an integer.' }
        if ($snapshot.schemaVersion -ne 1 -or $snapshot.kind -cne 'sandbox-source-workspace-v1') { throw 'Unsupported source snapshot.' }
        $snapshotDigest = (Get-FileHash -LiteralPath $SourceWorkspace -Algorithm SHA256).Hash.ToLowerInvariant()
        $source = $snapshot.sourceRevision
    }
    else {
        $source = (& git -C $WorkspaceRoot rev-parse --verify HEAD 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Sandbox CI requires an exact Git source revision.' }
        $dirty = & git -C $WorkspaceRoot status --porcelain --untracked-files=all
        if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Sandbox CI requires a clean, reviewed source checkout.' }
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $sid = $identity.User.Value } finally { $identity.Dispose() }
    Assert-SandboxCiAdmission $record $Lane $source $WorkspaceRoot ([Environment]::MachineName) $sid -SourceWorkspaceSha256 $snapshotDigest
    Assert-SandboxCiPhysicalPath $record.privateRoot
    $private = [IO.Path]::GetFullPath($record.privateRoot).TrimEnd('\', '/')
    $workspace = [IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\', '/')
    foreach ($pair in @(@($private, $workspace), @($workspace, $private))) {
        if ($pair[0].Equals($pair[1], [StringComparison]::OrdinalIgnoreCase) -or
            $pair[0].StartsWith($pair[1] + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Sandbox private evidence and source roots must be disjoint.'
        }
    }
    $recordPath = [IO.Path]::GetFullPath($AdmissionRecord)
    if (!$recordPath.StartsWith($private + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Sandbox lane admission must be retained inside its private root.'
    }
    return $record
}

function Invoke-SandboxCiReservedLane {
    param(
        [Collections.IDictionary]$Admission,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    # Global mutex prevents simultaneous local Editor/game lanes, across accounts.
    # An abandoned mutex or retained marker requires an operator recovery review.
    $mutex = [Threading.Mutex]::new($false, 'Global\TopiaForgeSandboxAcceptanceV1')
    $held = $false
    $marker = Join-Path $Admission.privateRoot 'sandbox-recovery-required.json'
    try {
        try { $held = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] {
            $held = $true
            if (!(Test-Path -LiteralPath $marker)) {
                $recovery = [IO.File]::Open($marker, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try {
                    $bytes = [Text.Encoding]::UTF8.GetBytes((@{schemaVersion=1;kind='sandbox-recovery-required-v1';reason='abandoned-mutex';reservationId=$Admission.reservationId} | ConvertTo-Json -Compress))
                    $recovery.Write($bytes, 0, $bytes.Length)
                    $recovery.Flush($true)
                }
                finally { $recovery.Dispose() }
            }
            throw 'Sandbox prior reservation was abandoned; operator recovery is required.'
        }
        if (!$held) { throw 'Another Sandbox lane owns the workstation/device reservation.' }
        if (Test-Path -LiteralPath $marker) { throw 'Sandbox recovery marker remains; operator recovery is required.' }
        $stream = [IO.File]::Open($marker, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes((@{
                schemaVersion = 1; kind = 'sandbox-recovery-required-v1'
                reservationId = $Admission.reservationId; sourceRevision = $Admission.sourceRevision
                startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
            } | ConvertTo-Json -Compress))
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally { $stream.Dispose() }
        $result = & $Action
        if ($result -isnot [Collections.IDictionary] -or
            $result.cleanupConfirmed -isnot [bool] -or !$result.cleanupConfirmed) {
            throw 'Sandbox runner did not confirm input release, cleanup and original owned-process exit; preserve the private layout.'
        }
        # A test failure with confirmed cleanup still retains its run evidence.
        # Remove only this exact marker, never private evidence or process state.
        Assert-SandboxCiPhysicalPath $marker -File
        [IO.File]::Delete($marker)
        return $result
    }
    finally {
        if ($held) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}
