[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("ValidateCredentials", "Sign", "Verify")]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^(?!0{64}$)[0-9a-f]{64}$")]
    [string]$ExpectedCertificateSha256,

    [string]$HandoffPath,

    [string]$SignaturePath,

    # These inputs exist only so the focused regression test can exercise the
    # complete RFC 3161 path without depending on an Internet TSA. Release
    # orchestration and hosted verification never set them.
    [Parameter(DontShow)]
    [string]$TestTimestampResponsePath,

    [Parameter(DontShow)]
    [string[]]$TestTrustedRootPath = @(),

    # Synthetic test certificates intentionally have no live CA revocation
    # service. This opt-in is accepted only with the focused test's custom
    # trust root; production callers always perform an online revocation check.
    [Parameter(DontShow)]
    [switch]$TestAssumeRevocationGood
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.Security.Cryptography.Pkcs

$reviewedCertificateSha256 = $ExpectedCertificateSha256
$sha256Oid = "2.16.840.1.101.3.4.2.1"
$codeSigningEkuOid = "1.3.6.1.5.5.7.3.3"
$timestampingEkuOid = "1.3.6.1.5.5.7.3.8"
$rfc3161TimestampAttributeOid = "1.2.840.113549.1.9.16.2.14"
$rfc3161ContentTypeOid = "1.2.840.113549.1.9.16.1.4"
$cmsDataContentTypeOid = "1.2.840.113549.1.7.1"

$usingTestInputs =
    -not [string]::IsNullOrWhiteSpace($TestTimestampResponsePath) -or
    @($TestTrustedRootPath).Count -gt 0 -or
    [bool]$TestAssumeRevocationGood
if ($usingTestInputs -and
    [Environment]::GetEnvironmentVariable(
        "TOPIAFORGE_HANDOFF_SIGNATURE_TEST_MODE"
    ) -cne "1") {
    throw "Handoff signature test inputs are disabled outside the focused test."
}
if ($TestAssumeRevocationGood -and @($TestTrustedRootPath).Count -eq 0) {
    throw "The test revocation override requires an explicit test trust root."
}

function Get-CertificateSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $Certificate
    )

    return $Certificate.GetCertHashString(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256
    ).ToLowerInvariant()
}

function Test-EndEntityCertificate {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $Certificate,

        [Parameter(Mandatory = $true)]
        [string]$RequiredEkuOid,

        [Parameter(Mandatory = $true)]
        [string]$Role,

        [switch]$RequirePrivateKey,

        [switch]$RequireCurrentValidity,

        [switch]$RequireExclusiveCriticalEku
    )

    if ($RequirePrivateKey -and -not $Certificate.HasPrivateKey) {
        throw "The $Role certificate has no private key."
    }
    if ($RequireCurrentValidity) {
        $now = [DateTime]::UtcNow
        if ($Certificate.NotBefore.ToUniversalTime() -gt $now -or
            $Certificate.NotAfter.ToUniversalTime() -le $now) {
            throw "The $Role certificate is not currently valid."
        }
    }

    $basicConstraints = @(
        $Certificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.19" }
    )
    $basicConstraintsExtension = if ($basicConstraints.Count -eq 1) {
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]$basicConstraints[0]
    }
    else {
        $null
    }
    if ($basicConstraints.Count -gt 1 -or
        ($null -ne $basicConstraintsExtension -and
            $basicConstraintsExtension.CertificateAuthority)) {
        throw "The $Role certificate must be an end-entity certificate."
    }

    $keyUsage = @(
        $Certificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.15" }
    )
    if ($keyUsage.Count -gt 1) {
        throw "The $Role certificate has duplicate key-usage extensions."
    }
    $keyUsageExtension = if ($keyUsage.Count -eq 1) {
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]$keyUsage[0]
    }
    else {
        $null
    }
    if ($null -ne $keyUsageExtension -and
        -not ($keyUsageExtension.KeyUsages -band
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature)) {
        throw "The $Role certificate is not valid for digital signatures."
    }

    $ekuExtensions = @(
        $Certificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.37" }
    )
    if ($ekuExtensions.Count -ne 1) {
        throw "The $Role certificate must have exactly one EKU extension."
    }
    $ekuExtension =
        [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$ekuExtensions[0]
    $ekuValues = @($ekuExtension.EnhancedKeyUsages | ForEach-Object { $_.Value })
    if ($RequiredEkuOid -notin $ekuValues) {
        throw "The $Role certificate does not have its required EKU."
    }
    if ($RequireExclusiveCriticalEku -and
        (-not $ekuExtension.Critical -or
            $ekuValues.Count -ne 1 -or
            $ekuValues[0] -cne $RequiredEkuOid)) {
        throw "The RFC 3161 TSA certificate must have only a critical timestamping EKU."
    }
}

