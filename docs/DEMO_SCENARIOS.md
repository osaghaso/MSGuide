# MSGuide: four varied hackathon demo scenarios

Revised proposal for Uyi and Seyi · September 15, 2026

## The story: one companion across the employee's working day

> “Help me get unblocked, wherever I'm working.”

These scenarios span developer tools, cloud operations, access governance and business reporting. Each starts with a concrete blocker and ends with observable progress—not merely a documentation link or a tour of menus.

| # | Environment | User's problem | Observable outcome |
| --- | --- | --- | --- |
| 1 | **Visual Studio** | “This sample app crashes. I don't know how to debug it.” | User pauses execution and identifies the null input behind the sample exception. |
| 2 | **Azure portal** | “The test website is returning errors. Where do I investigate?” | User reaches a relevant diagnostic report and records an evidence-backed next check. |
| 3 | **Project access / My Access** | “I joined the project, but I can't open its workspace.” | User prepares a correctly scoped request through the approved route; an authorized test submission may show Pending approval. |
| 4 | **Excel** | “I need a project-status report from this task list.” | User creates a correct status-count PivotTable, optionally with a chart. |

**Recommended short-demo pairing:** access as the relatable opener, followed by Visual Studio as the technical demonstration. Azure adds cloud-operations breadth; Excel offers a business-user alternative. Choose based on audience and verified readiness, not a requirement to rush four integrations.

**Current readiness:** all four are proposed workflows. The existing deterministic provider only supports the synthetic MSGuide Demo Build Center. Native capture and foreground-overlay validation remain blocked, and no live model or enterprise retrieval has been verified. Build Center can demonstrate shared mechanics only after its own gates pass; it is not a Visual Studio or Azure integration.

## Shared experience

**Ask → choose a window → review and approve context → get one grounded next step → user acts → review and approve a fresh observation → adapt.**

- The user operates the controls. MSGuide does not click, type, grant access, deploy or repair on their behalf.
- Documentation explains the procedure; screen evidence establishes the current controls and state. Documentation alone does not prove a control is present.
- Switching applications or separately captured dialogs requires explicit selection and approval. No silent whole-desktop capture or continuous upload.
- Example responses below describe proposed behavior, not outputs from working integrations.
- Use invented local content or an explicitly approved test environment. Keep credentials, MFA codes, production logs and unrelated personal information out of shared context.

## Scenario 1: Visual Studio — “Show me why this app crashes.”

### Developer story and starting point

**Persona:** Alex, a fictional engineer opening a small unfamiliar C# application in **Visual Studio for Windows**, not Visual Studio Code.

**Prompt:** “This sample app crashes when I format a customer's name. Show me how to find the cause in Visual Studio.”

**Fixture to prepare:** a disposable local C# application with a method named `FormatDisplayName`. A test call supplies a null name, and the method calls `Trim()` on it. It fails predictably with `NullReferenceException`. No real customer data, external services or package installation is part of the recording.

**Starting screen:** the relevant source is open. Alex has reproduced the failure but does not know where to pause execution or how to inspect the argument. Verify the fixture and debugger configuration beforehand; an exception helper is not guaranteed under every configuration.

**Why normal documentation leaves a gap:** “Inspect the variable before the exception” assumes the user knows how to set a breakpoint, find Locals and distinguish stepping from continuing.

### Visual Studio walkthrough

| Beat | User action | Proposed MSGuide guidance | Evidence of progress |
| --- | --- | --- | --- |
| 1. Locate the operation | Review `FormatDisplayName`. | “Pause before the call to `Trim()` so you can inspect the name passed in.” | The statement is readable or confirmed by the user. |
| 2. Set a breakpoint | Use the tested breakpoint command or source margin. | “Set a breakpoint here. It will stop execution before this statement runs.” | A breakpoint is visible; do not claim detection if its state is unreadable. |
| 3. Start debugging | Choose Debug → Start Debugging, or press F5 themselves. | “Run under the debugger, then inspect the paused state.” | Execution stops at the intended statement. |
| 4. Inspect the input | Open Locals or another available variable-inspection view. | “Check the name argument. Is its value null?” | Variable identity and value are visible or confirmed by the user. |
| 5. Explain the failure | Inspect the failing operation and caller. | “In this sample, calling `Trim()` on a null name explains the exception. Check what the caller should do with a missing name.” | The observed value and operation support the explanation. |
| 6. Optional repair | Stop debugging, apply a reviewed local change and rerun sample cases manually. | “Choose the expected missing-name behavior, then test missing and normal inputs.” | Actual run/test results, not an assumed repair. |

