# MSGuide demo playbook: from task list to project-status report

Proposed business-user scenario · September 15, 2026 · For Uyi and Seyi to review

**Scope:** detailed playbook for the Excel scenario in [the four-scenario demo plan](DEMO_SCENARIOS.md). Excel is the business-reporting option alongside Visual Studio debugging, Azure diagnostics and project access requests. The plan recommends access plus Visual Studio for a varied short demo, subject to verified readiness; this remains an alternative single-story recording option.

**Readiness:** this is a scenario and implementation proposal, not a working Excel integration. No workbook was created, Excel was not tested, and no live model was called while writing this plan. The existing prototype's capture and foreground-overlay blockers remain open.

## 1. The story in one sentence

> “My project review starts soon. I have a list of tasks in Excel, but I don't know how to turn it into a clear status report. Show me what to do, here on my screen.”

The user starts with rows of work items and ends with a report showing **6 completed, 4 in progress and 2 blocked**, plus a chart. They make every selection and change themselves. MSGuide supplies the missing connection between a documented Excel procedure and the controls on their current screen.

### Why this is a better demo

| Element | What the audience understands |
| --- | --- |
| Relatable pressure | Someone needs to explain project progress in an upcoming meeting. |
| Recognizable application | Excel is familiar even to people who do not know PivotTables. |
| Genuine knowledge gap | The user knows the question they want answered, but not which Excel feature or settings to use. |
| Visible transformation | A raw task list becomes a small report and a chart, not just a chat answer. |
| Context matters | Guidance changes between worksheet, ribbon, dialog, field list and finished report. |
| Useful recovery | If Task ID lands in Rows rather than Values, MSGuide helps the user correct it instead of advancing blindly. |
| Safe scope | Only a disposable local workbook with invented data changes. Nothing is emailed, provisioned or shared. |

**The memorable moment:** “It doesn't just tell me to create a PivotTable—it helps me set it up correctly in the Excel window I'm actually using.”

### Positioning alongside Copilot

Do not claim that Copilot cannot create PivotTables. Microsoft explicitly documents Copilot assistance for that task with the appropriate licensing. The proposed MSGuide emphasis is **guided learning and user-controlled execution**, with source-backed instructions mapped to observed UI controls. Its broader cross-application ambition is a future direction, not something one Excel demo proves.

## 2. Persona, problem and scope

**Persona:** Maya, a fictional project coordinator preparing a status review. She understands the work items but has not built a PivotTable before.

**Starting situation:** a clean local workbook is open to a sheet named Tasks. It contains twelve invented work items. There is no report yet. Excel's standard ribbon is expanded, and no unrelated workbooks or sensitive windows are visible.

**Question to answer:** “How many tasks are in each status?”

**Primary success:** a PivotTable with Status in Rows and Count of Task ID in Values; grand total 12.

**Optional visual finish:** a clustered column PivotChart showing the three status counts.

**Not the question:** “What did my real team complete this week?” The fixture has no completion dates, so it describes a status snapshot, not work completed within a date range. A real weekly-completion report would need an explicit date interval and completion-date filtering.

**Exclude:** importing organizational data, querying Teams, publishing to SharePoint, sending a report, macros, workbook automation, resource changes and general-purpose control of arbitrary applications.

## 3. Demo fixture and exact expected result

Create a disposable local workbook during implementation. Use an ordinary range A1:D13 with one header row, no merged cells, no blank rows and no external connections. Do not prebuild the final report in the starting copy.

These are invented records, not employee or production data:

| Task ID | Work item | Workstream | Status |
| --- | --- | --- | --- |
| DEMO-001 | Draft project brief | Planning | Completed |
| DEMO-002 | Review requirements | Planning | Completed |
| DEMO-003 | Sketch landing page | Design | Completed |
| DEMO-004 | Review navigation | Design | Completed |
| DEMO-005 | Build sample screen | Engineering | Completed |
| DEMO-006 | Write demo checklist | Quality | Completed |
| DEMO-007 | Add help text | Design | In progress |
| DEMO-008 | Implement report view | Engineering | In progress |
| DEMO-009 | Test keyboard navigation | Quality | In progress |
| DEMO-010 | Prepare presentation | Planning | In progress |
| DEMO-011 | Resolve sample dependency | Engineering | Blocked |
| DEMO-012 | Review sample wording | Quality | Blocked |