function Get-TestTrustedRoots {
    $roots =
        [System.Security.Cryptography.X509Certificates.X509Certificate2Collection]::new()
    try {
        foreach ($path in @($TestTrustedRootPath)) {
            $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
            if ($item -isnot [System.IO.FileInfo] -or
                ($item.Attributes -band
                    [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
                $item.Length -le 0 -or
                $item.Length -gt 1048576) {
                throw "A handoff signature test root is not a bounded regular file."
            }
            [void]$roots.Add(
                [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                    $item.FullName
                )
            )
        }
        return ,$roots
    }
    catch {
        foreach ($root in $roots) {
            $root.Dispose()
        }
        throw
    }
}

function Assert-CertificateChain {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $Certificate,

        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2Collection]
        $ExtraCertificates,

        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2Collection]
        $TrustedRoots,

        [Parameter(Mandatory = $true)]
        [string]$ApplicationPolicyOid,

        [Parameter(Mandatory = $true)]
        [DateTimeOffset]$VerificationTime,

        [Parameter(Mandatory = $true)]
        [string]$Role
    )

    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        # Issuer certificates must be embedded in the PFX/CMS. Disabling AIA
        # issuer downloads does not disable CRL/OCSP retrieval.
        $chain.ChainPolicy.DisableCertificateDownloads = $true
        $chain.ChainPolicy.RevocationMode = if ($TestAssumeRevocationGood) {
            [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        }
        else {
            [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        }
        $chain.ChainPolicy.RevocationFlag =
            [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::EntireChain
        $chain.ChainPolicy.VerificationFlags =
            [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
        $chain.ChainPolicy.VerificationTime = $VerificationTime.UtcDateTime
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(5)
        [void]$chain.ChainPolicy.ApplicationPolicy.Add(
            [System.Security.Cryptography.Oid]::new($ApplicationPolicyOid)
        )
        foreach ($extra in $ExtraCertificates) {
            if ((Get-CertificateSha256 $extra) -cne
                (Get-CertificateSha256 $Certificate)) {
                [void]$chain.ChainPolicy.ExtraStore.Add($extra)
            }
        }
        if ($TrustedRoots.Count -gt 0) {
            $chain.ChainPolicy.TrustMode =
                [System.Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
            foreach ($root in $TrustedRoots) {
                [void]$chain.ChainPolicy.CustomTrustStore.Add($root)
            }
        }
        $built = $chain.Build($Certificate)
        $revocationFailureFlags =
            [System.Security.Cryptography.X509Certificates.X509ChainStatusFlags]::Revoked -bor
            [System.Security.Cryptography.X509Certificates.X509ChainStatusFlags]::RevocationStatusUnknown -bor
            [System.Security.Cryptography.X509Certificates.X509ChainStatusFlags]::OfflineRevocation
        $revocationFailures = @(
            foreach ($element in $chain.ChainElements) {
                foreach ($status in $element.ChainElementStatus) {
                    if (($status.Status -band $revocationFailureFlags) -ne 0) {
                        "$($element.Certificate.Subject):" +
                            "$($status.Status):" +
                            $status.StatusInformation.Trim()
                    }
                }
            }
        )
        if ($revocationFailures.Count -gt 0) {
            throw "The $Role certificate chain revocation status could not be " +
                "proven good at the required verification time: " +
                "$($revocationFailures -join '; ')."
        }
        if (-not $built) {
            $statuses = @(
                $chain.ChainStatus |
                    ForEach-Object {
                        "$($_.Status):$($_.StatusInformation.Trim())"
                    }
            )
            throw "The $Role certificate chain is not trusted at the required " +
                "verification time: $($statuses -join '; ')."
        }
    }
    finally {
        $chain.Dispose()
    }
}

function Test-CodeSigningCertificate {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $Certificate,

        [switch]$RequirePrivateKey,

        [switch]$RequireCurrentValidity
    )

    if ((Get-CertificateSha256 $Certificate) -cne
        $reviewedCertificateSha256) {
        throw "The Windows signing certificate does not match the reviewed SHA-256 pin."
    }
    Test-EndEntityCertificate -Certificate $Certificate `
        -RequiredEkuOid $codeSigningEkuOid -Role "Windows code-signing" `
        -RequirePrivateKey:$RequirePrivateKey `
        -RequireCurrentValidity:$RequireCurrentValidity
}

function Get-ConfiguredSigningCertificate {
    $encoded = [Environment]::GetEnvironmentVariable(
        "WINDOWS_CERTIFICATE_PFX"
    )
    $password = [Environment]::GetEnvironmentVariable(
        "WINDOWS_CERTIFICATE_PASSWORD"
    )
    $timestampUrl = [Environment]::GetEnvironmentVariable(
        "WINDOWS_TIMESTAMP_URL"
    )
    if ([string]::IsNullOrWhiteSpace($encoded) -or
        [string]::IsNullOrEmpty($password) -or
        [string]::IsNullOrWhiteSpace($timestampUrl)) {
        throw "WINDOWS_CERTIFICATE_PFX, WINDOWS_CERTIFICATE_PASSWORD, and WINDOWS_TIMESTAMP_URL are mandatory."
    }
    $timestamp = $null
    if (-not [Uri]::TryCreate(
            $timestampUrl,
            [UriKind]::Absolute,
            [ref]$timestamp
        ) -or
        $timestamp.Scheme -cne "https" -or
        [string]::IsNullOrWhiteSpace($timestamp.Host) -or
        -not [string]::IsNullOrEmpty($timestamp.UserInfo) -or
        -not [string]::IsNullOrEmpty($timestamp.Fragment)) {
        throw "WINDOWS_TIMESTAMP_URL must be a credential-free absolute HTTPS RFC 3161 endpoint."
    }

    $pfxBytes = $null
    $certificates =
        [System.Security.Cryptography.X509Certificates.X509Certificate2Collection]::new()
    try {
        try {
            $pfxBytes = [Convert]::FromBase64String(
                ($encoded -replace "\s", "")
            )
        }
        catch {
            throw "WINDOWS_CERTIFICATE_PFX must be valid base64."
        }
        $certificates.Import(
            $pfxBytes,
            $password,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
        )
        $matching = @(
            $certificates |
                Where-Object {
                    (Get-CertificateSha256 $_) -ceq
                        $reviewedCertificateSha256
                }
        )
        if ($matching.Count -ne 1) {
            throw "The PFX must contain exactly one certificate matching the reviewed SHA-256 pin."
        }
        $selected = $matching[0]
        Test-CodeSigningCertificate -Certificate $selected `
            -RequirePrivateKey -RequireCurrentValidity

        $trustedRoots = Get-TestTrustedRoots
        try {
            Assert-CertificateChain -Certificate $selected `
                -ExtraCertificates $certificates -TrustedRoots $trustedRoots `
                -ApplicationPolicyOid $codeSigningEkuOid `
                -VerificationTime ([DateTimeOffset]::UtcNow) `
                -Role "Windows code-signing"
        }
        finally {
            foreach ($root in $trustedRoots) {
                $root.Dispose()
            }
        }
        return [pscustomobject]@{
            Certificate = $selected
            Certificates = $certificates
            TimestampUri = $timestamp
        }
    }
    catch {
        foreach ($certificate in $certificates) {
            $certificate.Dispose()
        }
        throw
    }
    finally {
        if ($null -ne $pfxBytes) {
            [Array]::Clear($pfxBytes, 0, $pfxBytes.Length)
        }
    }
}

function Close-SigningConfiguration {
    param([Parameter(Mandatory = $true)]$Configuration)

    foreach ($certificate in $Configuration.Certificates) {
        $certificate.Dispose()
    }
}

function Get-RequiredRegularFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedName,
        [Parameter(Mandatory = $true)][Int64]$MaximumBytes
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or
        [System.IO.Path]::GetFileName($Path) -cne $ExpectedName) {
        throw "The required file must be named $ExpectedName."
    }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item -isnot [System.IO.FileInfo] -or
        ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne
            0 -or
        $item.Length -le 0 -or
        $item.Length -gt $MaximumBytes) {
        throw "$ExpectedName must be a bounded regular file."
    }
    return $item
}

