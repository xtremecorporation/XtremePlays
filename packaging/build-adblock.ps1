$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Join-Path $repoRoot 'native\xtreme_adblock_bridge\Cargo.toml'
$bridgeDll = Join-Path $repoRoot 'native\xtreme_adblock_bridge\target\x86_64-pc-windows-msvc\release\xtreme_adblock_bridge.dll'

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw 'Rust stable (cargo) is required to build the Brave ad-block engine. Install it from https://rustup.rs/ and retry.'
}

cargo build --release --target x86_64-pc-windows-msvc --manifest-path $manifest
if ($LASTEXITCODE -ne 0) { throw 'Brave ad-block engine build failed.' }
if (-not (Test-Path -LiteralPath $bridgeDll)) { throw 'The native ad-block engine DLL was not created.' }

Write-Host "Brave ad-block engine built: $bridgeDll"
