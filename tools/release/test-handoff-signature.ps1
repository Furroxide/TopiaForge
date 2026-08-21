[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Formats.Asn1
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
Add-Type -TypeDefinition @'
using System;
using System.Formats.Asn1;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public static class TopiaForgeRfc3161TestEncoding
{
    public static byte[] BuildTstInfo(
        byte[] messageHash,
        DateTimeOffset timestamp,
        string sha256Oid)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.WriteInteger(1);
        writer.WriteObjectIdentifier("1.3.6.1.4.1.57264.1");
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteObjectIdentifier(sha256Oid);
        writer.WriteNull();
        writer.PopSequence();
        writer.WriteOctetString(messageHash);
        writer.PopSequence();
        writer.WriteInteger(1);
        writer.WriteGeneralizedTime(timestamp, omitFractionalSeconds: false);
        writer.PopSequence();
        return writer.Encode();
    }

    public static byte[] BuildGrantedResponse(byte[] timestampToken)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteInteger(0);
        writer.PopSequence();
        writer.WriteEncodedValue(timestampToken);
        writer.PopSequence();
        return writer.Encode();
    }

    public static byte[] BuildSigningCertificateV2(byte[] certificateHash)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteOctetString(certificateHash);
        writer.PopSequence();
        writer.PopSequence();
        writer.PopSequence();
        return writer.Encode();
    }
}

public sealed class TopiaForgeCrlTestServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _serverTask;
    private byte[] _content;

    public TopiaForgeCrlTestServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Uri = new Uri($"http://127.0.0.1:{endpoint.Port}/topiaforge-test.crl");
        _serverTask = Task.Run(ServeAsync);
    }

    public Uri Uri { get; }

    public void SetContent(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Interlocked.Exchange(ref _content, (byte[])content.Clone());
    }

    private async Task ServeAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(
                    _cancellation.Token
                ).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }

            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                var request = new byte[16384];
                var used = 0;
                while (used < request.Length)
                {
                    int read = await stream.ReadAsync(
                        request.AsMemory(used, request.Length - used),
                        _cancellation.Token
                    ).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    used += read;
                    if (used >= 4 &&
                        Encoding.ASCII.GetString(request, 0, used)
                            .Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                byte[] content = Volatile.Read(ref _content);
                if (content is null)
                {
                    byte[] unavailable = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 503 Service Unavailable\r\n" +
                        "Content-Length: 0\r\nConnection: close\r\n\r\n"
                    );
                    await stream.WriteAsync(
                        unavailable,
                        _cancellation.Token
                    ).ConfigureAwait(false);
                    continue;
                }

                byte[] header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/pkix-crl\r\n" +
                    $"Content-Length: {content.Length}\r\n" +
                    "Cache-Control: no-store\r\n" +
                    "Connection: close\r\n\r\n"
                );
                await stream.WriteAsync(
                    header,
                    _cancellation.Token
                ).ConfigureAwait(false);
                await stream.WriteAsync(
                    content,
                    _cancellation.Token
                ).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _listener.Stop();
        try
        {
            _serverTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        _cancellation.Dispose();
    }
}
'@

$sha256Oid = "2.16.840.1.101.3.4.2.1"
$codeSigningEkuOid = "1.3.6.1.5.5.7.3.3"
$timestampingEkuOid = "1.3.6.1.5.5.7.3.8"
$timestampAttributeOid = "1.2.840.113549.1.9.16.2.14"
$timestampContentTypeOid = "1.2.840.113549.1.9.16.1.4"

function Invoke-HandoffSignature {
    param(
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][string]$ExpectedPin,
        [string]$Handoff,
        [string]$Signature,
        [string]$TimestampResponse,
        [string[]]$TrustedRoots = @(),
        [switch]$AssumeRevocationGood
    )

    $arguments = @(
        "-NoLogo",
        "-NoProfile",
        "-NonInteractive",
        "-File",
        (Join-Path $PSScriptRoot "handoff-signature.ps1"),
        "-Mode",
        $Mode,
        "-ExpectedCertificateSha256",
        $ExpectedPin
    )
    if (-not [string]::IsNullOrWhiteSpace($Handoff)) {
        $arguments += @("-HandoffPath", $Handoff)
    }
    if (-not [string]::IsNullOrWhiteSpace($Signature)) {
        $arguments += @("-SignaturePath", $Signature)
    }
    if (-not [string]::IsNullOrWhiteSpace($TimestampResponse)) {
        $arguments += @("-TestTimestampResponsePath", $TimestampResponse)
    }
    foreach ($root in $TrustedRoots) {
        $arguments += @("-TestTrustedRootPath", $root)
    }
    if ($AssumeRevocationGood) {
        $arguments += "-TestAssumeRevocationGood"
    }
    $output = & pwsh @arguments 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output | Out-String)
    }
}

