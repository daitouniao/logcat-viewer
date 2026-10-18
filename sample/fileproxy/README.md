# File Proxy Samples (fileproxy)

This directory contains two ready-to-run proxy samples for the logcat "File Proxy" feature, plus the full configuration guide for the app side.

When a file cannot be opened directly (unsupported path forms, NTFS permissions tightened, sharing violation from the writer process, EDR/group-policy interception, or the content turns out not to be text), an external "file proxy" process can read it on the app's behalf: the proxy obtains the file bytes by any means and hands them to the app over a Windows named pipe, following the protocol below. The app first spools the byte stream to a local temporary file, then runs its normal indexing pipeline, while the UI shows the original path throughout.

## Contents

| File | Description |
| ---- | ----------- |
| `fileproxy.py` | Python 3.8+ sample, zero third-party dependencies (standard library only) |
| `fileproxy.js` | Node.js sample, zero third-party dependencies |

Both scripts implement the identical protocol v1. Pick either one, or use them as a skeleton for your own proxy.

## Option 1: Start the proxy manually

1. Start the script first (default pipe name is `logcat-fileproxy`; keep it consistent with the app settings):

   ```text
   python <path-to-script>\fileproxy.py
   node  <path-to-script>\fileproxy.js
   ```

2. Open the target file in the app. In "On" or "Auto" mode, when the proxy is needed the app connects to the already-running proxy.

## Option 2: Configure a launch command (auto-start)

Open the settings dialog via the menu "Settings → File Proxy…":

| Setting | Description |
| ------- | ----------- |
| Mode | Three choices, default "Off". **Off**: the proxy path is never taken; behavior is identical to a build without this feature. **On**: every file open goes through the proxy. **Auto**: the proxy is used only when the direct open fails, or the open succeeds but the text probe decides the content is not text; otherwise the file is read directly as usual |
| Pipe Name | Default `logcat-fileproxy`. The app automatically appends `--pipe <this value>` when launching the proxy — no need to type it in the arguments. Validated on save (non-empty, no backslash, length limit) |
| Executable | **Full path** to the interpreter or program (no file association), e.g. the path of `python.exe` or `node.exe` |
| Arguments | Extra arguments passed to the executable (e.g. `--root D:\data`). `--pipe <name>` is appended automatically at the end of the launch command; wrap the script path in quotes if it contains spaces. A hand-written `--pipe {pipe}` from older configs still works (treated as a placeholder) and is stripped automatically when the dialog loads |

Example arguments (replace `<path-to-script>` with where you placed this directory; `--pipe <name>` is appended by the app automatically — do not type it):

```text
# Python
"<path-to-script>\fileproxy.py"

# Node.js
"<path-to-script>\fileproxy.js"
```

| Setting | Description |
| ------- | ----------- |
| Launch timeout (ms) | How long to wait for the pipe to become ready after auto-launch, default 5000 ms |
| Test Connection | Connects and performs the handshake using the current field values (no save required); if the proxy is not running it is launched automatically via the configured command and the launched process stays resident for reuse |

Notes:

- Auto-launch happens only when the **manual proxy cannot be reached** and a **launch command is configured**; the launched process stays resident for reuse and is terminated when the app exits.
- Settings take effect immediately after saving; no restart needed.
- A launch command is equivalent to executing arbitrary code with the app's privileges. **Never configure untrusted scripts** (see "Security Notes" below).

## Script command-line arguments

Common arguments for both scripts:

| Argument | Description |
| -------- | ----------- |
| `--pipe <name>` | Pipe name, default `logcat-fileproxy`; must match the pipe name in the app's File Proxy settings |
| `--root <dir>` | Only allow reading files under this directory; all other paths are rejected (demonstrates a minimal security boundary; unrestricted if omitted) |

Script-specific arguments:

| Script | Argument | Description |
| ------ | -------- | ----------- |
| `fileproxy.py` | `--once` | Exit after serving one client connection (handy for testing / one-shot use) |
| `fileproxy.js` | `--idle <sec>` | Auto-exit after this many seconds without a client; `0` = never (default) |

## Protocol v1 summary

A custom proxy only needs to implement four operations. Transport is a Windows named pipe in byte mode (`\\.\pipe\<pipe name>`):

- Control messages = single-line JSON + `\n` (UTF-8, 64KB per-line limit)
- `hello` → `{"ok":true,"proxy":"<self description>","proto":1}`; the app treats the connection as failed if `proto` is missing or ≠ 1
- `open` + file path → `{"ok":true,"h":<handle>,"size":<byte count>}`
- `read` + `{h, off, len}` → `{"ok":true,"n":<byte count>}` header line + exactly n raw payload bytes immediately after (no separator); `n=0` means EOF; `len` is capped at 16MB
- `close` + `{h}` → `{"ok":true}`
- Unknown ops / malformed JSON / bad handles reply `{"ok":false,"err":"…"}` and **must not close the connection** (the connection is reused)

## App-side behavior and limits

- Files read via the proxy are first spooled in full to the `temp` subfolder next to the app executable, then indexed; the title bar, recent-files list, and export default name all show the original path; the spool folder is deleted when the document closes.
- Files larger than 1GB trigger a confirmation prompt first (full-copy spooling can fill the disk).
- "Reload" on a proxied document re-pulls the whole file.
- The app runs single-instance; opening multiple windows at the same time is not supported.

## Security notes

- Configuring a launch command is equivalent to executing arbitrary code with the app's own privileges. **Never configure untrusted scripts.**
- The named pipe is secured by same-session local ACL, and the protocol carries no authentication messages. The samples' `--root` argument demonstrates a minimal read-scope restriction; tighten the read scope yourself in a production proxy.
- The app does not verify the provenance of proxied content; the proxy self-description (the `proxy` field of the `hello` response) is used for error messages only.
