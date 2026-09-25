@echo off
rem Builds MCDRebornItems.dll with the Visual Studio 2019 x64 compiler, and puts it where the app
rem embeds it from (MCDSaveEdit\Logic\PluginAssets). The app carries the built DLL rather than
rem building it: its own build needs no C++ compiler that way.
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
cd /d "%~dp0"
if not exist out mkdir out
cl /nologo /D_CRT_SECURE_NO_WARNINGS /LD /O2 /EHsc /W4 /MT /std:c++17 ItemPlugin.cpp /Fo:out\ /Fe:out\MCDRebornItems.dll /link /NOLOGO /DEF:exports.def user32.lib kernel32.lib
if errorlevel 1 exit /b %errorlevel%
copy /y out\MCDRebornItems.dll ..\MCDSaveEdit\Logic\PluginAssets\MCDRebornItems.dll >nul
exit /b %errorlevel%
