<#
.SYNOPSIS
  Construit les versions installables de NetShare Hub (Windows et Linux).

.DESCRIPTION
  1. Publie l'application « autonome » (le runtime .NET est embarqué : rien à installer à côté).
  2. Windows : archive .zip portable, et installeur .exe si Inno Setup 6 est installé.
  3. Linux  : paquets .deb et .tar.gz (via WSL, car dpkg-deb et les droits Unix sont nécessaires).

  Les fichiers produits sont déposés dans le dossier « dist » à la racine du dépôt.

.EXAMPLE
  ./packaging/build.ps1                       # tout (win-x64, linux-x64, linux-arm64)
  ./packaging/build.ps1 -Targets win-x64      # Windows uniquement
  ./packaging/build.ps1 -Version 1.2.0
#>
param(
    [string] $Version = '1.0.1',
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64')]
    [string[]] $Targets = @('win-x64', 'linux-x64', 'linux-arm64')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Src/NetShareHub/Desktop/NetShareHub.Desktop.csproj'
$dist = Join-Path $root 'dist'
$publishRoot = Join-Path $dist 'publish'
New-Item -ItemType Directory -Force $dist, $publishRoot | Out-Null

function Publish-Target([string] $rid) {
    $out = Join-Path $publishRoot $rid
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Write-Host "==> Publication $rid" -ForegroundColor Cyan
    dotnet publish $project -c Release -r $rid --self-contained true -o $out `
        -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false -p:PublishReadyToRun=false --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Échec de la publication $rid" }
    return $out
}

foreach ($rid in $Targets) {
    $out = Publish-Target $rid

    if ($rid -eq 'win-x64') {
        $zip = Join-Path $dist "NetShareHub-$Version-win-x64-portable.zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
        Write-Host "    Archive portable : $zip"

        $iscc = Get-Command iscc -ErrorAction SilentlyContinue
        if (-not $iscc) {
            $candidate = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
                Where-Object { Test-Path $_ } | Select-Object -First 1
            if ($candidate) { $iscc = Get-Item $candidate }
        }
        if ($iscc) {
            $source = if ($iscc.PSObject.Properties['Source']) { $iscc.Source } else { $iscc.FullName }
            Write-Host '    Compilation de l''installeur (Inno Setup)…'
            & $source "/DAppVersion=$Version" "/DSourceDir=$out" (Join-Path $PSScriptRoot 'windows/NetShareHub.iss') | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Échec de la compilation Inno Setup' }
            Write-Host "    Installeur : $(Join-Path $dist "NetShareHub-Setup-$Version-x64.exe")"
        }
        else {
            Write-Warning 'Inno Setup 6 introuvable : installeur .exe non généré (archive portable disponible). https://jrsoftware.org/isdl.php'
        }
    }
    else {
        $arch = $rid -replace '^linux-', ''
        if (-not (Get-Command wsl -ErrorAction SilentlyContinue)) {
            Write-Warning "WSL introuvable : paquets Linux $arch non générés. Lancez packaging/linux/build-linux-packages.sh depuis une machine Linux."
            continue
        }
        # Chemins Windows -> chemins WSL (/mnt/c/...)
        $toWsl = { param($p) (wsl wslpath -a ($p -replace '\\', '/')).Trim() }
        $script = & $toWsl (Join-Path $PSScriptRoot 'linux/build-linux-packages.sh')
        Write-Host "==> Paquets Linux $arch (WSL)" -ForegroundColor Cyan
        wsl sh $script (& $toWsl $out) $arch $Version (& $toWsl $dist)
        if ($LASTEXITCODE -ne 0) { throw "Échec des paquets Linux $arch" }
    }
}

Write-Host "`nTerminé. Fichiers dans : $dist" -ForegroundColor Green
Get-ChildItem $dist -File | Select-Object Name, @{n = 'Taille (Mo)'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize | Out-String | Write-Host
