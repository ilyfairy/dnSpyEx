$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RuntimeIdentifier = 'win-x64'
$Configuration = 'Release'
$TargetFramework = 'net10.0-windows'

function Get-RepoRoot {
	if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
		throw 'Could not determine script root.'
	}
	return $PSScriptRoot
}

function Ensure-Directory([string]$Path) {
	if ([string]::IsNullOrWhiteSpace($Path)) {
		throw 'Directory path must not be empty.'
	}
	$null = New-Item -ItemType Directory -Force -Path $Path
}

function Invoke-DotNet([string[]]$Arguments) {
	Write-Host ('dotnet ' + ($Arguments -join ' '))
	& dotnet @Arguments
	if ($LASTEXITCODE) {
		throw "dotnet command failed with exit code $LASTEXITCODE"
	}
}

function Invoke-AppHostPatcher([string]$PatcherExe, [string]$ExePath) {
	Write-Host ("Patching apphost: $ExePath")
	& $PatcherExe $ExePath -d bin
	if ($LASTEXITCODE) {
		throw "AppHostPatcher failed with exit code $LASTEXITCODE"
	}
}

function Copy-DirectoryContents([string]$SourceDir, [string]$DestinationDir, [switch]$OnlyMissing, [string[]]$Include = @()) {
	if (!(Test-Path -LiteralPath $SourceDir)) {
		throw "Source directory not found: $SourceDir"
	}
	Ensure-Directory $DestinationDir
	$sourceRoot = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\')
	Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force -File | ForEach-Object {
		$relativePath = $_.FullName.Substring($sourceRoot.Length).TrimStart('\')
		if ($Include.Count -ne 0) {
			$included = $false
			foreach ($pattern in $Include) {
				if ($_.Name -like $pattern -or $relativePath -like $pattern) {
					$included = $true
					break
				}
			}
			if (-not $included) {
				return
			}
		}
		$destinationPath = Join-Path $DestinationDir $relativePath
		$destinationParent = Split-Path -Parent $destinationPath
		Ensure-Directory $destinationParent
		if ($OnlyMissing -and (Test-Path -LiteralPath $destinationPath)) {
			return
		}
		Copy-Item -LiteralPath $_.FullName -Destination $destinationPath -Force
	}
}

$repoRoot = Get-RepoRoot
$solutionPath = Join-Path $repoRoot 'dnSpy.sln'
$mainProject = Join-Path $repoRoot 'dnSpy\dnSpy\dnSpy.csproj'
$appHostPatcherProject = Join-Path $repoRoot 'Build\AppHostPatcher\AppHostPatcher.csproj'
$appHostPatcherExe = Join-Path $repoRoot ("Build\AppHostPatcher\bin\$Configuration\net48\AppHostPatcher.exe")
$runtimeBuildDir = Join-Path $repoRoot ("dnSpy\dnSpy\bin\$Configuration\$TargetFramework")
$OutputDir = Join-Path $repoRoot ("bin\publish\dnSpy-$RuntimeIdentifier")
$binDir = Join-Path $OutputDir 'bin'

Write-Host "Repository root: $repoRoot"
Write-Host "RID: $RuntimeIdentifier"
Write-Host "Configuration: $Configuration"
Write-Host "Target framework: $TargetFramework"
Write-Host "Output directory: $OutputDir"

Write-Host 'Building solution to populate extension outputs...'
Invoke-DotNet @('build', $solutionPath, '-c', $Configuration, '-f', $TargetFramework)

Write-Host 'Building AppHostPatcher...'
Invoke-DotNet @('build', $appHostPatcherProject, '-c', $Configuration, '-f', 'net48')

if (!(Test-Path -LiteralPath $appHostPatcherExe)) {
	throw "AppHostPatcher not found: $appHostPatcherExe"
}

if (!(Test-Path -LiteralPath $runtimeBuildDir)) {
	throw "Runtime build directory not found: $runtimeBuildDir"
}

Ensure-Directory $binDir

Write-Host 'Publishing dnSpy main application (self-contained)...'
Invoke-DotNet @(
	'publish',
	$mainProject,
	'-c', $Configuration,
	'-f', $TargetFramework,
	'-r', $RuntimeIdentifier,
	'--self-contained', 'true',
	'-o', $binDir
)

Ensure-Directory $OutputDir

Write-Host 'Overlaying missing extension and plugin files from the solution build output into bin/...'
Copy-DirectoryContents -SourceDir $runtimeBuildDir -DestinationDir $binDir -OnlyMissing
Write-Host 'Refreshing extension and plugin assemblies from the solution build output into bin/...'
Copy-DirectoryContents -SourceDir $runtimeBuildDir -DestinationDir $binDir -Include @('*.x.dll', '*.x.pdb', '*.x.deps.json')

$sourceExe = Join-Path $binDir 'dnSpy.exe'
if (Test-Path -LiteralPath $sourceExe) {
	$destinationExe = Join-Path $OutputDir 'dnSpy.exe'
	Copy-Item -LiteralPath $sourceExe -Destination $destinationExe -Force
	Invoke-AppHostPatcher -PatcherExe $appHostPatcherExe -ExePath $destinationExe
}

Write-Host ''
Write-Host 'Self-contained publish completed.'
Write-Host "Publish directory: $OutputDir"