function New-HandoffSignedCms {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "This helper constructs an in-memory CMS object and does not mutate external state."
    )]
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo]$Handoff,
        [Parameter(Mandatory = $true)]$Configuration
    )

    $contentInfo =
        [System.Security.Cryptography.Pkcs.ContentInfo]::new(
            [System.IO.File]::ReadAllBytes($Handoff.FullName)
        )
    $signedCms =
        [System.Security.Cryptography.Pkcs.SignedCms]::new(
            $contentInfo,
            $true
        )
    $signer =
        [System.Security.Cryptography.Pkcs.CmsSigner]::new(
            [System.Security.Cryptography.Pkcs.SubjectIdentifierType]::IssuerAndSerialNumber,
            $Configuration.Certificate
        )
    $signer.DigestAlgorithm =
        [System.Security.Cryptography.Oid]::new($sha256Oid)
    $signer.IncludeOption =
        [System.Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    foreach ($certificate in $Configuration.Certificates) {
        if ((Get-CertificateSha256 $certificate) -cne
                (Get-CertificateSha256 $Configuration.Certificate) -and
            $certificate.Subject -cne $certificate.Issuer) {
            [void]$signer.Certificates.Add($certificate)
        }
    }
    $signedCms.ComputeSignature($signer, $true)
    return $signedCms
}

