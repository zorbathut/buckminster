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
if %errorlevel% neq 0 pause
exit /b
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
