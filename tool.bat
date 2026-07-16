#!/bin/sh
: '
@echo off
REM Windows part
where py >nul 2>nul
if %errorlevel% neq 0 (
    echo --------
    echo Error: Python is not installed.
    echo Please install Python from https://www.python.org/downloads/
    echo Default settings are fine; "py launcher" must be checked if you use custom settings
    exit /b 1
)
py -3 "%~dp0\tools\lib\bootstrap.py" %*
set BUCK_EXIT=%errorlevel%
REM The pause is a double-click convenience for interactive failures; CI (which defines CI=true) must not wait on a keypress. The errorlevel capture is defensive hardening, not a bugfix: pause and bare exit /b both preserve errorlevel, but an explicit BUCK_EXIT survives future edits inserting commands that do clobber it.
if %BUCK_EXIT% neq 0 if not defined CI pause
exit /b %BUCK_EXIT%
'
# Shell script part
if ! command -v python3 >/dev/null 2>&1; then
    echo "--------"
    echo "Error: Python 3 is not installed or not in PATH."
    echo "Please install Python 3:"
    echo "  Ubuntu/Debian: sudo apt install python3"
    echo "  Arch: sudo pacman -S python"
    exit 1
fi
exec /usr/bin/env python3 "$(dirname "$0")/tools/lib/bootstrap.py" "$@"
