#!/usr/bin/env bash
# Reports the macOS permissions Cookie Jar needs: Accessibility, Screen Recording and Automation.
# Read-only apart from one temporary screenshot. Bash 3.2 compatible (the version macOS ships).
# The permissions belong to the app that runs this script (Terminal, iTerm, ...), not to Roblox.
set -u

echo "== macOS =="
sw_vers 2>&1 || echo "sw_vers failed"
echo

echo "== Accessibility =="
AX_OUT=$(osascript -e 'tell application "System Events" to get UI elements enabled' 2>&1)
AX_CODE=$?
echo "osascript exit $AX_CODE: $AX_OUT"
case "$AX_OUT" in
  *-1719*|*-25211*) echo "RESULT: DENIED (Accessibility is not granted to this app)" ;;
  *)
    # The query prints true or false on success. false means UI scripting is not allowed,
    # even though no error is raised.
    if [ "$AX_CODE" -eq 0 ] && [ "$AX_OUT" = "true" ]; then
      echo "RESULT: GRANTED (UI scripting answered true)"
    elif [ "$AX_CODE" -eq 0 ] && [ "$AX_OUT" = "false" ]; then
      echo "RESULT: DENIED (UI scripting is disabled for this app, value: false)"
    else
      echo "RESULT: UNKNOWN (unexpected error, see above)"
    fi
    ;;
esac
echo

echo "== Screen Recording =="
SHOT="${TMPDIR:-/tmp}/cookiejar-permission-check-$$.png"
screencapture -x "$SHOT" 2>&1
SHOT_CODE=$?
if [ "$SHOT_CODE" -eq 0 ] && [ -s "$SHOT" ]; then
  SIZE=$(wc -c < "$SHOT" | tr -d ' ')
  echo "screencapture wrote $SIZE bytes to: $SHOT"
  echo "RESULT: a file was written. Open it in Preview and check that your app windows are in it."
  echo "WARNING: without Screen Recording granted, screencapture still writes a file, but it shows only"
  echo "         the wallpaper and no windows. A file of any size is not proof that the permission works."
else
  echo "screencapture failed (exit $SHOT_CODE) or wrote nothing"
  echo "RESULT: likely DENIED or the tool is unavailable"
fi
echo

echo "== Automation (System Events) =="
AUTO_OUT=$(osascript -e 'tell application "System Events" to get name of every process' 2>&1)
AUTO_CODE=$?
echo "osascript exit $AUTO_CODE: $AUTO_OUT"
case "$AUTO_OUT" in
  *-1743*) echo "RESULT: DENIED (this app is not allowed to control System Events)" ;;
  *)
    if [ "$AUTO_CODE" -eq 0 ]; then
      echo "RESULT: GRANTED (System Events answered)"
    else
      echo "RESULT: UNKNOWN (unexpected error, see above)"
    fi
    ;;
esac
echo

echo "== Where to grant what (System Settings > Privacy & Security) =="
echo "  Accessibility:     Privacy & Security > Accessibility, turn on the app that runs this script"
echo "  Screen Recording:  Privacy & Security > Screen & System Audio Recording (older macOS: Screen Recording)"
echo "                     turn on the app that runs this script, then quit and reopen that app"
echo "  Automation:        Privacy & Security > Automation, expand the app that runs this script,"
echo "                     turn on System Events"
echo
echo "Done. Paste the full output back."
