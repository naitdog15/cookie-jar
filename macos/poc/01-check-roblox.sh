#!/usr/bin/env bash
# Reports the installed Roblox app, the macOS version and whether the roblox:// scheme is registered.
# Read-only: changes nothing. Bash 3.2 compatible (the version macOS ships).
set -u

echo "== macOS =="
sw_vers 2>&1 || echo "sw_vers failed"
echo "architecture: $(uname -m 2>&1)"
echo

FOUND=0
for APP in "/Applications/Roblox.app" "$HOME/Applications/Roblox.app"; do
  echo "== $APP =="
  if [ ! -d "$APP" ]; then
    echo "not installed here"
    echo
    continue
  fi
  FOUND=1
  PLIST="$APP/Contents/Info.plist"

  SHORT=$(plutil -extract CFBundleShortVersionString raw -o - "$PLIST" 2>/dev/null) || \
    SHORT=$(defaults read "$APP/Contents/Info" CFBundleShortVersionString 2>/dev/null) || SHORT="(unreadable)"
  BUILD=$(plutil -extract CFBundleVersion raw -o - "$PLIST" 2>/dev/null) || \
    BUILD=$(defaults read "$APP/Contents/Info" CFBundleVersion 2>/dev/null) || BUILD="(unreadable)"
  EXE=$(plutil -extract CFBundleExecutable raw -o - "$PLIST" 2>/dev/null) || \
    EXE=$(defaults read "$APP/Contents/Info" CFBundleExecutable 2>/dev/null) || EXE="RobloxPlayer"

  echo "CFBundleShortVersionString: $SHORT"
  echo "CFBundleVersion: $BUILD"
  echo "CFBundleExecutable: $EXE"
  if [ -x "$APP/Contents/MacOS/$EXE" ]; then
    echo "player binary: $APP/Contents/MacOS/$EXE (present)"
  else
    echo "player binary: $APP/Contents/MacOS/$EXE (MISSING or not executable)"
  fi
  echo "ls Contents/MacOS:"
  ls -la "$APP/Contents/MacOS" 2>&1
  echo
done

if [ "$FOUND" -eq 0 ]; then
  echo "Roblox.app was not found in /Applications or ~/Applications."
  echo
fi

echo "== roblox:// URL scheme (best effort) =="
LSREG="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
if [ -x "$LSREG" ]; then
  MATCHES=$("$LSREG" -dump 2>/dev/null | grep -i roblox | head -n 20)
  if [ -n "$MATCHES" ]; then
    echo "$MATCHES"
  else
    echo "no roblox entries in lsregister -dump (the scheme may still work; this check is approximate)"
  fi
else
  echo "lsregister not found; cannot check the URL scheme"
fi
echo
echo "Done. Paste the full output back."
