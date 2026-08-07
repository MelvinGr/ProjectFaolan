@echo off
setlocal enabledelayedexpansion

rem Windows equivalent of run.sh. Usage:
rem   run.bat [--faolan|--web|--cdn|--client|--client2|--timeline <dump>|--claude|--clean] [extra args...]
rem Set RDB_DIR to the client RDB folder for --timeline (it decrypts the LDB text inline).

set "command=%~1"
if not "%command%"=="" shift

rem Collect the remaining args (everything after the command) into ARGS, preserving quoting.
rem DUMP is the FIRST of them - the commands that take exactly one operand (--timeline) use it.
set "ARGS="
set "DUMP=%~1"
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
if "%command%"=="--timeline" goto timeline
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
    --Cdn:Store "C:\games\mirror-archive" ^
    --Cdn:ListenAddress 127.0.0.1 ^
    --Cdn:ListenPort 7081 ^
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

:timeline
rem Mirrors run.sh --timeline: ONE dump path, and say so rather than handing the report an empty arg.
if "%DUMP%"=="" goto timeline_usage
if "%RDB_DIR%"=="" set "RDB_DIR=C:\games\Age of Conan\RDB"
dotnet run --project .\Faolan.Importer\Faolan.Importer.csproj -- ^
    --report timeline ^
    --report-arg "%DUMP%" ^
    --RdbDir "%RDB_DIR%"
goto end

:timeline_usage
echo usage: run.bat --timeline ^<capture^>.tshark.json 1>&2
exit /b 1

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
