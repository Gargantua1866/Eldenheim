@echo off
rem Starts the sandbox from the project folder so it finds assets\ (animations and sounds).
rem In the window: Enter plays the demo, Enter again stops it, H shows the help.
cd /d "%~dp0"
set BEVY_ASSET_ROOT=%~dp0
if exist target\release\tarnished.exe (
    start "" target\release\tarnished.exe
) else (
    start "" target\debug\tarnished.exe
)
