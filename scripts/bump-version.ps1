# Usage: .\scripts\bump-version.ps1 <major|minor|patch>
# Example: .\scripts\bump-version.ps1 minor   ->  1.1.0 => 1.2.0

param(
    [Parameter(Mandatory)]
    [ValidateSet("major", "minor", "patch")]
    [string]$Part
)

$csproj = "$PSScriptRoot\..\src\Peak.App\Peak.App.csproj"
$content = Get-Content $csproj -Raw

if ($content -notmatch '<Version>(.*?)</Version>') {
    Write-Error "Could not find <Version> in $csproj"
    exit 1
}

$current = [version]$Matches[1]
$new = switch ($Part) {
    "major" { "$($current.Major + 1).0.0" }
    "minor" { "$($current.Major).$($current.Minor + 1).0" }
    "patch" { "$($current.Major).$($current.Minor).$($current.Build + 1)" }
}

$content = $content -replace '<Version>.*?</Version>', "<Version>$new</Version>"
Set-Content $csproj $content -NoNewline

Write-Host ""
Write-Host "  Version bumped: $current -> $new" -ForegroundColor Green
Write-Host ""
Write-Host "  Next steps:" -ForegroundColor Cyan
Write-Host "    git add -A"
Write-Host "    git commit -m 'release: v$new'"
Write-Host "    git tag v$new"
Write-Host "    git push origin main --tags"
Write-Host ""
