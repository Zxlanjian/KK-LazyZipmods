# Lists, for every plugin dll in the game folder, which methods call into the list / asset loading
# helpers that Sideloader hooks. Used to work out where the startup time is actually spent.
#
#   $env:KOIKATSU_DIR = 'D:\KoikatsuRX22'
#   powershell -File scan-callers.ps1
$ErrorActionPreference = 'Stop'

$game = $env:KOIKATSU_DIR
if (-not $game) { throw 'Set KOIKATSU_DIR to the game folder first.' }

$exe = Join-Path $PSScriptRoot 'bin\Release\net8.0\ilscan.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Build ilscan first: dotnet build -c Release ($exe missing)" }

$files = Get-ChildItem -LiteralPath (Join-Path $game 'BepInEx\plugins') -Recurse -Filter *.dll |
    Where-Object { $_.FullName -notmatch 'Translators|Console|ExtProtocol|IronPython' }

foreach ($f in $files)
{
    foreach ($pat in @('GetCategoryInfo', 'GetListInfo', 'LoadAllAsset', 'Studio\.Info::FindAllAssetName', 'GetAllAssetName', 'LoadListInfoAll', 'GetAssetBundleNameListFromPath'))
    {
        $out = & $exe $f.FullName callers $pat 2>$null
        $hits = $out | Select-String -Pattern ' -> ' | Where-Object { $_.Line -notmatch '^# ' }
        if ($hits)
        {
            Write-Output "===== $($f.Name)  [$pat] ====="
            $hits | ForEach-Object { Write-Output $_.Line }
        }
    }
}
