# Spark for macOS: Spark.app for Apple Silicon and Intel, zipped (release/Spark-macOS-*.zip). Runs on Windows.
$ErrorActionPreference = 'Stop'
$env:Path = "$env:LOCALAPPDATA\Microsoft\dotnet;" + $env:Path
foreach ($rid in 'osx-arm64', 'osx-x64') {
    dotnet publish src/Spark.Mac -c Release -r $rid --self-contained -p:UseAppHost=true -o "release/mac-build/$rid" -nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
python tools/mac/package.py
exit $LASTEXITCODE
