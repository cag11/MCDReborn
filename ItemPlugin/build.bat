@echo off
rem Builds the plugin with the Visual Studio 2019 x64 compiler, twice, and puts both where the app
rem embeds them from (MCDSaveEdit\Logic\PluginAssets). The app carries the built DLLs rather than
rem building them: its own build needs no C++ compiler that way.
rem   MCDRebornItems.dll  - installed as xinput1_3.dll (Steam, the Minecraft Launcher)
rem   MCDRebornDSound.dll - installed as dsound.dll (the Xbox app, whose build never loads XInput)
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
cd /d "%~dp0"
if not exist out mkdir out
if not exist out\dsound mkdir out\dsound
cl /nologo /D_CRT_SECURE_NO_WARNINGS /LD /O2 /EHsc /W4 /MT /std:c++17 ItemPlugin.cpp /Fo:out\ /Fe:out\MCDRebornItems.dll /link /NOLOGO /DEF:exports.def user32.lib kernel32.lib
if errorlevel 1 exit /b %errorlevel%
cl /nologo /D_CRT_SECURE_NO_WARNINGS /DPROXY_DSOUND /LD /O2 /EHsc /W4 /MT /std:c++17 ItemPlugin.cpp /Fo:out\dsound\ /Fe:out\dsound\MCDRebornDSound.dll /link /NOLOGO /DEF:exports_dsound.def user32.lib kernel32.lib
if errorlevel 1 exit /b %errorlevel%
copy /y out\MCDRebornItems.dll ..\MCDSaveEdit\Logic\PluginAssets\MCDRebornItems.dll >nul
copy /y out\dsound\MCDRebornDSound.dll ..\MCDSaveEdit\Logic\PluginAssets\MCDRebornDSound.dll >nul
exit /b %errorlevel%
