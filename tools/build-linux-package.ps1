# Builds a self-contained Linux command line package of LLMBench (no .NET needed on the target machine).
#
#   powershell -ExecutionPolicy Bypass -File tools\build-linux-package.ps1 [-ServerUrl http://127.0.0.1:8081]
#
# Result: dist\llmbench-linux\  (copy this folder to the Linux machine that runs llama-server)
param([string]$ServerUrl = "http://127.0.0.1:8081")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist\llmbench-linux"

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish (Join-Path $root "src\LLMBench.Cli") -c Release -r linux-x64 --self-contained true `
    -p:PublishSingleFile=true -p:InvariantGlobalization=true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# workspace next to the executable: catalogs, prompts, run defaults, empty runs folder
Copy-Item -Recurse (Join-Path $root "catalogs") (Join-Path $out "catalogs")
Copy-Item -Recurse (Join-Path $root "prompts") (Join-Path $out "prompts")
Copy-Item -Recurse (Join-Path $root "auto") (Join-Path $out "auto")
New-Item -ItemType Directory (Join-Path $out "runs") | Out-Null
$defaults = Get-Content (Join-Path $root "run-defaults.json") -Raw | ConvertFrom-Json
$defaults.serverUrl = $ServerUrl
# UTF-8 without BOM (works in Windows PowerShell 5.1 and PowerShell 7)
[System.IO.File]::WriteAllText((Join-Path $out "run-defaults.json"), ($defaults | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding $false))
Copy-Item (Join-Path $root "tools\README-LINUX.md") (Join-Path $out "README.md")

Write-Host "Linux package: $out (server $ServerUrl)"
