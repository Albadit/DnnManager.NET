@echo off
rem Run by dockur/windows at the end of Windows setup (this folder is copied to C:\OEM).
powershell -NoProfile -ExecutionPolicy Bypass -File C:\OEM\provision.ps1 > C:\OEM\provision.log 2>&1