function Assert-Succeeded {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Result.ExitCode -ne 0) {
        throw "$Label failed: $($Result.Output)"
    }
}

function Assert-Rejected {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Label,
        [string]$OutputPattern
    )

    if ($Result.ExitCode -eq 0) {
        throw "$Label was unexpectedly accepted."
    }
    if (-not [string]::IsNullOrWhiteSpace($OutputPattern) -and
        $Result.Output -notmatch $OutputPattern) {
        throw "$Label failed for the wrong reason: $($Result.Output)"
    }
}

function New-RandomSerial {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "This helper returns random in-memory test bytes and does not mutate external state."
    )]
    param()

    $serial = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $serial[0] = $serial[0] -band 0x7f
    if (-not ($serial | Where-Object { $_ -ne 0 })) {
        $serial[$serial.Length - 1] = 1
    }
    return $serial
}

function New-TestRoot {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "This helper constructs an in-memory test certificate and does not mutate external state."
    )]
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][DateTimeOffset]$NotBefore,
        [Parameter(Mandatory = $true)][DateTimeOffset]$NotAfter
    )

    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $request =
            [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
                "CN=$Name",
                $rsa,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
            )
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
                $true,
                $false,
                0,
                $true
            )
        )
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
                    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign,
                $true
            )
        )
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new(
                $request.PublicKey,
                $false
            )
        )
        $certificate = $request.CreateSelfSigned($NotBefore, $NotAfter)
        return [pscustomobject]@{
            Certificate = $certificate
            Key = $rsa
        }
    }
    catch {
        $rsa.Dispose()
        throw
    }
}

function New-TestLeaf {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "This helper constructs an in-memory test certificate and does not mutate external state."
    )]
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $Issuer,
        [Parameter(Mandatory = $true)][string]$EkuOid,
        [Parameter(Mandatory = $true)][DateTimeOffset]$NotBefore,
        [Parameter(Mandatory = $true)][DateTimeOffset]$NotAfter,
        [switch]$CriticalExclusiveEku,
        [Uri]$CrlUri
    )

    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $publicCertificate = $null
    try {
        $request =
            [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
                "CN=$Name",
                $rsa,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
            )
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
                $false,
                $false,
                0,
                $true
            )
        )
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
                $true
            )
        )
        $enhancedKeyUsages =
            [System.Security.Cryptography.OidCollection]::new()
        [void]$enhancedKeyUsages.Add(
            [System.Security.Cryptography.Oid]::new($EkuOid)
        )
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
                $enhancedKeyUsages,
                [bool]$CriticalExclusiveEku
            )
        )
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new(
                $request.PublicKey,
                $false
            )
        )
        if ($null -ne $CrlUri) {
            $request.CertificateExtensions.Add(
                [System.Security.Cryptography.X509Certificates.CertificateRevocationListBuilder]::BuildCrlDistributionPointExtension(
                    [string[]]@($CrlUri.AbsoluteUri),
                    $false
                )
            )
        }
        $publicCertificate = $request.Create(
            $Issuer,
            $NotBefore,
            $NotAfter,
            (New-RandomSerial)
        )
        $certificate =
            [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey(
                $publicCertificate,
                $rsa
            )
        $publicCertificate.Dispose()
        return [pscustomobject]@{
            Certificate = $certificate
            Key = $rsa
        }
    }
    catch {
        if ($null -ne $publicCertificate) {
            $publicCertificate.Dispose()
        }
        $rsa.Dispose()
        throw
    }
}

function New-OuterCms {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "This helper constructs an in-memory test CMS and does not mutate external state."
    )]
    param(
        [Parameter(Mandatory = $true)][byte[]]$Content,
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $SignerCertificate
    )

    $contentInfo =
        [System.Security.Cryptography.Pkcs.ContentInfo]::new($Content)
    $cms =
        [System.Security.Cryptography.Pkcs.SignedCms]::new(
            $contentInfo,
            $true
        )
    $signer =
        [System.Security.Cryptography.Pkcs.CmsSigner]::new(
            [System.Security.Cryptography.Pkcs.SubjectIdentifierType]::IssuerAndSerialNumber,
            $SignerCertificate
        )
    $signer.DigestAlgorithm =
        [System.Security.Cryptography.Oid]::new($sha256Oid)
    $signer.IncludeOption =
        [System.Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    $cms.ComputeSignature($signer, $true)
    return $cms
}

