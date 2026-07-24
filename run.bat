@echo off
setlocal enabledelayedexpansion

rem Windows equivalent of run.sh. Usage:
rem   run.bat [--faolan|--web|--cdn|--client|--client2|--claude|--clean] [extra args...]

set "command=%~1"
if not "%command%"=="" shift

rem Collect the remaining args (everything after the command) into ARGS, preserving quoting.
set "ARGS="
:collect
if "%~1"=="" goto dispatch
set "ARGS=!ARGS! %1"
shift
goto collect

:dispatch
if "%command%"=="--faolan"  goto faolan
if "%command%"=="--web"     goto web
if "%command%"=="--cdn"     goto cdn
if "%command%"=="--client"  goto client
if "%command%"=="--client2" goto client2
if "%command%"=="--claude"  goto claude
if "%command%"=="--clean"   goto clean
goto usage

:faolan
dotnet run --project .\Faolan\Faolan.csproj -- %ARGS%
goto end

:web
dotnet run --project .\Faolan.Web\Faolan.Web.csproj -- %ARGS%
goto end

:cdn
dotnet run --project .\Faolan.Web.Content\Faolan.Web.Content.csproj -- ^
    --CdnMirror:Store "C:\games\mirror-archive" ^
    --CdnMirror:Ip 127.0.0.1 ^
    --CdnMirror:Port 7081 ^
    %ARGS%
goto end

:client
dotnet run --project .\Faolan.ClientLauncher\Faolan.ClientLauncher.csproj --no-build -- ^
    --console ^
    --universeaddr 127.0.0.1:7010 ^
    --cdn http://127.0.0.1:7081 ^
    --launcher-notes "http://127.0.0.1:7081/_stats?UniverseName=%%s&Language=%%s" ^
    %ARGS%
goto end

:client2
dotnet run --project .\Faolan.ClientLauncher\Faolan.ClientLauncher.csproj --no-build -- ^
    --exe "C:\Games\Age of Conan 2\AgeOfConan.exe" ^
    --console ^
    --universeaddr 127.0.0.1:7010 ^
    --cdn http://127.0.0.1:7081 ^
    --launcher-notes "http://127.0.0.1:7081/_stats?UniverseName=%%s&Language=%%s" ^
    %ARGS%
goto end

:claude
claude --dangerously-load-development-channels server:faolan-chat %ARGS%
goto end

:clean
for /d /r %%d in (bin obj) do @if exist "%%d" rd /s /q "%%d"
goto end

:usage
echo Unknown command: %command% 1>&2
echo Usage: %~nx0 [--faolan^|--web^|--cdn^|--client^|--client2^|--claude^|--clean] [extra args...] 1>&2
exit /b 1

:end
endlocal
