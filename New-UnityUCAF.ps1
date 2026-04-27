# New-UnityUCAF.ps1
# Adds UCAF to an existing Unity project
# Usage: .\New-UnityUCAF.ps1 -ProjectPath "C:\Projects\MyGame"

param(
    [Parameter(Mandatory=$true)]
    [string]$ProjectPath,

    [string]$UnityVersion = "6000.4.3f1",

    [string]$UcafSource = "https://github.com/honzamalak-afk/ucaf.git",

    [string]$RenderPipeline = "URP"
)

$ErrorActionPreference = "Stop"

$projectPath = (Resolve-Path $ProjectPath).Path
$workspacePath = Join-Path $projectPath "ucaf_workspace"
$manifestPath = Join-Path $projectPath "Packages\manifest.json"
$templatePath = Join-Path $PSScriptRoot "WorkspaceTemplate~"
$unityExe = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Unity.exe"

Write-Host "Setting up UCAF for: $projectPath"

# 1. Copy workspace template
if (-not (Test-Path $workspacePath)) {
    Copy-Item -Recurse $templatePath $workspacePath
    Write-Host "  Workspace created"
} else {
    Write-Host "  Workspace already exists, skipping"
}

# 2. Patch ucaf_config.json
$configPath = Join-Path $workspacePath "ucaf_config.json"
$config = Get-Content $configPath | ConvertFrom-Json
$config.unity_project_path = $projectPath -replace "\\", "/"
$config.ucaf_workspace = $workspacePath -replace "\\", "/"
$config.unity_executable = $unityExe -replace "\\", "/"
$config.render_pipeline = $RenderPipeline
$config | ConvertTo-Json -Depth 10 | Set-Content $configPath
Write-Host "  ucaf_config.json patched"

# 3. Add UCAF to Packages/manifest.json
if (Test-Path $manifestPath) {
    $manifest = Get-Content $manifestPath | ConvertFrom-Json
    if (-not $manifest.dependencies.'com.ucaf') {
        $manifest.dependencies | Add-Member -NotePropertyName "com.ucaf" -NotePropertyValue $UcafSource
        $manifest | ConvertTo-Json -Depth 10 | Set-Content $manifestPath
        Write-Host "  com.ucaf added to manifest.json"
    } else {
        Write-Host "  com.ucaf already in manifest.json, skipping"
    }
} else {
    Write-Host "  WARNING: manifest.json not found at $manifestPath"
}

# 4. Generate CLAUDE.md
$claudeMdPath = Join-Path $projectPath "CLAUDE.md"
if (-not (Test-Path $claudeMdPath)) {
    $projectName = Split-Path $projectPath -Leaf
    $claudeMd = @"
# $projectName

## Stack
- Unity $UnityVersion
- Render Pipeline: $RenderPipeline
- UCAF workspace: ./ucaf_workspace/

## Jak pracovat
- Vždy používej UCAF příkazy pro manipulaci se scénami, objekty a komponentami
- edit_file vždy s compile=true
- get_console vždy s parametrem since
- Po každém vyřešeném bugu zavolej log_bug

## UCAF
Workspace je aktivní na: $($workspacePath -replace "\\", "/")
Příkazy zapisuj do: ucaf_workspace/commands/pending/
Výsledky čti z: ucaf_workspace/commands/done/
"@
    Set-Content $claudeMdPath $claudeMd
    Write-Host "  CLAUDE.md vygenerovan"
} else {
    Write-Host "  CLAUDE.md already exists, skipping"
}

Write-Host ""
Write-Host "UCAF setup complete. Otevri projekt v Unity - UCAF se automaticky aktivuje."