Expected report, independent of row order:

| Status | Count of Task ID |
| --- | --- |
| Blocked | 2 |
| Completed | 6 |
| In progress | 4 |
| Grand Total | 12 |

**Reviewer check:** count the fixture independently and compare it with the report. Do not show these expected totals as an MSGuide observation unless they were actually available in the approved screen evidence. A chart existing is not proof its underlying report is correct.

Keep a pristine starting copy for resets. Work on a disposable duplicate; do not overwrite a personal workbook. Store recording assets only after reviewing them for sensitive content.

## 4. Full screen-by-screen walkthrough

This is the full rehearsal, not a promise that all steps fit unedited into two minutes. The quoted MSGuide responses below are **proposed acceptance examples**, not outputs from the current build. Ribbon labels and menu layouts must be confirmed on the chosen Excel version and language.

### Scene A — Ask for the outcome, not the feature

**User says or types:** “Help me turn this task list into a report showing how many tasks are completed, in progress or blocked.”

1. Invoke MSGuide with Ctrl+Alt+M or bring its window forward.
2. Select the Excel workbook window.
3. Choose Capture / review. Inspect the image and the UI Automation text/boxes.
4. Leave screenshot upload off unless an approved provider and pixel sharing are explicitly part of this test.
5. Approve the snapshot and choose Send approved snapshot.

**Proposed response:** “A PivotTable can count tasks by status. First, select a cell inside the task list, then open Insert.”

**What MSGuide must know:** the approved context identifies the worksheet and relevant controls. It must not assert that all twelve records are readable just because an Excel window is visible.

**Visual guidance:** point to Insert only if its observed accessible element has a valid box. A cell-selection instruction may be text-only if cell coordinates cannot be grounded.

**Narrator:** “She knows the report she needs. She doesn't need to know the name of the Excel feature.”

### Scene B — Find the right control in this application

The user selects a cell in the list and opens Insert. They choose Check next step, review the fresh snapshot and approve it.

**Proposed response:** “Select PivotTable to summarize this task list.”

**Visual guidance:** highlight the observed PivotTable control, not a remembered screen coordinate. If Excel presents a source submenu, guide From Table/Range only after observing it.

The user clicks. The creation dialog appears.

**Important engineering gate:** dialogs and menus may have separate window handles. A capture of the workbook does not automatically authorize or include another window. If necessary, the user selects that dialog and explicitly approves a new capture. If that sequence cannot work reliably, simplify or reject the scenario rather than expanding capture silently.

### Scene C — Avoid creating the report from the wrong data

After fresh review and approval of the dialog:

**Proposed response:** “Check that the source includes all four headers and twelve tasks. For this sample, that is Tasks!A1:D13. Choose New Worksheet, then OK.”

This is a human check against the known fixture. MSGuide may say it has verified the range only if the range value is present in readable approved evidence.

For live operation, split the instruction into individual observed targets if needed: source check → New Worksheet → OK. Never outline multiple future controls as though they were already observed.

**Visible result:** a new worksheet with an empty PivotTable and its field list.

### Scene D — Turn records into the right answer

The user captures and approves the new screen.

**Proposed response:** “Place Status in Rows. That creates one group for each status.”

After the user does that and approves another observation:

**Proposed response:** “Place Task ID in Values, then confirm the calculation is Count. That counts tasks rather than adding identifiers.”

Use the field menu's placement commands if they are available and accessible on the tested Excel version; otherwise guide the user's manual drag. The prototype supports one target per response, so a drag may require a source instruction followed by a destination instruction. Do not promise a two-endpoint animated drag overlay.

**Why this matters:** checking a text field can put it in Rows by default. The guide should explain the desired arrangement rather than assume that checking two boxes produces the correct report.

**Visible result:** three status groups with counts. The user checks the numbers against the sample. If Count is not selected, guide Value Field Settings and Count using fresh observed controls.

