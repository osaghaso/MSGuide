# MSGuide — Project Submission

## Tagline

A screen-aware Windows companion that helps employees learn tools and complete workflows through voice, visual guidance, and user-controlled assistance.

## Executive Challenge

Suggested theme: **AI-powered employee productivity and empowerment**.

Select the closest matching option in the submission form. The available dropdown options have not been reviewed, so this is a suggested theme rather than an exact challenge name.

## Topic Challenges

Select up to five matching themes, if available:

- AI assistants and agents
- Employee productivity
- Learning and onboarding
- Accessibility and inclusive experiences
- Responsible AI and privacy

## Description

### MSGuide — Help where the work happens

Employees often interrupt their work to search documentation, watch tutorials, or ask colleagues how to navigate unfamiliar tools. Traditional chat assistants can explain a process, but users still have to translate those instructions into what they see on screen.

**MSGuide aims to close that gap with a screen-aware Windows companion that explains the next step and points to the relevant control while the user stays in charge.**

### How it works

A user invokes MSGuide with a hotkey and asks a question by voice or text. They select a window, review the captured context, and explicitly approve sharing it. MSGuide returns a short instruction and, when supported by the observed interface, highlights the relevant control.

The user performs the step, then requests a fresh observation so guidance can adapt to the updated screen. Capture, microphone input, and guidance can be stopped at any time.

For example, an employee investigating a build failure could receive guidance through opening logs, finding troubleshooting information, and checking the outcome—without switching between instructions and the application.

### Our approach

MSGuide combines a native Windows desktop client with a Python/FastAPI backend. The prototype uses Windows UI Automation to identify visible interface elements and includes an optional, explicitly configured vision-model integration.

The design keeps **guidance separate from action execution**. Viewing a screen does not authorize clicks or changes. Screen sharing requires explicit approval, screenshot upload is optional, and the desktop does not automatically operate other applications.

### Expected impact

- Shorter learning curves for unfamiliar software.
- Less context switching between applications and help resources.
- More independent onboarding and troubleshooting.
- Flexible assistance through text, voice, and visual pointing.

These are intended benefits; productivity improvements have not yet been measured.

### Current prototype and next steps

The local MVP includes the desktop companion, capture review and consent, local speech support, a synthetic troubleshooting workflow, and backend safeguards. **150 backend tests pass**, alongside desktop build and safety checks.

Native capture and foreground-overlay reliability still require further interactive validation. Live-model quality has not yet been evaluated. Enterprise sign-in, permission-aware internal knowledge retrieval, and real action connectors are planned—not current capabilities.

The next milestone is a reliably repeatable screen-guidance demonstration, followed by evaluation with approved models and data.

## Keywords

MSGuide, AI assistant, Windows, screen-aware guidance, employee productivity, onboarding, voice assistance, UI Automation, human-in-the-loop, responsible AI