function New-TimestampResponse {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "This helper constructs in-memory RFC 3161 test bytes and does not mutate external state."
    )]
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.Pkcs.Rfc3161TimestampRequest]
        $Request,
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $TimestampCertificate,
        [Parameter(Mandatory = $true)][DateTimeOffset]$Timestamp,
        [Parameter(Mandatory = $true)][ref]$TimestampTokenBytes
    )

    if ($Request.HashAlgorithmId.Value -cne $sha256Oid) {
        throw "The test timestamp request did not use SHA-256."
    }
    $tstInfo = [TopiaForgeRfc3161TestEncoding]::BuildTstInfo(
        ($Request.GetMessageHash()).ToArray(),
        $Timestamp,
        $sha256Oid
    )

    $contentInfo =
        [System.Security.Cryptography.Pkcs.ContentInfo]::new(
            [System.Security.Cryptography.Oid]::new($timestampContentTypeOid),
            $tstInfo
        )
    $timestampCms =
        [System.Security.Cryptography.Pkcs.SignedCms]::new(
            $contentInfo,
            $false
        )
    $timestampSigner =
        [System.Security.Cryptography.Pkcs.CmsSigner]::new(
            [System.Security.Cryptography.Pkcs.SubjectIdentifierType]::IssuerAndSerialNumber,
            $TimestampCertificate
        )
    $timestampSigner.DigestAlgorithm =
        [System.Security.Cryptography.Oid]::new($sha256Oid)
    $timestampSigner.IncludeOption =
        [System.Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    $timestampCertificateHash =
        [System.Security.Cryptography.SHA256]::HashData(
            $TimestampCertificate.RawData
        )
    [void]$timestampSigner.SignedAttributes.Add(
        [System.Security.Cryptography.AsnEncodedData]::new(
            [System.Security.Cryptography.Oid]::new(
                "1.2.840.113549.1.9.16.2.47"
            ),
            [TopiaForgeRfc3161TestEncoding]::BuildSigningCertificateV2(
                $timestampCertificateHash
            )
        )
    )
    $timestampCms.ComputeSignature($timestampSigner, $true)

    $TimestampTokenBytes.Value = [byte[]]$timestampCms.Encode()
    return ,[TopiaForgeRfc3161TestEncoding]::BuildGrantedResponse(
        $TimestampTokenBytes.Value
    )
}

function New-TestTimestampedSignature {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "This internal regression helper writes only to the test-owned temporary directory."
    )]
    param(
        [Parameter(Mandatory = $true)][string]$HandoffPath,
        [Parameter(Mandatory = $true)][string]$SignaturePath,
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $SignerCertificate,
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $TimestampCertificate,
        [Parameter(Mandatory = $true)][DateTimeOffset]$Timestamp,
        [string]$TimestampResponsePath,
        [switch]$SkipResponseValidation
    )

    $cms = New-OuterCms `
        -Content ([System.IO.File]::ReadAllBytes($HandoffPath)) `
        -SignerCertificate $SignerCertificate
    $request =
        [System.Security.Cryptography.Pkcs.Rfc3161TimestampRequest]::CreateFromSignerInfo(
            $cms.SignerInfos[0],
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            $null,
            $null,
            $true,
            $null
        )
    [byte[]]$timestampTokenBytes = $null
    $responseBytes = New-TimestampResponse -Request $request `
        -TimestampCertificate $TimestampCertificate -Timestamp $Timestamp `
        -TimestampTokenBytes ([ref]$timestampTokenBytes)
    if (-not [string]::IsNullOrWhiteSpace($TimestampResponsePath)) {
        [System.IO.File]::WriteAllBytes($TimestampResponsePath, $responseBytes)
    }
    $consumed = 0
    if ($SkipResponseValidation) {
        $timestampEncoded = $timestampTokenBytes
    }
    else {
        $token = $request.ProcessResponse(
            [System.ReadOnlyMemory[byte]]::new($responseBytes),
            [ref]$consumed
        )
        if ($consumed -ne $responseBytes.Length) {
            throw "The generated test timestamp response has trailing bytes."
        }
        $timestampEncoded = $token.AsSignedCms().Encode()
    }
    $timestampValue =
        [System.Security.Cryptography.AsnEncodedData]::new(
            [System.Security.Cryptography.Oid]::new($timestampAttributeOid),
            $timestampEncoded
        )
    $cms.SignerInfos[0].AddUnsignedAttribute($timestampValue)
    [System.IO.File]::WriteAllBytes($SignaturePath, $cms.Encode())
}

