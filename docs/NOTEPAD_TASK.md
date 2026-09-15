# Experimental Notepad dual-mode task

## Scope and release gate

Implemented September 15, 2026: one approved synthetic draft, at most 1000 characters, into an **empty Microsoft Notepad editor**. Guide me shows the target and verifies text you enter. Do it for me performs one handle-scoped text insertion and verifies the result. This is a local typed adapter, not a model-generated action plan or general computer-use engine.

**Native Notepad acceptance is pending.** External writes are disabled unless the desktop process inherits `MSGUIDE_ENABLE_EXPERIMENTAL_NOTEPAD_CONTROL=1`. This is an explicit synthetic-test opt-in, not production readiness. Guide mode does not need the flag, but its native editor/outline behavior also needs interactive validation.

There is no Save, file-open, send, clipboard, global mouse or keyboard command. **Notepad may persist unsaved tabs/session backups itself.** “No Save command” does not mean “nothing reaches disk.” Do not use sensitive content.

## Try it manually

1. Open a separate Microsoft Notepad window with exactly one new blank tab. Dismiss first-run dialogs. Do not use an existing document or a multi-tab window.
2. Open MSGuide. Set the experimental environment variable to `1` in the launching environment only if testing Control mode; remove it afterwards.
3. Use **Refresh windows**, then select that exact Notepad window in the chooser. Arrange both windows before preparing; moving/resizing either target after preparation can invalidate the task.
4. Expand **Notepad · approved draft**, edit the exact synthetic text, and choose **Guide me** or **Do it for me**.
5. Choose **Prepare selected Notepad task**. Review the target, exact text, permissions and retention warning. Check consent, then **Start approved task** within 60 seconds of preparation.
6. **Guide:** switch to Notepad, type the draft, return to MSGuide and choose **I did it · check**. Partial text keeps the task incomplete. Line-ending representation is normalized for comparison.
7. **Control:** keep MSGuide foreground and do not touch the target while the single insertion runs. Only exact text verification produces success. A failed/unknown outcome revokes authority and is never retried.
8. **Stop task**, **Take over manually**, mode/draft/prompt/selection changes, Pause, Escape, minimize and close revoke authority. Pause also clears the draft and displayed plan. Stop does not undo completed work or recall an already-dispatched native request.

## Implementation boundaries

- Supported processes must resolve to the Windows System32 Notepad executable or the Microsoft WindowsNotepad package under the protected WindowsApps directory. This is a path allowlist, not enterprise attestation.
- Exact PID/start time, HWND, editor HWND/class, visibility, enabled/read-only/password state and original window bounds are revalidated.
- Packaged Notepad additionally requires exactly one selected UIA tab with a stable runtime ID. Unknown UIA implementations and multi-tab windows fail closed. A single outstanding bounded tab-check worker is permitted globally; a hung provider is not retried.
- Preparation reads only editor length, not existing document text. Nonempty editors are refused. Approved verification reads at most 1000 characters locally; nothing is sent to the backend or model.
- Native `WM_GETTEXTLENGTH`, `WM_GETTEXT` and `WM_SETTEXT` use `SendMessageTimeout` with 500 ms limits and Windows system-message marshalling. No custom cross-process pointer messages are used. The window-message result is separate from the API delivery result.
- Native calls and synchronous validation can briefly delay the WPF Stop handler. Stop prevents subsequent requests, not an in-flight operation. Timeout means unknown outcome; inspect manually.
- Blank checks and cross-process writes are **not an atomic compare-and-set**. The companion must remain foreground; do not edit the target or run another writer during insertion. This residual race and unverified modern Notepad behavior are reasons for the experimental gate. This adapter is not safe for valuable documents.
- The 200 ms task timer checks native identity and tab metadata, not pixels or editor text. Guide reads occur only at approved Start/Check; Control reads occur for insertion/result verification.

## Verified in this increment

| Check | Result |
| --- | --- |
| .NET desktop build | Passed |
| Existing desktop safety tests | Passed |
| Notepad policy/component tests | Passed with fake editor; not native acceptance |
| Real companion stop/takeover/consent/mode/prompt/draft/Pause handlers | Passed with fake Notepad editor |
| Existing demo Control component suite | Passed; simulated focus/time remains explicit |
| Native Notepad text insertion | Not run: user unavailable to provide the native test click |
| Notepad Guide highlight/text-check UX | Pending |
| Native Notepad foreground Control acceptance | Pending user interaction |

Notepad component coverage includes unapproved actions, one-shot execution, exact text verification, refusal to read/overwrite existing content, content changes after preparation/approval, Guide with partial/completed text and no writes, stop, expiry, unavailable scope, reapproval, oversized text, mismatch, timeout/no retry, invalid drafts and executable rejection.

## Native diagnostic findings

**Latest update:** strict owned-demo Control passed three two-action runs with real foreground checks. The four-state capture/API test passed (18 checks). A DPI-triggered overlay activation bug was fixed and full native integration passed three post-fix runs, the final two with stronger zero-activation and hide/re-show checks. This does not establish real Notepad acceptance. See [latest validation evidence](VALIDATION.md).

The current Windows session was active RDP with an accessible `Default` input desktop and a nonzero foreground HWND. A background-launched owned-window control test still failed foreground activation. This is consistent with Windows foreground restrictions; it does not establish the root cause or prove normal user-clicked operation is broken.

The control harness now allows 30 seconds for the user to activate its clearly labelled host window. It never injects a click, steals focus or substitutes simulated focus in native mode.

Two `--native-diagnostic` runs compared only owned synthetic-window captures:

| Trial | Default rendering | Software rendering |
| --- | --- | --- |
| 1 | Capture and View logs target passed | Blank capture rejected |
| 2 | Capture and View logs target passed | Capture and View logs target passed |

The diagnostic report's `passed` field means the diagnostic completed; inspect its individual checks for capture outcomes. This is **not** interactive acceptance. No software-rendering change, blank-image bypass or desktop-capture fallback was applied. Capture remains intermittent; root cause unresolved.

The installed Notepad package was `11.2607.15.0`. A launch probe showed that `/new` was interpreted as a filename, not a new-window option. Its owned missing-file dialog was dismissed and that test window closed; no draft was inserted. The production UI does not use this argument or launch Notepad automatically.

## Test entry points and remaining acceptance

The desktop executable supports `--self-test`, `--control-component-test`, `--control-test`, `--native-diagnostic`, and `--notepad-test --notepad-hwnd <decimal HWND>`, with `--test-results <path>`. Run one mode at a time. The Notepad native test requires both experimental opt-in and an actual user click on its disclosure window; it addresses only the explicitly supplied Notepad handle, not a title search. It leaves verified synthetic text for manual inspection and never closes or saves that target.

Before enabling external writes by default:

- [ ] Complete both modes on a blank, single-tab Notepad window on the installed version.
- [ ] Confirm visual outline alignment and normalized multiline text verification.
- [ ] Verify native rejection of nonempty/read-only editors, a second tab, tab replacement, target movement/closure and focus loss.
- [ ] Exercise real Stop/takeover during the pre-action interval and document responsiveness.
- [ ] Validate no-retry behavior on hung/rejected native operations; inspect late outcomes manually.
- [ ] Rehearse the full demo/API/overlay path on an unlocked desktop and investigate intermittent blank capture.

Model-generated actions, editing existing documents, saving, Excel, Visual Studio and access requests remain out of scope.

Reference: [Microsoft SendMessageTimeout documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw).
