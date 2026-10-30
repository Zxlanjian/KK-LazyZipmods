# Scans every plugin dll in the game folder and prints the Harmony patch targets that touch the
# list / Studio / card loader surface. Used to find out which third party plugins participate in
# Studio item registration (AccessoriesToStudioItems was found this way).
#
#   $env:KOIKATSU_DIR = 'D:\KoikatsuRX22'
#   powershell -File scan-all.ps1
$ErrorActionPreference = 'Stop'

$game = $env:KOIKATSU_DIR
if (-not $game) { throw 'Set KOIKATSU_DIR to the game folder first.' }

$exe = Join-Path $PSScriptRoot 'bin\Release\net8.0\ilscan.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Build ilscan first: dotnet build -c Release ($exe missing)" }

$files = Get-ChildItem -LiteralPath (Join-Path $game 'BepInEx\plugins') -Recurse -Filter *.dll |
    Where-Object { $_.FullName -notmatch 'Translators|Console|XUnity.AutoTranslator.Plugin.ExtProtocol' }
$pat = 'Studio\.Info|ChaListControl|ChaListData|CommonLib|AssetBundleCheck|SceneInfo|Studio\.Studio|ChaFile|Info::FindAllAssetName|ExcelData|StudioScene|StartScene'

foreach ($f in $files)
{
    $out = & $exe $f.FullName patch ".*" 2>$null
    if (-not $out) { continue }
    $hits = $out | Select-String -Pattern $pat
    if ($hits)
    {
        Write-Output "===== $($f.Name) ====="
        $hits | ForEach-Object { Write-Output $_.Line }
    }
}
