#!/bin/sh
# Runs the self-checking test app with a throwaway calibration file.
set -e
cd "$(dirname "$0")"
# Own calibration file and port, so a running OpenTabletDriver is never touched.
export OTD_PENCALIBRATION_FILE="${TMPDIR:-/tmp}/pencal-test-$$.json"
export OTD_PENCALIBRATION_PORT=47863
dotnet run --project Tests -c Release
