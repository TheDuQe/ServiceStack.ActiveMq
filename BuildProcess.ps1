Param(
	[Parameter(Mandatory=$false)]
	[ValidateSet("release","debug")] 
	[String]
	$buildConfiguration="debug"
)
Clear-Host
Push-Location $PSScriptRoot
Get-ChildItem -Recurse -Directory | Select-Object -ExpandProperty FullName | Where-Object {$_ -match '[d|D]ependencies|[d|D]istrib|[b|B]in|[o|O]bj'} | Remove-Item -Recurse -Force -ErrorAction Ignore #-Verbose
Get-ChildItem -Path ".\BuildProcess" -Recurse | Select-Object -ExpandProperty FullName | Where-Object {$_ -notmatch 'conf|scripts'} | Remove-Item -Recurse -Force -ErrorAction Ignore #-Verbose

# Install GitVersion.Tool 
dotnet tool install --tool-path .\distrib --verbosity minimal GitVersion.Tool 

.\distrib\dotnet-gitversion /output json /updatewixversionfile >".\distrib\GitVersion.Properties"
if(Test-Path(".\GitVersion_WixVersion.wxi")){Move-Item -Path ".\GitVersion_WixVersion.wxi" -Destination ".\distrib\GitVersion_WixVersion.wxi"}

foreach ($gitversion in (Get-Content -Raw '.\distrib\GitVersion.properties'| ConvertFrom-Json).PSObject.Properties)
{
  # Create new environment variables
  [Environment]::SetEnvironmentVariable($gitversion.Name, $gitversion.Value)
}

Write-Host 
Get-ChildItem -Path Env:\

# call thirdparty if it exists
if(Test-Path(".\BuildProcess\scripts\build.xml"))
{
  ant -f ./BuildProcess/scripts/build.xml dependencies
  if(Test-Path(".\libcache")){Remove-Item(".\libcache") -Recurse -Force -ErrorAction Ignore}
}

# find all solutions to build, either from standard path (.\src) or from BuildProcess.SolutionsBuildOrder.txt file
if(Test-Path(".\BuildProcess.SolutionsBuildOrder.txt"))
{
  Write-Host ('Found SolutionsBuildOrder.txt')
  $allSLN = [System.IO.File]::ReadLines("$($PSScriptRoot)\BuildProcess.SolutionsBuildOrder.txt") | ForEach-Object {  Get-ChildItem $_ }
}

if($null -eq $allSLN){$allSLN = Get-ChildItem -Path '.\' -Filter *.sln -Depth 1}

# call dotnet build
$allSLN | ForEach-Object { 
    dotnet clean  $_.FullName --configuration $buildConfiguration
    dotnet build  $_.FullName --configuration $buildConfiguration /p:PackageOutputPath="..\Distrib\Packages" /p:PackageVersion="$($Env:NuGetVersionV2)" /p:Platform="Any CPU"  /p:nodeReuse="false" /flp:"v=diag;logfile=$PSScriptRoot\\distrib\\Build.txt"
    dotnet test   $_.FullName --no-build  -l "console;verbosity=detailed" 
}

Pop-Location


