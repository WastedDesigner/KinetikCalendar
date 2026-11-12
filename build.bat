@echo off
cd /d "%~dp0"
rem Builds a single executable with the .NET 8 SDK; requires the .NET 8 Desktop Runtime to run.
rem "build.bat package" also makes KinetikCalendar.zip (the exe, README and licence) for a release.
if not exist icon.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1 || exit /b 1
set OUT=%LOCALAPPDATA%\Kinetik\Build\publish-KinetikCalendar
dotnet publish KinetikCalendar.csproj -c Release -nologo -v q -p:PublishSingleFile=true -o "%OUT%" || exit /b 1
copy /y "%OUT%\KinetikCalendar.exe" KinetikCalendar.new.exe >nul || exit /b 1
rem Keep previous binaries. Never delete recovery material during a build.
if not exist "..\Old Versions\" mkdir "..\Old Versions" || exit /b 1
if exist KinetikCalendar.exe (
  for /f %%t in ('powershell -NoProfile -Command "[Guid]::NewGuid().ToString('N')"') do move /y KinetikCalendar.exe "..\Old Versions\KinetikCalendar-%%t.exe" >nul
  if exist KinetikCalendar.exe exit /b 1
)
move /y KinetikCalendar.new.exe KinetikCalendar.exe >nul || exit /b 1
echo Built KinetikCalendar.exe
if /i not "%~1"=="package" exit /b 0
powershell -NoProfile -ExecutionPolicy Bypass -File package.ps1 -App KinetikCalendar
if errorlevel 1 exit /b 1
exit /b 0
