@echo off
rem Launches 7 Days to Die straight into a save, skipping the splash and the news screen.
rem
rem   tools\run-save.bat
rem   tools\run-save.bat "Juvupe Valley" lyova
rem
rem The arguments the game accepts here were read out of GameStartupHelper.parseRawCommandline,
rem which maps them onto GamePrefs:
rem
rem   -skipintro            LaunchSceneScript goes to SceneGame instead of SceneSplash
rem   -skipnewsscreen=true  skips the news page
rem   -LoadSaveGame=true    a FLAG, not a name - it turns auto-loading on
rem   -world=  -name=       GamePrefs.GameWorld and GamePrefs.GameName, the actual save
rem   -gamemode=SP          single player
rem   -noeac                required: a mod with a DLL cannot run under the anti-cheat
rem
rem A .bat rather than a PowerShell line because PowerShell mangles the quotes around a world name
rem with a space in it unless the whole tail is passed through --%, which is easy to forget.

setlocal

set "GAME=D:\SteamLibrary\steamapps\common\7 Days To Die\7DaysToDie.exe"
set "WORLD=%~1"
set "SAVE=%~2"

if "%WORLD%"=="" set "WORLD=Old Lecinise County"
if "%SAVE%"=="" set "SAVE=DD"

if not exist "%GAME%" (
    echo Cannot find the game at:
    echo   %GAME%
    echo Edit GAME at the top of this file.
    pause
    exit /b 1
)

if not exist "%APPDATA%\7DaysToDie\Saves\%WORLD%\%SAVE%" (
    echo No save at "%APPDATA%\7DaysToDie\Saves\%WORLD%\%SAVE%".
    echo Available:
    for /d %%W in ("%APPDATA%\7DaysToDie\Saves\*") do for /d %%S in ("%%~fW\*") do echo    "%%~nxW" "%%~nxS"
    pause
    exit /b 1
)

echo Loading "%WORLD%" / "%SAVE%"
start "" "%GAME%" -skipintro -skipnewsscreen=true -LoadSaveGame=true -gamemode=SP -world="%WORLD%" -name="%SAVE%" -noeac

endlocal
