# Proof-of-concept scripts (run on a Mac)

These scripts answer one question before the port goes further: **can several Roblox clients run side by side on a Mac?**
They change nothing on your Mac except one temporary screenshot in the temp folder (`03`) and the two Roblox clients they open (`02`).

## Before you start

1. Install Roblox (`/Applications/Roblox.app` or `~/Applications/Roblox.app`) and sign in.
2. Open the Terminal app. The permissions in step 3 belong to the app that runs the scripts (Terminal, iTerm, ...).
3. Make the scripts runnable (once):

   ```sh
   cd path/to/macos/poc
   chmod +x 01-check-roblox.sh 02-two-clients.sh 03-permissions.sh
   ```

## What to run, in order

| Step | Script | Time | What it does |
|---|---|---|---|
| 1 | `./01-check-roblox.sh` | under 5 s | Roblox version, player binary, macOS version, CPU type, roblox:// scheme (best effort). |
| 2 | `./03-permissions.sh` | under 10 s | Accessibility, Screen Recording and Automation state. Tells you which System Settings pane to open. |
| 3 | `./02-two-clients.sh "<URL 1>" "<URL 2>"` | about 70 s | Opens two Roblox clients and checks both stay alive for 60 s. |

Run step 3 last. Before it, get a **fresh pair** of launch URLs: they contain one-time auth tickets that expire quickly.
Run `./02-two-clients.sh` with no arguments to see the usage text.

If a permission is missing in step 2, grant it in **System Settings > Privacy & Security**, quit and reopen the Terminal app, and run step 2 again.

## What to send back

Paste these four things in the chat:

1. **The full terminal output of `01-check-roblox.sh`**, copied in one go.
2. **The full terminal output of `03-permissions.sh`**, copied in one go.
3. **The full terminal output of `02-two-clients.sh`**, copied in one go. It must include the `PASS` or `FAIL` line `both clients alive for 60s: yes/no`.
4. **A screenshot of Activity Monitor with two Roblox rows** (the `RobloxPlayer` processes), taken while the clients are still open, after the script has finished. To find the rows, open Activity Monitor and search for `Roblox` in the search box.

Do not trim the output and do not paste only the PASS/FAIL line: the full output shows the process counts over time.

## Closing the clients

`02-two-clients.sh` never closes anything. When you have finished, quit each client yourself:

- Click the Roblox window and choose **Roblox > Quit Roblox** (or press Cmd-Q).
- Or open Activity Monitor, select the `RobloxPlayer` row, and click the **X** toolbar button.
- Use **Force Quit** only if a client does not respond.

## Notes

- Scripts are Bash 3.2 compatible, the version macOS ships. Each one can be checked on Windows with `bash -n <script>` (syntax only).
- `01` reads `lsregister -dump` for the roblox:// scheme. That check is approximate; a missing entry does not prove the scheme is broken.
- `03` reports Screen Recording as "a file was written". A denied grant still writes a file, but it shows only the wallpaper, so open the screenshot in Preview and check that your windows are in it.
- MAC-UNVERIFIED: none of these scripts has been run on a Mac yet. Expected behaviour is written into the scripts' comments.
