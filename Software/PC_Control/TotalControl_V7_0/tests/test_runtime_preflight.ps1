[CmdletBinding()]
param(
    [string]$Checker = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Checker)) {
    $Checker = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\Check-Runtime.ps1'
}

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    [System.IO.Path]::GetFullPath($Checker), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw ($parseErrors | Out-String) }

# Import helper definitions without running registry or installation checks.
$functions = $ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
}, $false)
. ([scriptblock]::Create(($functions | ForEach-Object { $_.Extent.Text }) -join "`n"))

$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$fixture = Join-Path $temporaryRoot ('nefu-runtime-preflight-' + [Guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($fixture)
$script:passed = 0

function Assert-True {
    param([bool]$Condition, [string]$Name)
    if (-not $Condition) { throw "FAILED: $Name" }
    $script:passed++
    Write-Output "PASS: $Name"
}

function New-PeFixture {
    param([string]$Name, [int]$Machine)
    $bytes = New-Object byte[] 128
    [BitConverter]::GetBytes([UInt16]0x5A4D).CopyTo($bytes, 0)
    [BitConverter]::GetBytes([Int32]64).CopyTo($bytes, 0x3C)
    [BitConverter]::GetBytes([UInt32]0x4550).CopyTo($bytes, 64)
    [BitConverter]::GetBytes([UInt16]$Machine).CopyTo($bytes, 68)
    $target = Join-Path $fixture $Name
    [System.IO.File]::WriteAllBytes($target, $bytes)
    return $target
}

try {
    $x64 = New-PeFixture 'mock-x64.dll' 0x8664
    $x86 = New-PeFixture 'mock-x86.dll' 0x014C
    Assert-True ((Get-PeMachine $x64) -eq 0x8664) 'Read x64 PE machine'
    Assert-True ((Get-PeMachine $x86) -eq 0x014C) 'Read x86 PE machine'
    Assert-True (Test-PeFile 'mock x64' $x64 0x8664).Ready 'Accept matching x64 PE machine'
    Assert-True (Test-PeFile 'mock x86' $x86 0x014C).Ready 'Accept matching x86 PE machine'
    Assert-True (-not (Test-PeFile 'mismatch' $x86 0x8664).Ready) 'Reject architecture mismatch'
    Assert-True (-not (Test-PeFile 'missing' (Join-Path $fixture 'missing.dll') 0x8664).Ready) 'Reject missing file'

    $invalid = Join-Path $fixture 'invalid.dll'
    [System.IO.File]::WriteAllBytes($invalid, [byte[]](1, 2, 3))
    Assert-True (-not (Test-PeFile 'truncated' $invalid 0x8664).Ready) 'Reject truncated header'

    $corrupt = [System.IO.File]::ReadAllBytes($x64)
    [BitConverter]::GetBytes([Int32]1048576).CopyTo($corrupt, 0x3C)
    [System.IO.File]::WriteAllBytes($invalid, $corrupt)
    Assert-True (-not (Test-PeFile 'bad offset' $invalid 0x8664).Ready) 'Reject PE offset beyond file'

    $corrupt = [System.IO.File]::ReadAllBytes($x64)
    $corrupt[64] = 0
    [System.IO.File]::WriteAllBytes($invalid, $corrupt)
    Assert-True (-not (Test-PeFile 'bad signature' $invalid 0x8664).Ready) 'Reject invalid PE signature'

    $previousSdk = [Environment]::GetEnvironmentVariable('NEFU_OSCAM_DIR', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('NEFU_OSCAM_DIR', $fixture, 'Process')
        [System.IO.File]::WriteAllBytes(
            (Join-Path $fixture 'OEApi64.dll'), [System.IO.File]::ReadAllBytes($x64))
        Assert-True ((Find-CameraDirectory $fixture) -eq $fixture) 'Prefer configured camera SDK directory'
    }
    finally { [Environment]::SetEnvironmentVariable('NEFU_OSCAM_DIR', $previousSdk, 'Process') }

    Write-Output ("Runtime preflight helper tests: {0} passed." -f $script:passed)
}
finally {
    $resolved = [System.IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([System.IO.Path]::GetFileName($resolved)).StartsWith('nefu-runtime-preflight-')) {
        throw 'Refusing cleanup outside the generated test fixture directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