### Scene E — The optional recovery moment

During a dedicated rehearsal, deliberately place Task ID in Rows instead of Values. This is a harmless change in a disposable workbook.

**User prompt:** “I see every task listed. I wanted totals by status.”

**Proposed response, only if field placement is readable:** “Task ID is grouped under Rows. Move it to Values and use Count so the report totals tasks by status.”

**Required alternative when evidence is insufficient:** “I can't verify the field placement from this view. Open the field list and share a fresh snapshot.”

The correction should be driven by the newly observed field layout, not an unconditional canned answer to that prompt. Test both correct and incorrect layouts with the same prompt. It should not advise moving a field that is already correctly placed.

**Recording choice:** include this short branch instead of the chart if it communicates adaptive guidance more clearly. Do not add both at the expense of showing the consent/recheck loop.

### Scene F — End with an artifact

Once the report is correct, the user asks: “Help me make this easier to present.”

**Proposed response:** “Select a cell in the PivotTable, then choose Insert → PivotChart.”

After the user opens the chart chooser and approves its context, guide a clustered column chart and OK, one step at a time as needed.

**User-visible finish:** a chart of the three status counts alongside the summary table. A chart title such as “Project task status — demo data” is optional manual polish, not required for core acceptance.

**Safe closing response:** “The report and chart are visible. Check the three counts before using them in your review.”

Do not claim that the report was shared, that tasks were completed during a particular week, or that the project is healthy. If the report values cannot be read reliably, the presenter—not MSGuide—verifies them.

## 5. Microsoft documentation grounding

These public Microsoft Support pages were consulted on September 15, 2026. Their procedural guidance supports the proposed flow; their availability does not mean retrieval is integrated into MSGuide.

