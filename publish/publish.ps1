param(
    [Parameter(Mandatory=$true)][string]$version) 

$diff = (git diff)
if ($diff) {
    Write-Error "Repository must not have any changes before pack"
    exit 1
}

# back 2 directories from the current file
$rootDir = ($MyInvocation.MyCommand.Path | Split-Path | Split-Path)

$versionFile = "$rootDir\v$version.txt"
$versionTag = "v$version"
$versionBranch = "working-v$version"

if (!(Test-Path $versionFile)) {
    git tag "$versionTag"
    git checkout -b "$versionBranch"
    New-Item -path "$rootDir" -name "v$version.txt" -type "file" -value ""
    git add $versionFile
    git commit -m "Deployed $project"
} elseif ((git branch --show-current) -ne $versionBranch) {
    Write-Error "Incorrect branch"
    exit 1
}

$publishApps = @(
    "$rootDir\TestDynamo\TestDynamo.fsproj",
    "$rootDir\TestDynamo.Lambda\TestDynamo.Lambda.fsproj",
    "$rootDir\TestDynamo.Serialization\TestDynamo.Serialization.fsproj")

$testApps = @(
    "$rootDir\tests\TestDynamo.Tests\TestDynamo.Tests.fsproj")

Write-Host "Pre processing project files"
node "$rootDir\publish\project-parser\init.js" @($testApps + $publishApps) --version "$version"
if (-not $?) { exit 1 }

git add @($testApps + $publishApps)
git commit -m "fsproj changes"

$nugetKey = Read-Host "Enter a nuget key" -MaskInput
$publishApps |
    ForEach-Object -Process {
        Write-Host "Packing $_"
        $project = $_

        if (Select-String -Path $versionFile -Pattern $project -SimpleMatch) {
            Write-Host "Project $_ already done"
            return
        }

        $packResult = (dotnet pack `
            "$project" `
            --configuration Release `
            -p:PackageVersion="$version")
            
        if (-not $?) {
            Write-Error "ERROR $packResult"
            exit 1
        }

        $match = select-string "Successfully created package '([\w-\.\\/:]+).nupkg'" -inputobject $packResult
        if ($match) {
            $nugetPackage = "$($match.Matches.Groups[1].Captures[0].Value).nupkg"
        } else {
            Write-Error "Could not find nupkg file name"
            exit 1
        }

        $match = select-string "Successfully created package '([^']+).snupkg'" -inputobject $packResult
        if ($match) {
            $symbolsPackage = "$($match.Matches.Groups[1].Captures[0].Value).snupkg"
        } else {
            Write-Error "Could not find snupkg file name"
            exit 1
        }

        @($nugetPackage, $symbolsPackage) | ForEach-Object -Process {
            dotnet nuget push `
                -s https://api.nuget.org/v3/index.json `
                "$_" `
                -k "$nugetKey" `
                --skip-duplicate
                
            if (-not $?) {
                Write-Error "ERROR"
                exit 1
            }
        }

        add-content -Path "$versionFile" -Value "$project"
        git add $versionFile
        git commit -m "Deployed $project"
    }

$testApps |
    ForEach-Object -Process {
        dotnet test "$_"

        if (-not $?) { exit 1}
    }

git push origin "$versionTag"
git reset HEAD --hard
git checkout main
git branch -D $versionBranch
Write-Host "Deploy complete"