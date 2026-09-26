[CmdletBinding()]
param(
    [string]$Root = '',
    [switch]$Json
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Split-Path -Parent $PSScriptRoot }

function Get-PeMachine {
    param([Parameter(Mandatory = $true)][string]$Path)
    $stream = $null
    $reader = $null
    try {
        $stream = [System.IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
        $reader = New-Object System.IO.BinaryReader($stream)
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw 'The file has no valid DOS/PE header.'
        }
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt ($stream.Length - 24)) {
            throw 'The PE header offset is outside the file.'
        }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw 'The PE signature is invalid.'
        }
        return $reader.ReadUInt16()
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        elseif ($null -ne $stream) { $stream.Dispose() }
    }
}

function New-Check {
    param([string]$Name, [bool]$Ready, [string]$Detail)
    return [pscustomobject]@{ Name = $Name; Ready = $Ready; Detail = $Detail }
}

function Test-PeFile {
    param([string]$Name, [string]$Path, [int]$Machine)
    if (-not [System.IO.File]::Exists($Path)) {
        return New-Check $Name $false ("Missing file: {0}" -f $Path)
    }
    try {
        $actual = Get-PeMachine $Path
        if ($actual -ne $Machine) {
            return New-Check $Name $false ("PE machine 0x{0:X4}; expected 0x{1:X4}: {2}" -f $actual, $Machine, $Path)
        }
        return New-Check $Name $true ("PE machine 0x{0:X4}: {1}" -f $actual, $Path)
    }
    catch {
        return New-Check $Name $false ("Unreadable PE header: {0}; {1}" -f $Path, $_.Exception.Message)
    }
}

function Test-DotNetFramework {
    param([Microsoft.Win32.RegistryView]$View, [string]$Folder)
    $base = $null
    $installed = $false
    $release = 0
    try {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', $View)
        foreach ($profile in @('Full')) {
            $key = $base.OpenSubKey("SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\$profile")
            if ($null -ne $key) {
                try {
                    if ($key.GetValue('Install', 0) -eq 1) { $installed = $true }
                    $release = [int]$key.GetValue('Release', 0)
                }
                finally { $key.Dispose() }
            }
        }
    }
    finally { if ($null -ne $base) { $base.Dispose() } }
    $runtime = Join-Path $env:WINDIR "Microsoft.NET\$Folder\v4.0.30319"
    $present = [System.IO.File]::Exists((Join-Path $runtime 'clr.dll')) -and
        [System.IO.File]::Exists((Join-Path $runtime 'mscorlib.dll'))
    return [pscustomobject]@{
        Name = ".NET Framework 4.8+ ($View)"
        Ready = $installed -and $release -ge 528040 -and $present
        Detail = "Registry install flag: $installed; Release: $release (minimum 528040); runtime files: $present; $runtime"
        Release = $release
    }
}

function Find-CameraDirectory {
    param([string]$ApplicationRoot)
    $programFiles64 = [Environment]::GetEnvironmentVariable('ProgramW6432')
    if ([string]::IsNullOrEmpty($programFiles64)) {
        $programFiles64 = [Environment]::GetFolderPath('ProgramFiles')
    }
    $programFiles32 = [Environment]::GetFolderPath('ProgramFilesX86')
    $candidates = @([Environment]::GetEnvironmentVariable('NEFU_OSCAM_DIR'))
    if (-not [string]::IsNullOrEmpty($programFiles64)) {
        $candidates += Join-Path $programFiles64 'Oscam'
    }
    if (-not [string]::IsNullOrEmpty($programFiles32)) {
        $candidates += Join-Path $programFiles32 'Oscam'
    }
    $candidates += $ApplicationRoot
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrEmpty($candidate) -and
            [System.IO.File]::Exists((Join-Path $candidate 'OEApi64.dll'))) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    return $null
}