| Source | What it supports | Intended in-demo use |
| --- | --- | --- |
| [Create a PivotTable to analyze worksheet data](https://support.microsoft.com/en-us/office/create-a-pivottable-to-analyze-worksheet-data-a9a84538-bfe9-40a9-a8e9-f99134456576) | Tabular data, Insert → PivotTable, report placement, field arrangement and Count versus Sum. | Cite the relevant section when introducing PivotTables or arranging fields. |
| [Create a PivotChart](https://support.microsoft.com/en-us/office/create-a-pivotchart-c1b1e057-6990-4c38-b52b-8255538e7b1c) | Creating a chart from a PivotTable. | Cite when proposing the chart step. |

For a narrow hackathon implementation, use a small reviewed set of procedural summaries tied to these exact sources, rather than building enterprise search. Keep summaries distinct from screen observations: the document explains what the feature does; the observation establishes which control is present now.

The desktop already has a citation display and HTTPS-link validation. The missing work is connecting trusted source selection to the desktop guidance path. Existing mock retrieval uses placeholder URLs, and the optional model adapter currently does not return citations. Do not manufacture a citation or let a model-supplied URL bypass source review.

## 6. What must be built or verified

This is not a request to rewrite the application or install integrations now. It identifies the smallest work needed if the team selects this scenario.

| Work item | Current starting point | Acceptance criterion |
| --- | --- | --- |
| Excel feasibility probe — first | Selected-window PrintWindow capture and UI Automation exist; reliability remains blocked. | On the actual recording machine, capture workbook, ribbon, field pane and relevant dialogs legibly; locate needed targets without hard-coded boxes. |
| Disposable sample workbook | Fixture specified above; workbook not created. | Clean starting copy and a reviewed expected report; no macros, connections or real data. |
| Narrow Excel guide | Default provider recognizes only MSGuide Demo. | Recognize supported observed Excel states; return the correct next step or clarification. Unknown layouts must not advance blindly. |
| Approved source attachment | Citation UI exists; desktop guidance has no real document retrieval. | Each source-backed step has a reviewed source title and valid HTTPS link. |
| Observation coverage | UIA currently collects names/types/boxes, not a complete Excel object model. | Determine whether field placement, range text and totals are readable. If not, state the limit and use human checks; do not pretend to verify values. |
| Dialog and menu handling | One selected window and strict identity/freshness checks. | Explicit selection/approval works for each required surface without widening capture boundaries. |
| Correction branch | Not implemented for Excel. | Same question on correct versus incorrect field layouts produces evidence-appropriate guidance. |
| Model mode, only if selected | Optional transport tested with mocks; no approved live endpoint. | Configure an explicitly approved provider securely and evaluate its actual outputs before claiming AI-driven guidance. |
| Rehearsal evidence | Existing backend and desktop checks do not prove Excel functionality. | Three consecutive unedited successful runs of the chosen scope, plus negative-case checks. |

Relevant implementation areas: [capture](../desktop/CaptureService.cs), [desktop UI](../desktop/MainWindow.xaml), [client contracts](../desktop/Contracts.cs), [current deterministic guide](../src/guidance.py), and [API contract](API.md).

### Feasibility decision before expanding scope

Time-box an initial interactive Excel probe to approximately 60–90 minutes as a planning choice, not an estimate of the complete implementation.

- **Proceed:** capture is reliable and the core worksheet/ribbon/dialog/field-list targets are observable.
- **Reduce scope:** create the status PivotTable only; omit chart and recovery branch if their extra surfaces are unreliable. Keep at least one genuine re-observation that changes guidance.
- **Stop this candidate:** core capture or target grounding cannot be made reliable within the available hackathon time. Use the existing synthetic build walkthrough only if it passes its own gates, or label the deliverable a concept demonstration.
- **Do not fake compatibility:** an Excel-like mock can explain the concept but is not footage of a working Excel integration.

## 7. Failure paths worth testing

| Situation | Correct behavior | Presenter response |
| --- | --- | --- |
| Snapshot not approved | No guidance submission from that snapshot. | Show approval once; do not describe capture alone as consent. |
| Blank/protected capture | Reject it and show no target based on it. | Stop the take; do not disable the blank-image guard. |
| Wrong workbook selected | Do not claim it contains the expected fixture. | Discard, select the intended workbook and approve again. |
| Menu or dialog not in selected capture | Do not infer hidden controls. | Select the appropriate surface and reapprove, if supported. |
| Expired snapshot or moved window | Old target is invalidated; require fresh context. | Use Check next step and review again. |
| Task ID is in the wrong area | Correct only when the placement is visible and interpretable. | Show the recovery branch if it was verified. |
| Correct placement with the same complaint | Do not move an already-correct field; ask what result differs. | Demonstrates state-aware guidance rather than fixed sequencing. |
| Missing or duplicate target labels | No guessed outline; clarification. | Reveal the relevant pane or control and recapture. |
| Source data range is incomplete | Ask the user to check the range; claim detection only if readable. | Correct the source before continuing. |
| Provider failure or timeout | No fabricated answer or silent substitution of scripted output. | Retry explicitly after fresh review, or end the take. |
| User chooses Pause / clear | Clear guidance/context and stop active speech as designed. | Explain that the user remains in control. |

## 8. Two-minute video: one story, not a feature tour

Target **1:55**. The full task will likely take longer because each new step requires capture review and approval. Use labeled cuts between verified stages; do not advertise the edited runtime as task completion time.

| Time | Shot | Narration / point |
| --- | --- | --- |
| 0:00–0:12 | Raw task list and a title card: “Project review coming up.” | “You have the data. You know the question. But you don't know the Excel steps to get the answer.” |
| 0:12–0:27 | Enter the goal in MSGuide, select Excel, inspect and approve the snapshot. | “MSGuide uses the window context you approve to help you do the task yourself.” |
| 0:27–0:47 | Highlight Insert/PivotTable; user clicks; source title is visible if integrated. | “Instead of sending you away to read a tutorial, it connects the documented step to your current screen.” |
| 0:47–1:12 | Labeled later-step cut; configure Status and Count of Task ID. Include a visible fresh approval. | “After you act, it checks a fresh view and guides the next step.” |
| 1:12–1:35 | Choose one: correct a misplaced field OR add the chart. | Recovery: “It helps you correct the setup.” Chart: “Now turn the summary into something you can present.” |
| 1:35–1:45 | Hold on the verified report: 6 completed, 4 in progress, 2 blocked. | “A task list is now a clear project-status report.” |
| 1:45–1:55 | Pause / clear and final product card. | “MSGuide: from knowing what you want to knowing what to do next. Guidance, with you in control.” |

Show an accurate persistent mode label: **Scripted Excel prototype** if using a narrow deterministic guide, or **Model-guided prototype** only after that path is implemented and tested. Label the data synthetic in either case. Do not put a recorded overlay on top of footage and present it as live output.

### Recording preparation

1. Confirm the submission deadline, time zone and maximum video duration on the actual submission page. The meeting notes give September 21, 2026 and two minutes; those requirements have not been independently checked.
2. Use an unlocked desktop, one tested display/scaling configuration and the chosen Excel version/language.
3. Close sensitive content and suppress unrelated notifications without changing organizational security controls.
4. Open a fresh duplicate of the sample workbook; start with no finished report.
5. Use typed input unless microphone behavior has passed a separate rehearsal.
6. Rehearse three complete runs before recording. Record failures too in the test notes; do not select one lucky pass as reliability evidence.
7. Retain an unedited verification take. The submission can use an edited version with transparent cuts.
8. Review the finished video for readable text, correct numbers, accurate mode labels and no credentials or private information.

## 9. Priorities, ownership and stop rules

Suggested ownership for discussion, not an assignment already agreed by the team:

| Priority | Deliverable | Suggested lead | Stop rule |
| --- | --- | --- | --- |
| P0 | Excel capture/target feasibility and existing blocker triage | Uyi | Do not build a long workflow on an unreadable surface. |
| P0 | Story, fixture and expected result review | Seyi | Reject anything that needs tenant data to be compelling. |
| P1 | PivotTable-only guide, approved source mapping and consent loop | Uyi | Freeze to one Excel version and one fixture shape for the prototype. |
| P1 | Independent correctness and failure-path rehearsal | Both | Three consecutive passes before declaring it recordable. |
| P2 | Chart OR wrong-field correction | Both | Add one only after the core flow passes. |
| P2 | Recording, narration and claim review | Seyi with Uyi | Stay below two minutes and keep disclosures readable. |

Wednesday, September 16: choose the scenario and complete the feasibility decision. Thursday, September 17: implement only the selected scope and rehearse. Friday, September 18: record and submit if testing passes. Keep remaining time before the stated September 21 deadline as buffer, not permission to add more integrations.

## 10. How to judge whether the demo worked

### Functional acceptance

- Correct fixture source range, all twelve records counted once.
- Status groups are 6 Completed, 4 In progress and 2 Blocked; total 12.
- Every displayed target corresponds to an actually observed control.
- At least one changed screen yields an appropriate changed instruction.
- No input injection, automatic workbook edits, sends or external writes.
- Source attribution refers to the reviewed procedure, not an invented runbook.
- Failure and uncertainty are visible rather than hidden behind a success message.

### Audience acceptance

Ask two or three reviewers who have not built MSGuide:

1. What problem was the person trying to solve?
2. What did MSGuide contribute beyond giving a text answer?
3. Who clicked the buttons and changed the workbook?
4. What information did the user approve sharing?
5. What was real, scripted or still planned?

If reviewers answer “it just made a chart automatically,” revise the footage or narration. The intended takeaway is guided execution and learning, not autonomous spreadsheet generation.

Record elapsed time, wrong turns and clarification prompts during rehearsals if useful. These are local test observations, not evidence of general productivity gains. Do not invent a percentage improvement.

## 11. Relationship to the other three scenarios

- **Visual Studio debugging:** an engineer uses breakpoints and variable inspection to understand a reproducible local exception; any repair requires a separate verified rerun.
- **Azure diagnostics:** an engineer finds read-only evidence for a test app's failure and identifies the next check, without changing or restarting the service.
- **Project access requests:** a new member follows a reviewed access route and prepares a correctly scoped request. A pending request does not mean access has been granted.

See [the four-scenario plan](DEMO_SCENARIOS.md) for their detailed flows and the combined 1:55 overview cut. This Excel playbook remains the deeper single-story recording option. Select implementation and recording scope based on verified readiness, not the number of proposals.