**Core endpoint:** Alex identifies the null argument and failing operation. A fix is optional, not required for the video. Do not automatically recommend replacing null with an empty string without understanding the intended behavior.

**Adaptive moment:** start a second take with execution already paused. MSGuide should guide inspection, not start a new debugging session. In a variant with a non-null argument, it must not repeat the null diagnosis.

**Why it is compelling:** the confusing crash becomes an understandable explanation through real tool use. Alex learns to investigate rather than simply receiving replacement code.

### Visual Studio preparation and success gate

- Create and independently verify the fixture; pin the Visual Studio version, workload and recording layout.
- Probe source, toolbar and Locals capture. Existing UI Automation names/types/boxes may not expose source lines or variable values adequately.
- Outline a code location only when its bounds are grounded. Otherwise use text guidance and user selection; never invent gutter coordinates.
- Add supported-state guidance for editing, paused execution and variable inspection, with reviewed debugger documentation.
- Test null/non-null inputs, an unavailable pane and already-paused execution. Require three consecutive reset/runs to the core endpoint.

**Reference:** [Visual Studio debugger overview](https://learn.microsoft.com/en-us/visualstudio/debugger/debugger-feature-tour) documents breakpoints, starting debugging, stepping and variable inspection. Visual Studio also provides Copilot debugging assistance; the proposed differentiator is a consistent guided experience across tools, not an exclusive debugging capability.

## Scenario 2: Azure — “The test website is failing. What should I inspect first?”

### Cloud story and starting point

**Persona:** Sam, a fictional engineer responsible for a development web app who does not yet know Azure portal diagnostics.

**Prompt:** “This test website is returning server errors. Guide me to the evidence before I change anything.”

**Preferred environment:** an already provisioned, explicitly approved nonproduction App Service with sufficient read permissions and a reviewed period of test errors. Resource preparation, traffic generation and costs require separate approval; this plan does not authorize or perform them.

**Starting screen:** the intended app's overview blade. Sam knows the app, subscription/environment and failure interval. Telemetry is test-only and reviewed for sensitive content.

**Synthetic alternative:** a clearly labeled interactive diagnostics sandbox with invented data. That demonstrates the concept, not a live Azure investigation. The existing Build Center is not this sandbox; a new fixture would need implementation.

**Why normal documentation leaves a gap:** a runbook says “check availability diagnostics,” but the user faces many portal blades and may jump to Restart without understanding the failure.

### Azure walkthrough

| Beat | User action | Proposed MSGuide guidance | Evidence of progress |
| --- | --- | --- | --- |
| 1. Confirm scope | Review resource identity and environment. | “Confirm this is the intended test app and subscription.” | User confirms the resource; a window title alone is insufficient. |
| 2. Find diagnostics | Open Diagnose and solve problems. | “Start with diagnostics before making changes.” | Diagnostics landing page opens. |
| 3. Follow the symptom | Select a relevant availability diagnostic, such as Web App Down, if available. | “Choose the diagnostic matching the reported availability problem.” | A report opens; labels depend on app type and portal version. |
| 4. Bound the evidence | Review or set the available report interval. | “Match the report to the failure window and check the time zone.” | The displayed interval matches the investigation. |
| 5. Inspect a finding | Expand the relevant report detail. | “Separate the observed symptom from a possible cause. What does this finding establish?” | A specific finding, interval and supporting detail can be inspected. |
| 6. Choose the next check | Follow a read-only evidence step supported by the report and approved runbook. | “Record this evidence and the next check. It does not yet prove the root cause.” | Sam records a bounded conclusion and next diagnostic action. |

**Expected fixture result:** reviewed telemetry establishes errors during the chosen test interval. Use a startup-error or missing-setting explanation only if the actual test evidence supports it. A generic HTTP 500 is not enough to identify a cause.

**Adaptive moment:** the selected interval contains no relevant data. MSGuide asks to confirm the interval and symptom; it does not announce that the app is healthy. A wrong resource produces a stop and resource-selection clarification.

**Visible outcome:** Sam reaches useful evidence and knows what to investigate next. End before changing settings, restarting, scaling, redeploying, enabling diagnostics or collecting new dumps/traces.

### Azure preparation and success gate

- Confirm approved test scope, resource identity, read permissions and usable existing telemetry before selecting live mode.
- Verify browser capture, portal targets and report readability. Do not assume chart values are accessible to the current desktop.
- Add a narrow reviewed guide and evidence-based state transitions. Some diagnostics require prior configuration; enabling them is not a read-only navigation step.
- Test wrong-resource, missing-permission, empty-interval and unreadable-report branches.
- Require three repeatable runs without resource mutation. If evidence is unavailable, narrow the claim to navigation or use an explicitly synthetic fixture.

**Reference:** [Diagnostics in Azure App Service](https://learn.microsoft.com/en-us/azure/app-service/overview-diagnostics) documents Diagnose and solve problems, categories and reports. It does not guarantee a particular finding for the selected app.

## Scenario 3: Getting access — “I joined the project, but I'm blocked.”

### Access story and starting point

**Persona:** Jordan, a fictional new project member who cannot open a project workspace and does not know the access-request process.

**Prompt:** “I need to review this project's documents, but I get Access denied. Help me request the right access.”

**Starting screen:** an access-denied page for a test project workspace. A reviewed project onboarding guide provides its official access route. Choose a test project that actually uses an Entra entitlement-management access package; not every workspace, repository or Azure resource does.

**Fixture options:**

- **Lowest-dependency version:** a labeled local access-request sandbox containing a project guide, request form and simulated Pending approval state. No real account, message or permission changes occur.
- **Live test version:** an approved test tenant where My Access, an appropriate package, an eligible requestor and an approval-required policy already exist. This plan does not configure a tenant or create a package.

**Why normal documentation leaves a gap:** “Ask for access” does not explain which system, package, scope, justification or approval status matters for this specific project.

### Access walkthrough

| Beat | User action | Proposed MSGuide guidance | Evidence of progress |
| --- | --- | --- | --- |
| 1. Understand the denial | Confirm required resource and intended work account. | “This screen alone doesn't identify the missing permission. Confirm the project and account.” | Resource and intended identity are confirmed without collecting credentials. |
| 2. Follow the approved route | Open the reviewed project guide's request link manually. | “This project's guide routes workspace access through this package.” | Official route or clearly labeled synthetic equivalent opens. |
| 3. Check scope | Inspect package resources, description and applicable policy. | “Check that this covers document review. Don't request broader access just to remove the error.” | Relevant scope and policy are understood. |
| 4. Explain the need | Complete required questions and justification. | “State your actual task. For the fixture: ‘Review Project Atlas onboarding documents for the pilot.’” | Fields contain accurate, user-reviewed information. |
| 5. Review duration | Choose a permitted period if the policy offers that option. | “Request only the time needed and allowed by the policy.” | User checks available dates and details; no invented duration control. |
| 6. Review or submit | Review the final request; submit only in an approved test setup or simulation. | “Submitting is not approval. Confirm the request before you submit.” | Reviewed draft, or an accurately labeled request record. |
| 7. Track status | Open Request history if submitted. | “Pending approval means access has not been granted. Follow the documented next step.” | Jordan locates the request and understands its state. |

**Adaptive moment:** a similarly named package grants administration instead of document review. MSGuide flags the observable scope mismatch or asks for clarification. It never recommends the broadest permission as a shortcut.

**Recovery branches:** if the package is missing or no policy applies, use the documented owner/support route rather than inventing one. If an identical request is pending, inspect it instead of creating a duplicate.

**Visible outcome:** Jordan has a correct draft or tracked request and understands what happens next. Approval, provisioning and successful workspace access are distinct later events; Pending approval is not “access granted.”

### Access preparation and success gate

- Prepare a reviewed resource-to-request-route mapping. Neither a window title nor generic public documentation identifies the organization's correct package or approver.
- Build the controlled fixture, or confirm live prerequisites and policy with the owner. Add guidance, not an approval engine.
- Support denial, package selection, required fields, review and status states.
- Pause capture during authentication, MFA and other secret entry; do not share credentials or unrelated identity information with a model.
- Default to final review without submission. A live submission can create records and notify approvers; require separate explicit approval and coordinated test identities.
- Test wrong account/package, unavailable package, missing required field and existing pending request. Use three sandbox reset/runs, or draft-only live rehearsals to avoid duplicate submissions.

**Reference:** [Request access to an access package](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-request-access) covers My Access requests, policy-dependent questions/duration and request history. It cannot establish an internal project's actual access route.

**Audience takeaway:** “I don't have to know every access system in advance. MSGuide helps me follow the right process without bypassing approval.”

## Scenario 4: Excel — “Turn this task list into a project-status report.”

### Business story and starting point

**Persona:** Maya, a fictional coordinator preparing a project review who has never configured a PivotTable.

**Prompt:** “Show me how to summarize completed, in-progress and blocked tasks from this spreadsheet.”

**Starting screen:** a disposable local workbook containing twelve invented tasks and no report. Columns are Task ID, Work item, Workstream and Status. Use the exact fixture in [the Excel playbook](DEMO_EXCEL_PLAYBOOK.md).

**Why normal documentation leaves a gap:** “Create a PivotTable” assumes the user can identify the source range, arrange fields and distinguish grouping from counting.

### Excel walkthrough

| Beat | User action | Proposed MSGuide guidance | Evidence of progress |
| --- | --- | --- | --- |
| 1. Start | Select a source cell and open Insert. | “A PivotTable can summarize these tasks. Select PivotTable.” | Creation dialog opens. |
| 2. Check source | Include headers and twelve records; choose New Worksheet. | “Check the range before creating the report.” | Empty report and field list appear. |
| 3. Group | Place Status in Rows. | “Create one group for each status.” | Three status groups appear. |
| 4. Count | Place Task ID in Values and select Count. | “Count identifiers rather than summing them.” | Counts appear for each group. |
| 5. Optional finish | Create a PivotChart. | “Choose a chart comparing the three counts.” | A presentable visual appears. |

**Adaptive moment:** Task ID is accidentally in Rows. Guide correction only when field placement is readable; otherwise ask for the field pane or confirmation. The same prompt on a correct report must not trigger the wrong correction.

**Visible outcome:** 6 Completed, 4 In progress, 2 Blocked; grand total 12. These are fixture snapshot counts, not real team performance or completions in a particular week.

**Minimum work:** sample workbook, Excel capture/dialog feasibility, field-placement guidance and reviewed source attribution. Only the user changes the disposable workbook.

**Success gate:** three reset/runs produce correct totals, respond appropriately to placement variants and use observed targets. Chart is optional. A chart existing does not prove the underlying counts are correct.

**Detailed preparation:** [the Excel playbook](DEMO_EXCEL_PLAYBOOK.md) contains sample rows, Microsoft Support references, failure paths and a single-story video script. Excel is the business-user option, not the only recommended lead.

## What to focus on: breadth without four rushed integrations

| Goal | Best candidate | Biggest dependency |
| --- | --- | --- |
| Relatable employee blocker | Getting access | Reviewed project routing knowledge and sandbox or approved test workflow. |
| Strong technical demonstration | Visual Studio | Readable source/debugger state and grounded targeting. |
| Cloud/platform relevance | Azure | Approved existing test environment, permissions and evidence, or labeled simulation. |
| Business outcome | Excel | Field-pane/dialog capture and correct report configuration. |

Recommended implementation order:

1. Resolve or characterize shared capture/overlay blockers on the recording machine.
2. Probe Visual Studio and a controlled access fixture as the preferred pair.
3. Choose live Azure only if its approved environment and evidence already exist; do not provision infrastructure just to manufacture a story.
4. Use Excel as the business alternative if it is more reliable, or as a fourth brief example after acceptance.

Time-box each initial feasibility probe to approximately 60–90 minutes as a planning choice, not a complete implementation estimate. Stop expanding a candidate if its essential state cannot be read reliably. Current UI Automation is not a full IDE, portal or workbook data model.

## Video and presentation options

### Preferred two-minute submission: access plus Visual Studio

| Time | Scene | Suggested narration |
| --- | --- | --- |
| 0:00–0:12 | Access-denied screen and project task. | “Getting work done often starts with figuring out where to go and what to do next.” |
| 0:12–0:25 | Ask, select window, review and approve. | “MSGuide guides you using the context you approve.” |
| 0:25–0:55 | Approved route → correctly scoped request draft. | “Find the right request and understand the approval process.” |
| 0:55–1:30 | Visual Studio breakpoint → fresh observation → null input. | “In a technical tool, the same companion helps you use the debugger and understand the evidence.” |
| 1:30–1:43 | Verified outcomes and user-operated controls. | “A reviewable access request. An understood application failure. You perform every action.” |
| 1:43–1:55 | Pause/clear and prototype disclosure. | “MSGuide: help where the work happens.” |

These are separate vignettes: a draft request does not grant access to the sample app. Do not imply approval happened instantly between scenes.

### Alternative overview: all four scenarios

Target 1:55: problem/consent 0:00–0:20; access 0:20–0:42; Visual Studio 0:42–1:04; Azure 1:04–1:26; Excel 1:26–1:48; closing 1:48–1:55.

Show one actual guidance moment and a verified outcome per implemented scenario. Shorten repeated steps with labeled cuts, not fabricated footage. Unbuilt scenarios may appear only as clearly labeled concepts. Show at least one fresh approval explicitly; edited runtime is not task completion time.

For a longer team walkthrough, allocate approximately 3–5 minutes per accepted workflow plus questions. This is rehearsal time, not a measured performance claim. Show one recovery branch per scenario instead of only happy paths.

## Shared acceptance gates before recording

- **Repeatability:** three consecutive successful interactive runs per included scenario, with safe resets and independent outcome checks.
- **Capture and targets:** legible selected-window previews; grounded outlines that remain click-through and disappear when context becomes invalid. Never bypass blank-image or freshness guards.
- **Consent:** explicit selection/review/approval for each new observation; no hidden continuous capture or upload.
- **Guidance:** missing, duplicate or unreadable targets produce clarification. State changes drive guidance changes, not a fixed sequence presented as reasoning.
- **Sources:** only vetted sources actually used. The existing sample retriever is not enterprise search; placeholder links are not internal documentation.
- **Model mode:** no approved endpoint/model has been evaluated live. Label deterministic behavior scripted prototype; claim model-guided operation only after end-to-end evaluation.
- **Safety:** no automatic actions, real access grants, production changes, secrets or implicit expansion of capture scope.
- **Voice:** typed prompts are the baseline; include local speech only after microphone, stop and playback tests on the recording machine.
- **Honest outcomes:** pending request is not granted access; diagnostics are not recovery; understanding an exception is not a tested fix; chart presence is not report correctness.

Latest runtime evidence is in [docs/VALIDATION.md](VALIDATION.md) and [docs/HANDOFF.md](HANDOFF.md). Blank selected-window captures and foreground activation failures remain unresolved. Passing backend tests or desktop builds does not establish that these workflows work. If runtime remains unreliable, use a labeled concept storyboard and separate verified component footage, subject to submission rules.

## Review and submission schedule

The meeting supplied a September 21, 2026 deadline and two-minute video limit. Confirm both and the time zone on the submission page; they were not independently verified here.

| Date | Deliverable / decision |
| --- | --- |
| Wednesday, September 16 | Review four varied stories; select primary pair, fixture mode and approved sources; complete feasibility probes. |
| Thursday, September 17 | Implement selected gaps, test recovery branches and rehearse acceptance runs. |
| Friday, September 18 | Record, review privacy/claims and submit if ready. |
| September 19–21 | Buffer for retakes and submission issues; no new integrations. |

## Claims and evidence

Public Microsoft debugger, App Service diagnostics and My Access request documentation was consulted for this revision. No internal project access route, tenant policy, customer incident or deployment was queried. Characters and fixtures are invented. No application code, cloud resources or access assignments were changed for this planning task.

**Claim to make:** “MSGuide aims to connect instructions to action across employee tools, while the user stays in control.”

**Not yet supported:** arbitrary-app compatibility, production readiness, tenant-aware authorization, measured time savings, autonomous remediation or seamless cross-application task completion. Microsoft applications already offer native help and Copilot capabilities; position MSGuide around a consistent guided experience rather than claiming those tools cannot help.