function Test-Ch297Registration {
    param([string]$ExpectedDll)
    $base = $null
    $progId = $null
    $server = $null
    try {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('ClassesRoot', 'Registry32')
        $progId = $base.OpenSubKey('PMTCount.PMTCounter\CLSID')
        if ($null -eq $progId) {
            return New-Check 'CH297 32-bit COM registration' $false 'PMTCount.PMTCounter is not registered in the 32-bit registry view.'
        }
        $classId = [string]$progId.GetValue('')
        $guid = [Guid]::Empty
        if (-not [Guid]::TryParse($classId, [ref]$guid)) {
            return New-Check 'CH297 32-bit COM registration' $false 'The registered CLSID is not a valid GUID.'
        }
        $server = $base.OpenSubKey(('CLSID\{0}\InprocServer32' -f $guid.ToString('B')))
        if ($null -eq $server) {
            return New-Check 'CH297 32-bit COM registration' $false 'The registered CLSID has no InprocServer32 entry.'
        }
        $registeredPath = [Environment]::ExpandEnvironmentVariables([string]$server.GetValue('')).Trim('"')
        if ([string]::IsNullOrWhiteSpace($registeredPath) -or
            -not [System.IO.Path]::IsPathRooted($registeredPath)) {
            return New-Check 'CH297 32-bit COM registration' $false 'InprocServer32 must identify an absolute DLL path.'
        }
        $registeredPath = [System.IO.Path]::GetFullPath($registeredPath)
        $expectedPath = [System.IO.Path]::GetFullPath($ExpectedDll)
        if (-not [StringComparer]::OrdinalIgnoreCase.Equals($registeredPath, $expectedPath)) {
            return New-Check 'CH297 32-bit COM registration' $false `
                ("Registration points to {0}. Align the vendor registration with {1}." -f $registeredPath, $expectedPath)
        }
        return Test-PeFile 'CH297 32-bit COM registration' $registeredPath 0x014C
    }
    finally {
        if ($null -ne $server) { $server.Dispose() }
        if ($null -ne $progId) { $progId.Dispose() }
        if ($null -ne $base) { $base.Dispose() }
    }
}

try {
    $Root = [System.IO.Path]::GetFullPath($Root)
    if (-not [System.IO.Directory]::Exists($Root)) {
        throw "Application root does not exist: $Root"
    }
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'This preflight runs on Windows only.'
    }
    $checks = New-Object 'System.Collections.Generic.List[object]'
    $os = New-Check '64-bit Windows' ([Environment]::Is64BitOperatingSystem) 'The integrated application targets x64; the CH297 bridge targets x86.'
    $checks.Add($os)
    $net64 = Test-DotNetFramework 'Registry64' 'Framework64'
    $checks.Add($net64)
    $checks.Add((Test-DotNetFramework 'Registry32' 'Framework'))
    $ui = Test-PeFile 'Integrated application' (Join-Path $Root 'NEFU_China_iDEC_TotalControl_V7_0.exe') 0x8664
    $checks.Add($ui)
    $ch297 = Join-Path $Root 'runtime\ch297'
    $checks.Add((Test-PeFile 'CH297 bridge' (Join-Path $ch297 'NEFU_CH297_Bridge_V6_3_x86.exe') 0x014C))
    $pmtDll = Join-Path $ch297 'PMTCount.dll'
    $checks.Add((Test-PeFile 'CH297 vendor DLL' $pmtDll 0x014C))
    $calibration = Join-Path $ch297 'para.ini'
    $calibrationPresent = [System.IO.File]::Exists($calibration)
    if ($calibrationPresent) { $calibrationPresent = (Get-Item -LiteralPath $calibration).Length -gt 0 }
    $checks.Add((New-Check 'CH297 unit configuration' $calibrationPresent `
        ("Nonempty file required: {0}. Unit calibration content is not validated by this check." -f $calibration)))
    $checks.Add((Test-Ch297Registration $pmtDll))
    $cameraDirectory = Find-CameraDirectory $Root
    if ($null -eq $cameraDirectory) {
        $checks.Add((New-Check 'Camera vendor SDK' $false `
            'OEApi64.dll was not found in the application search paths. Install the vendor SDK or set NEFU_OSCAM_DIR; see BUILDING.md.'))
    }
    else {
        $checks.Add((Test-PeFile 'Camera vendor SDK' (Join-Path $cameraDirectory 'OEApi64.dll') 0x8664))
    }
    $allReady = @($checks | Where-Object { -not $_.Ready }).Count -eq 0
    $uiReady = $os.Ready -and $net64.Ready -and $ui.Ready
    $scope = 'Read-only file, PE-header, and registry checks. No project executables or vendor DLLs are loaded, no COM objects are activated, and no ports or devices are opened. Drivers, SDK transitive dependencies, calibration correctness, and hardware operation are not tested.'
    if ($Json) {
        [pscustomobject]@{
            Root = $Root
            UiPrerequisiteFilesPresent = $uiReady
            AllPrerequisitesPresent = $allReady
            Checks = @($checks.ToArray())
            Scope = $scope
        } | ConvertTo-Json -Depth 5
    }
    else {
        Write-Output 'NEFU-China Total Control V7.0 | Runtime Preflight'
        Write-Output ("Root: {0}" -f $Root)
        foreach ($check in $checks) {
            $status = if ($check.Ready) { 'PRESENT' } else { 'ACTION' }
            Write-Output ("[{0}] {1}: {2}" -f $status, $check.Name, $check.Detail)
        }
        Write-Output ("UI prerequisite files present: {0}" -f $uiReady)
        Write-Output ("All checked prerequisites present: {0}" -f $allReady)
        Write-Output $scope
        Write-Output 'Use the build and operation manual for device commissioning before acquisition or actuation.'
    }
    if ($allReady) { exit 0 }
    exit 2
}
catch {
    if ($Json) {
        [pscustomobject]@{ Error = $_.Exception.Message; ExitCode = 1 } | ConvertTo-Json
    }
    else { Write-Output ("[ERROR] Runtime preflight failed: {0}" -f $_.Exception.Message) }
    exit 1
}