function Get-Rfc3161Response {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.Pkcs.Rfc3161TimestampRequest]
        $Request,

        [Parameter(Mandatory = $true)][Uri]$TimestampUri
    )

    if (-not [string]::IsNullOrWhiteSpace($TestTimestampResponsePath)) {
        $fixture = Get-RequiredRegularFile `
            -Path $TestTimestampResponsePath `
            -ExpectedName "timestamp-response.tsr" `
            -MaximumBytes 1048576
        return ,[System.IO.File]::ReadAllBytes($fixture.FullName)
    }

    $requestBytes = $Request.Encode()
    $client = [System.Net.Http.HttpClient]::new()
    $content = [System.Net.Http.ByteArrayContent]::new($requestBytes)
    $response = $null
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(30)
        $content.Headers.ContentType =
            [System.Net.Http.Headers.MediaTypeHeaderValue]::new(
                "application/timestamp-query"
            )
        [void]$client.DefaultRequestHeaders.Accept.Add(
            [System.Net.Http.Headers.MediaTypeWithQualityHeaderValue]::new(
                "application/timestamp-reply"
            )
        )
        $response = $client.PostAsync(
            $TimestampUri,
            $content
        ).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "The RFC 3161 timestamp authority returned HTTP " +
                "$([int]$response.StatusCode)."
        }
        $mediaType = [string]$response.Content.Headers.ContentType.MediaType
        if ($mediaType -cne "application/timestamp-reply") {
            throw "The RFC 3161 timestamp authority returned an unexpected content type."
        }
        $responseBytes = $response.Content.ReadAsByteArrayAsync(
        ).GetAwaiter().GetResult()
        if ($responseBytes.Length -le 0 -or
            $responseBytes.Length -gt 1048576) {
            throw "The RFC 3161 timestamp response is empty or too large."
        }
        return ,$responseBytes
    }
    finally {
        if ($null -ne $response) {
            $response.Dispose()
        }
        $content.Dispose()
        $client.Dispose()
        [Array]::Clear($requestBytes, 0, $requestBytes.Length)
    }
}

function Add-Rfc3161Timestamp {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.Pkcs.SignedCms]$SignedCms,

        [Parameter(Mandatory = $true)][Uri]$TimestampUri
    )

    if ($SignedCms.SignerInfos.Count -ne 1) {
        throw "The release handoff CMS must have exactly one signer before timestamping."
    }
    $request =
        [System.Security.Cryptography.Pkcs.Rfc3161TimestampRequest]::CreateFromSignerInfo(
            $SignedCms.SignerInfos[0],
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            $null,
            $null,
            $true,
            $null
        )
    $responseBytes = Get-Rfc3161Response -Request $request `
        -TimestampUri $TimestampUri
    try {
        $consumed = 0
        try {
            $token = $request.ProcessResponse(
                [System.ReadOnlyMemory[byte]]::new($responseBytes),
                [ref]$consumed
            )
        }
        catch {
            throw "The RFC 3161 timestamp response does not match the handoff signer."
        }
        if ($consumed -ne $responseBytes.Length) {
            throw "The RFC 3161 timestamp response has trailing data."
        }
        $timestampValue =
            [System.Security.Cryptography.AsnEncodedData]::new(
                [System.Security.Cryptography.Oid]::new(
                    $rfc3161TimestampAttributeOid
                ),
                $token.AsSignedCms().Encode()
            )
        $SignedCms.SignerInfos[0].AddUnsignedAttribute($timestampValue)
    }
    finally {
        [Array]::Clear($responseBytes, 0, $responseBytes.Length)
    }
}

