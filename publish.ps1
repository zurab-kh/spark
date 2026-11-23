$env:Path = "$env:LOCALAPPDATA\Microsoft\dotnet;" + $env:Path
dotnet publish src/Strela.App -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o release/Strela
