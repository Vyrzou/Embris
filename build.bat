@echo off
rem Builds Embris.exe, the installer EmbrisSetup.exe, and EmbrisInstaller.zip to share with friends.
cd /d "%~dp0"
set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /win32icon:Embris.ico /resource:Embris.ico,Embris.ico /resource:embris-logo.png,embris-logo.png /r:System.Windows.Forms.dll /r:System.Drawing.dll
%CSC% /out:Embris.exe /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll" /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll" /r:System.Xaml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll Embris.cs Ui.cs Script.cs Backgrounds.cs Timeline.cs Editors.cs AutoClicker.cs ColorPicker.cs Overlay.cs Updater.cs || exit /b 1
echo Built Embris.exe
%CSC% /out:EmbrisSetup.exe /resource:Embris.exe,Embris.exe Setup.cs Ui.cs ColorPicker.cs || exit /b 1
echo Built EmbrisSetup.exe

if not exist EmbrisInstaller mkdir EmbrisInstaller
copy /y EmbrisSetup.exe EmbrisInstaller\EmbrisSetup.exe >nul
copy /y README-friends.txt "EmbrisInstaller\How to install.txt" >nul
powershell -NoProfile -Command "Compress-Archive -Path 'EmbrisInstaller' -DestinationPath 'EmbrisInstaller.zip' -Force" || exit /b 1
echo Built EmbrisInstaller.zip

rem Mac and Linux: Embris (Python + Qt) installers.
powershell -NoProfile -ExecutionPolicy Bypass -File crossplatform\package.ps1 || exit /b 1