function Get-OnlyRfc3161TimestampToken {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.Pkcs.SignerInfo]$Signer
    )

    if ($Signer.CounterSignerInfos.Count -ne 0) {
        throw "The release handoff CMS must not contain a legacy countersignature."
    }
    $timestampValues = @(
        foreach ($attribute in $Signer.UnsignedAttributes) {
            if ($attribute.Oid.Value -ceq $rfc3161TimestampAttributeOid) {
                foreach ($value in $attribute.Values) {
                    $value
                }
            }
        }
    )
    if ($timestampValues.Count -ne 1) {
        throw "The release handoff CMS must contain exactly one RFC 3161 timestamp."
    }
    [System.Security.Cryptography.Pkcs.Rfc3161TimestampToken]$token = $null
    $consumed = 0
    $rawData = [byte[]]$timestampValues[0].RawData
    if (-not [System.Security.Cryptography.Pkcs.Rfc3161TimestampToken]::TryDecode(
            [System.ReadOnlyMemory[byte]]::new($rawData),
            [ref]$token,
            [ref]$consumed
        ) -or
        $null -eq $token -or
        $consumed -ne $rawData.Length) {
        throw "The release handoff RFC 3161 timestamp token is malformed."
    }
    return $token
}

function Test-HandoffSignature {
    $handoff = Get-RequiredRegularFile -Path $HandoffPath `
        -ExpectedName "release-handoff-v1.json" -MaximumBytes 10485760
    $signature = Get-RequiredRegularFile -Path $SignaturePath `
        -ExpectedName "release-handoff-v1.json.p7s" -MaximumBytes 1048576
    $contentInfo =
        [System.Security.Cryptography.Pkcs.ContentInfo]::new(
            [System.IO.File]::ReadAllBytes($handoff.FullName)
        )
    $signedCms =
        [System.Security.Cryptography.Pkcs.SignedCms]::new(
            $contentInfo,
            $true
        )
    try {
        $signedCms.Decode(
            [System.IO.File]::ReadAllBytes($signature.FullName)
        )
        $signedCms.CheckSignature($true)
    }
    catch {
        throw "The detached release handoff CMS signature is invalid."
    }
    if (-not $signedCms.Detached -or
        $signedCms.ContentInfo.ContentType.Value -cne $cmsDataContentTypeOid) {
        throw "The release handoff CMS signature must be detached CMS data."
    }
    if ($signedCms.SignerInfos.Count -ne 1) {
        throw "The release handoff CMS signature must contain exactly one signer."
    }
    $signer = $signedCms.SignerInfos[0]
    if ($signer.DigestAlgorithm.Value -cne $sha256Oid -or
        $null -eq $signer.Certificate) {
        throw "The release handoff CMS signer must use SHA-256 and embed its certificate."
    }
    Test-CodeSigningCertificate -Certificate $signer.Certificate

    $timestampToken = Get-OnlyRfc3161TimestampToken -Signer $signer
    $timestampCms = $timestampToken.AsSignedCms()
    if ($timestampCms.Detached -or
        $timestampCms.ContentInfo.ContentType.Value -cne
            $rfc3161ContentTypeOid -or
        $timestampCms.SignerInfos.Count -ne 1 -or
        $timestampCms.SignerInfos[0].DigestAlgorithm.Value -cne $sha256Oid -or
        $timestampToken.TokenInfo.HashAlgorithmId.Value -cne $sha256Oid) {
        throw "The release handoff RFC 3161 timestamp token is not a single SHA-256 token."
    }
    try {
        $timestampCms.CheckSignature($true)
    }
    catch {
        throw "The release handoff RFC 3161 timestamp signature is invalid."
    }
    [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $timestampCertificate = $null
    if (-not $timestampToken.VerifySignatureForSignerInfo(
            $signer,
            [ref]$timestampCertificate,
            $timestampCms.Certificates
        ) -or
        $null -eq $timestampCertificate) {
        throw "The RFC 3161 timestamp imprint does not match the handoff CMS signer."
    }
    if ($null -eq $timestampCms.SignerInfos[0].Certificate -or
        (Get-CertificateSha256 $timestampCms.SignerInfos[0].Certificate) -cne
            (Get-CertificateSha256 $timestampCertificate)) {
        throw "The RFC 3161 timestamp signer certificate is ambiguous."
    }
    Test-EndEntityCertificate -Certificate $timestampCertificate `
        -RequiredEkuOid $timestampingEkuOid -Role "RFC 3161 TSA" `
        -RequireExclusiveCriticalEku

    $timestamp = $timestampToken.TokenInfo.Timestamp
    if ($timestamp -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        throw "The RFC 3161 timestamp is unreasonably far in the future."
    }
    $trustedRoots = Get-TestTrustedRoots
    try {
        Assert-CertificateChain -Certificate $timestampCertificate `
            -ExtraCertificates $timestampCms.Certificates `
            -TrustedRoots $trustedRoots `
            -ApplicationPolicyOid $timestampingEkuOid `
            -VerificationTime $timestamp -Role "RFC 3161 TSA"
        Assert-CertificateChain -Certificate $signer.Certificate `
            -ExtraCertificates $signedCms.Certificates `
            -TrustedRoots $trustedRoots `
            -ApplicationPolicyOid $codeSigningEkuOid `
            -VerificationTime $timestamp -Role "Windows code-signing"
    }
    finally {
        foreach ($root in $trustedRoots) {
            $root.Dispose()
        }
        $timestampCertificate.Dispose()
    }
}

if ($Mode -eq "ValidateCredentials") {
    $configuration = Get-ConfiguredSigningCertificate
    try {
        Write-Output "Windows signing credentials match the reviewed certificate pin and trusted chain."
    }
    finally {
        Close-SigningConfiguration $configuration
    }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($HandoffPath) -or
    [string]::IsNullOrWhiteSpace($SignaturePath)) {
    throw "-HandoffPath and -SignaturePath are required for $Mode."
}

if ($Mode -eq "Verify") {
    Test-HandoffSignature
    Write-Output "Detached release handoff CMS signature and RFC 3161 timestamp are valid."
    exit 0
}

$handoff = Get-RequiredRegularFile -Path $HandoffPath `
    -ExpectedName "release-handoff-v1.json" -MaximumBytes 10485760
if ([System.IO.Path]::GetFileName($SignaturePath) -cne
    "release-handoff-v1.json.p7s") {
    throw "The signature output must be named release-handoff-v1.json.p7s."
}

# Sign mode always proves possession of the configured private key, including
# immutable reruns. Credential-free resumptions use Verify mode.
$configuration = Get-ConfiguredSigningCertificate
if (Test-Path -LiteralPath $SignaturePath) {
    try {
        Test-HandoffSignature
        Write-Output "Exact detached release handoff CMS signature already exists."
    }
    finally {
        Close-SigningConfiguration $configuration
    }
    exit 0
}
$signatureDirectory = Split-Path -Parent (
    [System.IO.Path]::GetFullPath($SignaturePath)
)
if (-not (Test-Path -LiteralPath $signatureDirectory -PathType Container)) {
    Close-SigningConfiguration $configuration
    throw "The signature output directory must already exist."
}

$temporary = Join-Path $signatureDirectory (
    ".release-handoff-v1.json.p7s.$PID.tmp"
)
try {
    $signedCms = New-HandoffSignedCms -Handoff $handoff `
        -Configuration $configuration
    Add-Rfc3161Timestamp -SignedCms $signedCms `
        -TimestampUri $configuration.TimestampUri
    [System.IO.File]::WriteAllBytes($temporary, $signedCms.Encode())
    Move-Item -LiteralPath $temporary -Destination $SignaturePath
}
finally {
    Close-SigningConfiguration $configuration
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Force
    }
}
Test-HandoffSignature
Write-Output "Created and verified the detached release handoff CMS signature with one RFC 3161 timestamp."
