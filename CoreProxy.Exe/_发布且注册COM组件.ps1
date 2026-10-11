dotnet publish -r win-x64
regsvr32  .\bin\Release\net9.0\win-x64\publish\CoreProxy.dll
Write-Host "-------------------------ok---------------------------"