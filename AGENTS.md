# Cookie Jar: instructions for Codex and all coding agents

Read [the shared release policy](docs/RELEASE_POLICY.md) before changing, building or committing application code. It applies equally to Codex and Claude, on every branch and platform.

- Windows work belongs on `main`; macOS work belongs on `macos-port`. Check the branch before editing. Do not merge unrelated platform work.
- Follow the version-bump, source patch-notes, build validation and commit requirements in that policy for every application update. Do not bypass the release check.
- Windows source targets .NET Framework and C# 5. Preserve that compatibility.
- Do not run tests that move the real mouse or send keys without user permission.
