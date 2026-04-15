@echo OFF
:: in case DelayedExpansion is on and a path contains ! 
setlocal DISABLEDELAYEDEXPANSION
composer -d "C:\unity" %*