function Set-SigningEnvironment {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "This internal helper mutates only process-scoped synthetic test environment values."
    )]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSAvoidUsingPlainTextForPassword",
        "",
        Justification = "The value is a synthetic disposable PFX password used only by the focused regression test."
    )]
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $LeafCertificate,
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $RootCertificate,
        [Parameter(Mandatory = $true)][string]$Password
    )

    $collection =
        [System.Security.Cryptography.X509Certificates.X509Certificate2Collection]::new()
    [void]$collection.Add($LeafCertificate)
    [void]$collection.Add($RootCertificate)
    $pfx = $collection.Export(
        [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
        $Password
    )
    try {
        [Environment]::SetEnvironmentVariable(
            "WINDOWS_CERTIFICATE_PFX",
            [Convert]::ToBase64String($pfx)
        )
        [Environment]::SetEnvironmentVariable(
            "WINDOWS_CERTIFICATE_PASSWORD",
            $Password
        )
        [Environment]::SetEnvironmentVariable(
            "WINDOWS_TIMESTAMP_URL",
            "https://timestamp.example.test/rfc3161"
        )
    }
    finally {
        [Array]::Clear($pfx, 0, $pfx.Length)
    }
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "topiaforge-handoff-signature-test-$PID"
)
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$environmentNames = @(
    "WINDOWS_CERTIFICATE_PFX",
    "WINDOWS_CERTIFICATE_PASSWORD",
    "WINDOWS_TIMESTAMP_URL",
    "TOPIAFORGE_HANDOFF_SIGNATURE_TEST_MODE"
)
$originalEnvironment = @{}
foreach ($name in $environmentNames) {
    $originalEnvironment[$name] =
        [Environment]::GetEnvironmentVariable($name)
}
$disposables = [System.Collections.Generic.List[System.IDisposable]]::new()
try {
    [Environment]::SetEnvironmentVariable(
        "TOPIAFORGE_HANDOFF_SIGNATURE_TEST_MODE",
        $null
    )
    Assert-Rejected (
        Invoke-HandoffSignature -Mode ValidateCredentials `
            -ExpectedPin ("a" * 64) `
            -TrustedRoots @((Join-Path $temporaryRoot "missing-root.cer")) `
            -AssumeRevocationGood
    ) "Revocation test override outside focused test mode" `
        "test inputs are disabled outside the focused test"

    [Environment]::SetEnvironmentVariable(
        "TOPIAFORGE_HANDOFF_SIGNATURE_TEST_MODE",
        "1"
    )
    $now = [DateTimeOffset]::UtcNow
    $trustedRoot = New-TestRoot -Name "TopiaForge Test Root" `
        -NotBefore $now.AddYears(-2) -NotAfter $now.AddYears(2)
    $disposables.Add($trustedRoot.Certificate)
    $disposables.Add($trustedRoot.Key)
    $otherRoot = New-TestRoot -Name "Untrusted TopiaForge Test Root" `
        -NotBefore $now.AddYears(-2) -NotAfter $now.AddYears(2)
    $disposables.Add($otherRoot.Certificate)
    $disposables.Add($otherRoot.Key)

    $activeSigner = New-TestLeaf -Name "TopiaForge Handoff Test" `
        -Issuer $trustedRoot.Certificate -EkuOid $codeSigningEkuOid `
        -NotBefore $now.AddDays(-10) -NotAfter $now.AddDays(10)
    $disposables.Add($activeSigner.Certificate)
    $disposables.Add($activeSigner.Key)
    $activeTsa = New-TestLeaf -Name "TopiaForge RFC3161 Test TSA" `
        -Issuer $trustedRoot.Certificate -EkuOid $timestampingEkuOid `
        -NotBefore $now.AddDays(-30) -NotAfter $now.AddDays(30) `
        -CriticalExclusiveEku
    $disposables.Add($activeTsa.Certificate)
    $disposables.Add($activeTsa.Key)
    $untrustedTsa = New-TestLeaf -Name "Untrusted RFC3161 Test TSA" `
        -Issuer $otherRoot.Certificate -EkuOid $timestampingEkuOid `
        -NotBefore $now.AddDays(-30) -NotAfter $now.AddDays(30) `
        -CriticalExclusiveEku
    $disposables.Add($untrustedTsa.Certificate)
    $disposables.Add($untrustedTsa.Key)
    $untrustedSigner = New-TestLeaf -Name "Untrusted Handoff Signer" `
        -Issuer $otherRoot.Certificate -EkuOid $codeSigningEkuOid `
        -NotBefore $now.AddDays(-10) -NotAfter $now.AddDays(10)
    $disposables.Add($untrustedSigner.Certificate)
    $disposables.Add($untrustedSigner.Key)

    $rootPath = Join-Path $temporaryRoot "trusted-root.cer"
    $otherRootPath = Join-Path $temporaryRoot "other-root.cer"
    [System.IO.File]::WriteAllBytes(
        $rootPath,
        $trustedRoot.Certificate.Export(
            [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert
        )
    )
    [System.IO.File]::WriteAllBytes(
        $otherRootPath,
        $otherRoot.Certificate.Export(
            [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert
        )
    )

    $handoff = Join-Path $temporaryRoot "release-handoff-v1.json"
    $signature = Join-Path $temporaryRoot "release-handoff-v1.json.p7s"
    $timestampResponse = Join-Path $temporaryRoot "timestamp-response.tsr"
    [System.IO.File]::WriteAllText(
        $handoff,
        "{`"schema`":`"release-handoff-v1`"}`n",
        [System.Text.UTF8Encoding]::new($false)
    )
    New-TestTimestampedSignature -HandoffPath $handoff `
        -SignaturePath (Join-Path $temporaryRoot "fixture-signature.p7s") `
        -SignerCertificate $activeSigner.Certificate `
        -TimestampCertificate $activeTsa.Certificate -Timestamp $now `
        -TimestampResponsePath $timestampResponse

    $password = "test-password-$PID"
    $pin = $activeSigner.Certificate.GetCertHashString(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256
    ).ToLowerInvariant()
    Set-SigningEnvironment -LeafCertificate $activeSigner.Certificate `
        -RootCertificate $trustedRoot.Certificate -Password $password

    Assert-Rejected (
        Invoke-HandoffSignature -Mode ValidateCredentials `
            -ExpectedPin $pin -TrustedRoots @($rootPath)
    ) "Credential validation with unknown revocation state" `
        "RevocationStatusUnknown|OfflineRevocation"
    Assert-Succeeded (
        Invoke-HandoffSignature -Mode ValidateCredentials `
            -ExpectedPin $pin -TrustedRoots @($rootPath) `
            -AssumeRevocationGood
    ) "Credential validation"
    Assert-Succeeded (
        Invoke-HandoffSignature -Mode Sign -ExpectedPin $pin `
            -Handoff $handoff -Signature $signature `
            -TimestampResponse $timestampResponse `
            -TrustedRoots @($rootPath) -AssumeRevocationGood
    ) "Handoff signing"
    if (-not (Test-Path -LiteralPath $signature -PathType Leaf)) {
        throw "Handoff signing did not create the detached CMS."
    }
    $firstSignature = [System.IO.File]::ReadAllBytes($signature)

    # Verification and release-admin VerifyOnly resumptions must not depend on
    # signing secrets or the timestamp endpoint after the P7S exists.
    [Environment]::SetEnvironmentVariable("WINDOWS_CERTIFICATE_PFX", $null)
    [Environment]::SetEnvironmentVariable("WINDOWS_CERTIFICATE_PASSWORD", $null)
    [Environment]::SetEnvironmentVariable("WINDOWS_TIMESTAMP_URL", $null)
    Assert-Succeeded (
        Invoke-HandoffSignature -Mode Verify -ExpectedPin $pin `
            -Handoff $handoff -Signature $signature `
            -TrustedRoots @($rootPath) -AssumeRevocationGood
    ) "Credential-free handoff verification"
    Assert-Rejected (
        Invoke-HandoffSignature -Mode Sign -ExpectedPin $pin `
            -Handoff $handoff -Signature $signature `
            -TrustedRoots @($rootPath) -AssumeRevocationGood
    ) "Credential-free Sign mode"

    Set-SigningEnvironment -LeafCertificate $activeSigner.Certificate `
        -RootCertificate $trustedRoot.Certificate -Password $password
    Assert-Succeeded (
        Invoke-HandoffSignature -Mode Sign -ExpectedPin $pin `
            -Handoff $handoff -Signature $signature `
            -TimestampResponse $timestampResponse `
            -TrustedRoots @($rootPath) -AssumeRevocationGood
    ) "Exact signing rerun"
    if (-not [System.Linq.Enumerable]::SequenceEqual(
            [byte[]]$firstSignature,
            [byte[]][System.IO.File]::ReadAllBytes($signature)
        )) {
        throw "Exact handoff signing rerun mutated the detached CMS."
    }

    [System.IO.File]::AppendAllText($handoff, " ")
    Assert-Rejected (
        Invoke-HandoffSignature -Mode Verify -ExpectedPin $pin `
            -Handoff $handoff -Signature $signature `
            -TrustedRoots @($rootPath) -AssumeRevocationGood
    ) "Tampered handoff"
    [System.IO.File]::WriteAllText(
        $handoff,
        "{`"schema`":`"release-handoff-v1`"}`n",
        [System.Text.UTF8Encoding]::new($false)
    )
    $wrongPin = "a" * 64
    if ($wrongPin -ceq $pin) {
        $wrongPin = "b" * 64
    }
    Assert-Rejected (
        Invoke-HandoffSignature -Mode Verify -ExpectedPin $wrongPin `
            -Handoff $handoff -Signature $signature `
            -TrustedRoots @($rootPath) -AssumeRevocationGood
    ) "Wrong handoff signer pin"
    Assert-Rejected (
        Invoke-HandoffSignature -Mode Verify -ExpectedPin $pin `
            -Handoff $handoff -Signature $signature
    ) "Untrusted signer and TSA chains"

    $bareCms = New-OuterCms `
        -Content ([System.IO.File]::ReadAllBytes($handoff)) `
        -SignerCertificate $activeSigner.Certificate
    [System.IO.File]::WriteAllBytes($signature, $bareCms.Encode())
    Assert-Rejected (
        Invoke-HandoffSignature -Mode Verify -ExpectedPin $pin `
            -Handoff $handoff -Signature $signature `
            -TrustedRoots @($rootPath) -AssumeRevocationGood
    ) "Missing RFC3161 timestamp"

    New-TestTimestampedSignature -HandoffPath $handoff `
        -SignaturePath $signature -SignerCertificate $activeSigner.Certificate `
        -TimestampCertificate $untrustedTsa.Certificate -Timestamp $now
    Assert-Rejected (
        Invoke-HandoffSignature -Mode Verify -ExpectedPin $pin `
            -Handoff $handoff -Signature $signature `
            -TrustedRoots @($rootPath) -AssumeRevocationGood
    ) "Untrusted TSA chain"

    $untrustedSignerPin = $untrustedSigner.Certificate.GetCertHashString(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256
    ).ToLowerInvariant()
    New-TestTimestampedSignature -HandoffPath $handoff `
        -SignaturePath $signature `
        -SignerCertificate $untrustedSigner.Certificate `
        -TimestampCertificate $activeTsa.Certificate -Timestamp $now
    Assert-Rejected (
        Invoke-HandoffSignature -Mode Verify `
            -ExpectedPin $untrustedSignerPin -Handoff $handoff `
            -Signature $signature -TrustedRoots @($rootPath) `
            -AssumeRevocationGood
    ) "Untrusted code-signing chain"

    # Exercise the production revocation policy against a real, locally served
    # CRL. The TSA is absent from the CRL, while the handoff signer was revoked
    # before the RFC 3161 timestamp.
    $revocationServer = [TopiaForgeCrlTestServer]::new()
    $disposables.Add($revocationServer)
    $revokedSigner = New-TestLeaf -Name "Revoked Handoff Signer" `
        -Issuer $trustedRoot.Certificate -EkuOid $codeSigningEkuOid `
        -NotBefore $now.AddDays(-10) -NotAfter $now.AddDays(10) `
        -CrlUri $revocationServer.Uri
    $disposables.Add($revokedSigner.Certificate)
    $disposables.Add($revokedSigner.Key)
    $revocationAwareTsa = New-TestLeaf `
        -Name "Revocation-Aware RFC3161 TSA" `
        -Issuer $trustedRoot.Certificate -EkuOid $timestampingEkuOid `
        -NotBefore $now.AddDays(-10) -NotAfter $now.AddDays(10) `
        -CriticalExclusiveEku -CrlUri $revocationServer.Uri
    $disposables.Add($revocationAwareTsa.Certificate)
    $disposables.Add($revocationAwareTsa.Key)
    $revocationAwareSigner = New-TestLeaf `
        -Name "Revocation-Aware Handoff Signer" `
        -Issuer $trustedRoot.Certificate -EkuOid $codeSigningEkuOid `
        -NotBefore $now.AddDays(-10) -NotAfter $now.AddDays(10) `
        -CrlUri $revocationServer.Uri
    $disposables.Add($revocationAwareSigner.Certificate)
    $disposables.Add($revocationAwareSigner.Key)
    $revokedTsa = New-TestLeaf -Name "Revoked RFC3161 TSA" `
        -Issuer $trustedRoot.Certificate -EkuOid $timestampingEkuOid `
        -NotBefore $now.AddDays(-10) -NotAfter $now.AddDays(10) `
        -CriticalExclusiveEku -CrlUri $revocationServer.Uri
    $disposables.Add($revokedTsa.Certificate)
    $disposables.Add($revokedTsa.Key)
    $crlBuilder =
        [System.Security.Cryptography.X509Certificates.CertificateRevocationListBuilder]::new()
    $crlBuilder.AddEntry(
        $revokedSigner.Certificate,
        $now.AddMinutes(-5),
        [System.Security.Cryptography.X509Certificates.X509RevocationReason]::KeyCompromise
    )
    $crlBuilder.AddEntry(
        $revokedTsa.Certificate,
        $now.AddMinutes(-5),
        [System.Security.Cryptography.X509Certificates.X509RevocationReason]::KeyCompromise
    )
    $crlBytes = $crlBuilder.Build(
        $trustedRoot.Certificate,
        [System.Numerics.BigInteger]::One,
        $now.AddHours(12),
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1,
        $now.AddHours(-1)
    )
    $revocationServer.SetContent($crlBytes)
    $revokedSignerPin = $revokedSigner.Certificate.GetCertHashString(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256
    ).ToLowerInvariant()
    New-TestTimestampedSignature -HandoffPath $handoff `
        -SignaturePath $signature `
        -SignerCertificate $revokedSigner.Certificate `
        -TimestampCertificate $revocationAwareTsa.Certificate `
        -Timestamp $now
    Assert-Rejected (
        Invoke-HandoffSignature -Mode Verify `
            -ExpectedPin $revokedSignerPin -Handoff $handoff `
            -Signature $signature -TrustedRoots @($rootPath)
    ) "Signer revoked before timestamp" `
        "(?s)Windows code-signing certificate chain revocation status.*Revoked"

    $revocationAwareSignerPin =
        $revocationAwareSigner.Certificate.GetCertHashString(
            [System.Security.Cryptography.HashAlgorithmName]::SHA256
        ).ToLowerInvariant()
    New-TestTimestampedSignature -HandoffPath $handoff `
        -SignaturePath $signature `
        -SignerCertificate $revocationAwareSigner.Certificate `
        -TimestampCertificate $revokedTsa.Certificate -Timestamp $now
    Assert-Rejected (
        Invoke-HandoffSignature -Mode Verify `
            -ExpectedPin $revocationAwareSignerPin -Handoff $handoff `
            -Signature $signature -TrustedRoots @($rootPath)
    ) "TSA revoked before timestamp" `
        "(?s)RFC 3161 TSA certificate chain revocation status.*Revoked"

    # A reachable endpoint that deliberately cannot provide a CRL proves the
    # production verifier also rejects indeterminate revocation status.
    $unknownRevocationServer = [TopiaForgeCrlTestServer]::new()
    $disposables.Add($unknownRevocationServer)
    $unknownSigner = New-TestLeaf `
        -Name "Unknown-Revocation Handoff Signer" `
        -Issuer $trustedRoot.Certificate -EkuOid $codeSigningEkuOid `
        -NotBefore $now.AddDays(-10) -NotAfter $now.AddDays(10) `
        -CrlUri $unknownRevocationServer.Uri
    $disposables.Add($unknownSigner.Certificate)
    $disposables.Add($unknownSigner.Key)
    $unknownTsa = New-TestLeaf -Name "Unknown-Revocation RFC3161 TSA" `
        -Issuer $trustedRoot.Certificate -EkuOid $timestampingEkuOid `
        -NotBefore $now.AddDays(-10) -NotAfter $now.AddDays(10) `
        -CriticalExclusiveEku -CrlUri $unknownRevocationServer.Uri
    $disposables.Add($unknownTsa.Certificate)
    $disposables.Add($unknownTsa.Key)
    $unknownSignerPin = $unknownSigner.Certificate.GetCertHashString(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256
    ).ToLowerInvariant()
    New-TestTimestampedSignature -HandoffPath $handoff `
        -SignaturePath $signature `
        -SignerCertificate $unknownSigner.Certificate `
        -TimestampCertificate $unknownTsa.Certificate -Timestamp $now
    Assert-Rejected (
        Invoke-HandoffSignature -Mode Verify `
            -ExpectedPin $unknownSignerPin -Handoff $handoff `
            -Signature $signature -TrustedRoots @($rootPath)
    ) "Unknown online revocation status" `
        "RevocationStatusUnknown|OfflineRevocation"

    $historicalSigner = New-TestLeaf -Name "Historical Handoff Signer" `
        -Issuer $trustedRoot.Certificate -EkuOid $codeSigningEkuOid `
        -NotBefore $now.AddDays(-10) -NotAfter $now.AddDays(-5)
    $disposables.Add($historicalSigner.Certificate)
    $disposables.Add($historicalSigner.Key)
    $historicalPin = $historicalSigner.Certificate.GetCertHashString(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256
    ).ToLowerInvariant()
    New-TestTimestampedSignature -HandoffPath $handoff `
        -SignaturePath $signature `
        -SignerCertificate $historicalSigner.Certificate `
        -TimestampCertificate $activeTsa.Certificate `
        -Timestamp $now.AddDays(-7)
    Assert-Succeeded (
        Invoke-HandoffSignature -Mode Verify -ExpectedPin $historicalPin `
            -Handoff $handoff -Signature $signature `
            -TrustedRoots @($rootPath) -AssumeRevocationGood
    ) "Historically valid timestamped signer"

    $expiredAtTimestampSigner = New-TestLeaf `
        -Name "Expired At Timestamp Handoff Signer" `
        -Issuer $trustedRoot.Certificate -EkuOid $codeSigningEkuOid `
        -NotBefore $now.AddDays(-12) -NotAfter $now.AddDays(-8)
    $disposables.Add($expiredAtTimestampSigner.Certificate)
    $disposables.Add($expiredAtTimestampSigner.Key)
    $expiredAtTimestampPin =
        $expiredAtTimestampSigner.Certificate.GetCertHashString(
            [System.Security.Cryptography.HashAlgorithmName]::SHA256
        ).ToLowerInvariant()
    New-TestTimestampedSignature -HandoffPath $handoff `
        -SignaturePath $signature `
        -SignerCertificate $expiredAtTimestampSigner.Certificate `
        -TimestampCertificate $activeTsa.Certificate `
        -Timestamp $now.AddDays(-7)
    Assert-Rejected (
        Invoke-HandoffSignature -Mode Verify `
            -ExpectedPin $expiredAtTimestampPin -Handoff $handoff `
            -Signature $signature -TrustedRoots @($rootPath) `
            -AssumeRevocationGood
    ) "Signer expired at timestamp time"

    $expiredAtTimestampTsa = New-TestLeaf `
        -Name "Expired At Timestamp RFC3161 TSA" `
        -Issuer $trustedRoot.Certificate -EkuOid $timestampingEkuOid `
        -NotBefore $now.AddDays(-12) -NotAfter $now.AddDays(-8) `
        -CriticalExclusiveEku
    $disposables.Add($expiredAtTimestampTsa.Certificate)
    $disposables.Add($expiredAtTimestampTsa.Key)
    New-TestTimestampedSignature -HandoffPath $handoff `
        -SignaturePath $signature -SignerCertificate $activeSigner.Certificate `
        -TimestampCertificate $expiredAtTimestampTsa.Certificate `
        -Timestamp $now.AddDays(-7) -SkipResponseValidation
    Assert-Rejected (
        Invoke-HandoffSignature -Mode Verify -ExpectedPin $pin `
            -Handoff $handoff -Signature $signature `
            -TrustedRoots @($rootPath) -AssumeRevocationGood
    ) "TSA expired at timestamp time"

    New-TestTimestampedSignature -HandoffPath $handoff `
        -SignaturePath $signature -SignerCertificate $activeSigner.Certificate `
        -TimestampCertificate $activeTsa.Certificate -Timestamp $now
    $duplicateCms =
        [System.Security.Cryptography.Pkcs.SignedCms]::new(
            [System.Security.Cryptography.Pkcs.ContentInfo]::new(
                [System.IO.File]::ReadAllBytes($handoff)
            ),
            $true
        )
    $duplicateCms.Decode([System.IO.File]::ReadAllBytes($signature))
    $timestampValue = @(
        foreach ($attribute in $duplicateCms.SignerInfos[0].UnsignedAttributes) {
            if ($attribute.Oid.Value -ceq $timestampAttributeOid) {
                foreach ($value in $attribute.Values) {
                    $value
                }
            }
        }
    )[0]
    $duplicateCms.SignerInfos[0].AddUnsignedAttribute($timestampValue)
    [System.IO.File]::WriteAllBytes($signature, $duplicateCms.Encode())
    Assert-Rejected (
        Invoke-HandoffSignature -Mode Verify -ExpectedPin $pin `
            -Handoff $handoff -Signature $signature `
            -TrustedRoots @($rootPath) -AssumeRevocationGood
    ) "Duplicate RFC3161 timestamps"

    Set-SigningEnvironment -LeafCertificate $activeSigner.Certificate `
        -RootCertificate $trustedRoot.Certificate -Password $password
    [Environment]::SetEnvironmentVariable("WINDOWS_TIMESTAMP_URL", $null)
    Assert-Rejected (
        Invoke-HandoffSignature -Mode ValidateCredentials `
            -ExpectedPin $pin -TrustedRoots @($rootPath) `
            -AssumeRevocationGood
    ) "Signing credentials without an RFC3161 URL"
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $originalEnvironment[$name]
        )
    }
    for ($index = $disposables.Count - 1; $index -ge 0; $index--) {
        $disposables[$index].Dispose()
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Output "Detached release handoff CMS/RFC3161 signature tests passed."
