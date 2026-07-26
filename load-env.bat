@if exist "%~dp0.env" (
  @for /f "usebackq tokens=*" %%i in ("%~dp0.env") do set %%i
)