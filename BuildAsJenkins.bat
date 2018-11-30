for /f "delims=" %%F in ('dir %~dp0src\*.sln /b /o-n') do set filename=%%F
dotnet build ".\src\%filename%" --configuration "Release" /t:pack /p:Version="3.2.2.831" /p:Platform="Any CPU" /flp:"v=diag;logfile=C:\Temp\Build.txt"
PAUSE