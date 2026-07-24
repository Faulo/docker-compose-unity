@echo off
for /f "usebackq tokens=*" %%i in ("%~dp0.env") do set %%i
