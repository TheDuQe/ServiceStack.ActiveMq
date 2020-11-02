for /f "delims=" %%F in ('dir %~dp0*.sln /b /o-n') do set filename=%%F
del .git\gitversion_cache /Q
.\BuildProcess\gitversion.exe /output buildserver
.\BuildProcess\gitversion.exe /diag
dotnet build ".\%filename%" --configuration "Release" /p:Platform="Any CPU"  /flp:"v=diag;logfile=C:\Temp\Build.txt"
dotnet test ".\%filename%" --logger:Trx
PAUSE