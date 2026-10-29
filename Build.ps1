param(
    [switch]$RunTests,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'artifacts'
}
$decoderOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$decoderCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $decoderCompiler)) {
    throw '.NET Framework 4.x C# compiler was not found. Build on 64-bit Windows with .NET Framework 4.8.'
}
New-Item -ItemType Directory -Path $decoderOutput -Force | Out-Null
$decoderExe = Join-Path $decoderOutput 'QQBatchDecoder.exe'
$decoderSources = @('QQBatchDecoder.cs', 'SelfTest.cs', 'AssemblyInfo.cs') | ForEach-Object {
    Join-Path (Join-Path $PSScriptRoot 'source') $_
}
$decoderCompilerArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
    ('/out:' + $decoderExe),
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Web.Extensions.dll'
) + $decoderSources
& $decoderCompiler @decoderCompilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output ('Built: ' + $decoderExe)

if ($RunTests) {
    $decoderTestDir = Join-Path $decoderOutput ('self-test-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $decoderTestDir | Out-Null
    $decoderReport = Join-Path $decoderTestDir 'report.json'
    $decoderVectors = Join-Path $PSScriptRoot 'source\test-vectors.json'
    $decoderTestArgs = '--self-test --vectors "' + $decoderVectors + '" --test-dir "' + $decoderTestDir + '" --report "' + $decoderReport + '"'
    $decoderProcess = Start-Process -FilePath $decoderExe -ArgumentList $decoderTestArgs -WindowStyle Hidden -Wait -PassThru
    if (-not (Test-Path -LiteralPath $decoderReport)) { throw 'Self-tests did not produce a report.' }
    $decoderResult = Get-Content -LiteralPath $decoderReport -Raw -Encoding UTF8 | ConvertFrom-Json
    $decoderResult | ConvertTo-Json -Depth 5
    if ($decoderProcess.ExitCode -ne 0 -or -not $decoderResult.Passed) { throw 'Self-tests failed.' }
}
