param(
	[ValidateSet('Debug', 'Release')]
	[string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Push-Location $root
try {
	dotnet build 'Extensions/dnSpy.Mcp/dnSpy.Mcp.csproj' -c $Configuration --nologo -m:1
	dotnet build 'Extensions/dnSpy.Mcp.Tests/dnSpy.Mcp.Tests.csproj' -c $Configuration --nologo -m:1
	dotnet build 'Extensions/dnSpy.Mcp.Sdk.Tests/dnSpy.Mcp.Sdk.Tests.csproj' -c $Configuration --nologo -m:1
	dotnet run --project 'Extensions/dnSpy.Mcp.Tests/dnSpy.Mcp.Tests.csproj' -c $Configuration --no-build --no-restore --nologo
	dotnet run --project 'Extensions/dnSpy.Mcp.Sdk.Tests/dnSpy.Mcp.Sdk.Tests.csproj' -c $Configuration --no-build --no-restore --nologo
}
finally {
	Pop-Location
}
