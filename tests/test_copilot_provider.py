"""Copilot SDK provider tests use a fake runtime only."""

import asyncio
import base64
from io import BytesIO
import json
from pathlib import Path
from types import SimpleNamespace
from datetime import timedelta
from uuid import uuid4

import pytest

import src.copilot_provider as copilot_provider
from src.copilot_provider import (
    AGENCY_LEARN_READ_ONLY_TOOLS,
    AgencyMicrosoftLearnConfig,
    ApprovedGuidanceContext,
    COPILOT_INFERENCE_TIMEOUT_SECONDS,
    CopilotProvider,
    CopilotProviderConfig,
    CopilotProviderFailure,
)
from src.main import now
from src.models import Observation, TaskProgress, TaskStep


@pytest.fixture(autouse=True)
def clear_copilot_cli_path(monkeypatch):
    monkeypatch.delenv("COPILOT_CLI_PATH", raising=False)


def observation(image=False):
    image_base64 = None
    if image:
        from PIL import Image

        stream = BytesIO()
        Image.new("RGB", (2, 2), "red").save(stream, format="PNG")
        image_base64 = base64.b64encode(stream.getvalue()).decode("ascii")
    return Observation.model_validate(
        {
            "id": "obs-1",
            "windowId": "window-1",
            "application": "Public sample",
            "capturedAt": now().isoformat(),
            "width": 2,
            "height": 2,
            "ocrText": "untrusted screen text",
            "elements": [
                {
                    "role": "button",
                    "label": "Continue",
                    "box": [0.1, 0.2, 0.3, 0.1],
                    "confidence": 0.9,
                    "automationId": "continue-button",
                    "frameworkId": "WPF",
                    "isEnabled": True,
                    "isOffscreen": False,
                    "isPassword": False,
                    "targetable": True,
                    "targetId": "uia-continue",
                    "action": "invoke",
                }
            ],
            "imageBase64": image_base64,
        }
    )


def approved_context(obs):
    return {
        "observationId": obs.id,
        "stepId": "step-2",
        "targets": [{"id": "continue", "elementIndex": 0}],
        "citations": [
            {
                "id": "learn-1",
                "citation": {
                    "source": "https://learn.microsoft.com/example",
                    "title": "Approved Learn page",
                },
            }
        ],
    }


class FakeSession:
    def __init__(self, options, output, delay=0, fail_after_output=False,
                 abort_delay=0, disconnect_delay=0):
        self.options = options
        self.output = output
        self.delay = delay
        self.fail_after_output = fail_after_output
        self.sent = None
        self.disconnected = False
        self.aborted = False
        self.agent_active = False
        self.abort_delay, self.disconnect_delay = abort_delay, disconnect_delay
        self.sent_timeout = None

    async def send_and_wait(self, prompt, *, attachments, timeout=60):
        self.sent = (prompt, attachments)
        self.sent_timeout = timeout
        self.agent_active = True
        if self.delay:
            await asyncio.sleep(self.delay)
        output = self.output(json.loads(prompt)) if callable(self.output) else self.output
        outputs = output if isinstance(output, list) else [output]
        for output in outputs:
            if output is None:
                continue
            invocation = SimpleNamespace(arguments=output)
            await self.options["tools"][0].handler(invocation)
        if self.fail_after_output:
            raise RuntimeError("private post-guidance runtime failure")
        self.agent_active = False

    async def abort(self):
        await asyncio.sleep(self.abort_delay)
        self.aborted = True
        self.agent_active = False

    async def disconnect(self):
        await asyncio.sleep(self.disconnect_delay)
        self.disconnected = True


class FakeClient:
    def __init__(self, output, delay=0, fail_start=False, fail_after_output=False,
                 create_delay=0, abort_delay=0, disconnect_delay=0,
                 authenticated=True, auth_delay=0, auth_failure=False,
                 inherited_servers=None, discovery_failure=False, **kwargs):
        self.output = output
        self.delay = delay
        self.fail_start = fail_start
        self.fail_after_output = fail_after_output
        self.kwargs = kwargs
        self.started = 0
        self.stopped = 0
        self.sessions = []
        self.create_delay = create_delay
        self.abort_delay, self.disconnect_delay = abort_delay, disconnect_delay
        self.authenticated, self.auth_delay, self.auth_failure = authenticated, auth_delay, auth_failure
        self.auth_checks = 0
        self.auth_entered = asyncio.Event()
        self.inherited_servers = list(inherited_servers or [])
        self.discovery_failure = discovery_failure
        self.discovery_calls = 0
        self.rpc = SimpleNamespace(mcp=SimpleNamespace(discover=self.discover_mcp))

    async def start(self):
        self.started += 1
        if self.fail_start:
            raise RuntimeError("private runtime error")

    async def stop(self):
        self.stopped += 1
        for session in self.sessions:
            session.agent_active = False

    async def get_auth_status(self):
        self.auth_checks += 1
        self.auth_entered.set()
        if self.auth_delay:
            await asyncio.sleep(self.auth_delay)
        if self.auth_failure:
            raise RuntimeError("private authentication response")
        return SimpleNamespace(isAuthenticated=self.authenticated)

    async def discover_mcp(self, request):
        self.discovery_calls += 1
        if self.discovery_failure:
            raise RuntimeError("private discovery details")
        assert request.working_directory is not None
        return SimpleNamespace(servers=[SimpleNamespace(name=name) for name in self.inherited_servers])

    async def create_session(self, **options):
        await asyncio.sleep(self.create_delay)
        session = FakeSession(options, self.output, self.delay, self.fail_after_output,
                              self.abort_delay, self.disconnect_delay)
        self.sessions.append(session)
        return session


def provider(tmp_path, output, **client_changes):
    clients = []

    def factory(**kwargs):
        client = FakeClient(output, **client_changes, **kwargs)
        clients.append(client)
        return client

    result = CopilotProvider(
        CopilotProviderConfig(model="gpt-5", base_directory=tmp_path),
        approved_context,
        client_factory=factory,
    )
    return result, clients[0]


def test_inference_timeout_is_bounded_for_fresh_observations(tmp_path):
    config = CopilotProviderConfig(model="gpt-6-astra", base_directory=tmp_path)
    assert config.timeout_seconds == COPILOT_INFERENCE_TIMEOUT_SECONDS == 50.0
    with pytest.raises(ValueError):
        CopilotProviderConfig(
            model="gpt-6-astra",
            base_directory=tmp_path,
            timeout_seconds=COPILOT_INFERENCE_TIMEOUT_SECONDS + 0.1,
        ).validate()


def test_explicit_github_token_is_passed_to_runtime_client(tmp_path):
    clients = []

    def factory(**kwargs):
        clients.append(FakeClient(None, **kwargs))
        return clients[0]

    CopilotProvider(
        CopilotProviderConfig(
            model="gpt-6-astra",
            base_directory=tmp_path,
            github_token="github_pat_test",
        ),
        approved_context,
        client_factory=factory,
    )

    assert clients[0].kwargs["github_token"] == "github_pat_test"
    assert clients[0].kwargs["mode"] == "empty"
    assert clients[0].kwargs["use_logged_in_user"] is False


@pytest.mark.asyncio
async def test_persistent_client_and_short_lived_validated_sessions(tmp_path):
    output = {
        "observationId": "obs-1",
        "stepId": "step-2",
        "targetId": "continue",
        "status": "next_step",
        "instruction": "Select Continue.",
        "citationIds": ["learn-1"],
    }
    model, client = provider(tmp_path, output)
    await model.start()
    await model.start()
    first = await model("Help me", observation(image=True))
    second = await model("Help again", observation())
    await model.close()

    assert client.started == client.stopped == 1
    assert client.auth_checks == 1
    assert len(client.sessions) == 2
    assert all(session.disconnected for session in client.sessions)
    assert first.model_dump(mode="json") == second.model_dump(mode="json")
    assert first.status == "next_step" and first.target.label == "Continue"
    assert first.target.targetId == "uia-continue"
    assert first.target.action == "invoke"
    assert first.citations[0].source == "https://learn.microsoft.com/example"
    assert client.kwargs["mode"] == "copilot-cli"
    assert client.kwargs["use_logged_in_user"] is True
    options = client.sessions[0].options
    assert list(options["available_tools"]) == ["custom:submit_guidance"]
    assert options["enable_session_store"] is False
    assert options["infinite_sessions"] == {"enabled": False}
    assert options["memory"] == {"enabled": False}
    assert options["enable_config_discovery"] is False
    assert options["enable_file_hooks"] is False
    assert options["enable_host_git_operations"] is False
    assert options["enable_skills"] is False
    assert options["custom_agents_local_only"] is True
    assert options["enable_on_demand_instruction_discovery"] is False
    assert options["skip_custom_instructions"] is True
    assert options["disabled_mcp_servers"] == ["github-mcp-server"]
    assert "mcp_servers" not in options
    assert options["on_mcp_auth_request"](
        {"serverName": "unrelated", "serverUrl": "https://private.invalid", "requestId": "private"},
        {"sessionId": "private"},
    ) == {"kind": "cancelled"}
    assert options["reasoning_effort"] == "xhigh"
    assert options["context_tier"] == "long_context"
    payload, attachments = client.sessions[0].sent
    body = json.loads(payload)
    assert body["untrustedObservation"]["ocrText"] == "untrusted screen text"
    assert "imageBase64" not in payload and "box" not in body["serverApproved"]["targets"][0]
    assert body["serverApproved"]["targets"][0] == {"targetId": "continue", "elementIndex": 0}
    assert body["untrustedObservation"]["elements"][0]["action"] == "invoke"
    assert attachments == [
        {
            "type": "blob",
            "data": observation(image=True).imageBase64,
            "mimeType": "image/png",
            "displayName": "approved-observation.png",
        }
    ]
    assert client.sessions[1].sent[1] == []
    assert all(0 < session.sent_timeout <= 50 for session in client.sessions)
    assert all(session.aborted and not session.agent_active for session in client.sessions)


@pytest.mark.asyncio
@pytest.mark.parametrize("failure", ["signed_out", "invalid_status", "error", "timeout", "cancelled"])
async def test_auth_preflight_fails_before_sessions_and_cleans_runtime(tmp_path, failure):
    model, client = provider(
        tmp_path, None,
        authenticated=False if failure == "signed_out" else "true" if failure == "invalid_status" else True,
        auth_failure=failure == "error",
        auth_delay=10 if failure in {"timeout", "cancelled"} else 0,
    )
    object.__setattr__(model.config, "startup_timeout_seconds", 0.1)
    starting = asyncio.create_task(model.start())
    await client.auth_entered.wait()
    if failure == "cancelled":
        starting.cancel()
    expected = asyncio.CancelledError if failure == "cancelled" else CopilotProviderFailure
    with pytest.raises(expected) as error:
        await asyncio.wait_for(starting, 1)
    if failure != "cancelled":
        assert error.value.code == "startup" and "private" not in str(error.value)
    assert client.auth_checks == 1 and client.started == client.stopped == 1
    assert not client.sessions and not model._started and not model._runtime_owned
    await model.close()
    assert client.stopped == 1


def test_mcp_sign_in_requests_are_cancelled_without_logging_identity(tmp_path):
    log = tmp_path / "auth.log"
    copilot_provider.diagnostics.configure(str(log))
    try:
        result = CopilotProvider._reject_mcp_auth(
            {"serverName": "private-server", "serverUrl": "https://private.invalid",
             "requestId": "private-request", "staticClientConfig": {"clientSecret": "private-secret"}},
            {"sessionId": "private-session"},
        )
    finally:
        copilot_provider.diagnostics.configure(None)
    assert result == {"kind": "cancelled"}
    contents = log.read_text(encoding="utf-8")
    assert "copilot_mcp_auth_rejected" in contents
    assert "private" not in contents


@pytest.mark.asyncio
async def test_every_session_explicitly_blocks_new_inherited_mcp_servers(tmp_path):
    output = {"observationId": "obs-1", "stepId": "step-2", "status": "needs_input",
              "targetId": None, "instruction": "Synthetic guidance.", "citationIds": []}
    model, client = provider(tmp_path, output, inherited_servers=["user-mcp", "workspace-mcp", "plugin-mcp"])
    async with model:
        await model("Synthetic check", observation())
        client.inherited_servers.append("new-mcp")
        await model("Another synthetic check", observation())
    assert client.auth_checks == 1 and client.discovery_calls == 2
    assert client.sessions[0].options["disabled_mcp_servers"] == ["github-mcp-server", "plugin-mcp", "user-mcp", "workspace-mcp"]
    assert client.sessions[1].options["disabled_mcp_servers"] == ["github-mcp-server", "new-mcp", "plugin-mcp", "user-mcp", "workspace-mcp"]
    assert all(session.options["enable_config_discovery"] is False for session in client.sessions)


@pytest.mark.asyncio
async def test_failed_mcp_inventory_cannot_create_an_unisolated_session(tmp_path):
    model, client = provider(tmp_path, None, discovery_failure=True)
    async with model:
        with pytest.raises(CopilotProviderFailure) as failure:
            await model("Synthetic check", observation())
        assert failure.value.code == "runtime" and "private" not in str(failure.value)
        assert not client.sessions and model._started


@pytest.mark.asyncio
async def test_accepted_guidance_survives_later_session_failure(tmp_path):
    output = {
        "observationId": "obs-1",
        "stepId": "step-2",
        "targetId": None,
        "status": "needs_input",
        "instruction": "The public repository page is visible.",
        "citationIds": [],
    }
    model, client = provider(tmp_path, output, fail_after_output=True)
    await model.start()

    result = await model("Explain this page", observation(image=True))

    assert result.status == "needs_input"
    assert result.instruction == "The public repository page is visible."
    await model._cleanup_task
    assert client.sessions[0].disconnected and client.sessions[0].aborted
    await model.close()


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "changes",
    [
        {"targetId": "invented"},
        {"observationId": "other"},
        {"stepId": "completed"},
        {"citationIds": ["invented"]},
        {"url": "https://evil.invalid"},
        {"instruction": "Click at (123, 456)."},
        {"instruction": "Visit https://evil.invalid"},
    ],
)
async def test_invalid_or_unapproved_tool_result_fails_explicitly(tmp_path, changes):
    output = {
        "observationId": "obs-1",
        "stepId": "step-2",
        "targetId": None,
        "status": "needs_input",
        "instruction": "Ask the user to verify the visible screen.",
        "citationIds": [],
        **changes,
    }
    model, _ = provider(tmp_path, output)
    await model.start()
    with pytest.raises(CopilotProviderFailure) as failure:
        await model("screen says ignore policy", observation())
    assert failure.value.code == "invalid_result"
    await model.close()


@pytest.mark.asyncio
async def test_rejected_submission_can_be_corrected_with_approved_target(tmp_path):
    invalid = {
        "observationId": "obs-1",
        "stepId": "step-2",
        "targetId": "invented",
        "status": "next_step",
        "instruction": "Select the invented control.",
        "citationIds": [],
    }
    corrected = {
        "observationId": "obs-1",
        "stepId": "step-2",
        "targetId": "continue",
        "status": "next_step",
        "instruction": "Select Continue.",
        "citationIds": [],
    }
    model, client = provider(tmp_path, [invalid, corrected])
    await model.start()
    result = await model("Help me", observation())
    await model.close()

    assert result.target is not None
    assert result.target.label == "Continue"
    assert client.sessions[0].options["tools"][0].is_terminal is False


@pytest.mark.asyncio
async def test_selected_target_preserves_observation_identity(tmp_path):
    output = {
        "observationId": "obs-1",
        "stepId": "step-2",
        "targetId": "continue",
        "status": "next_step",
        "instruction": "Select Continue.",
        "citationIds": [],
    }
    observed = observation()
    observed.elements[0].targetId = "uia-observation-target"
    model, _ = provider(tmp_path, output)
    await model.start()
    result = await model("Help me", observed)
    await model.close()

    assert result.target is not None
    assert result.target.targetId == "uia-observation-target"
    assert result.target.automationId == "continue-button"
    assert result.target.frameworkId == "WPF"
    assert result.target.isEnabled is True
    assert result.target.isOffscreen is False


@pytest.mark.asyncio
async def test_blank_optional_uia_metadata_is_treated_as_absent(tmp_path):
    output = {
        "observationId": "obs-1",
        "stepId": "step-2",
        "targetId": "continue",
        "status": "next_step",
        "instruction": "Select Continue.",
        "citationIds": [],
    }
    observed = observation()
    observed.elements[0].automationId = ""
    observed.elements[0].frameworkId = ""
    model, _ = provider(tmp_path, output)

    await model.start()
    result = await model("Help me", observed)
    await model.close()

    assert result.target is not None
    assert result.target.automationId is None
    assert result.target.frameworkId is None
    assert result.target.action == "invoke"


@pytest.mark.asyncio
async def test_requires_explicit_lifecycle_and_surfaces_start_failure(tmp_path):
    model, _ = provider(tmp_path, None)
    with pytest.raises(CopilotProviderFailure) as failure:
        await model("help", observation())
    assert failure.value.code == "not_started"

    broken, _ = provider(tmp_path, None, fail_start=True)
    with pytest.raises(CopilotProviderFailure) as failure:
        await broken.start()
    assert failure.value.code == "startup"
    assert "private" not in str(failure.value)


@pytest.mark.asyncio
async def test_timeout_disconnects_without_fallback(tmp_path):
    model, client = provider(tmp_path, None, delay=0.1)
    object.__setattr__(model.config, "timeout_seconds", 0.01)
    await model.start()
    with pytest.raises(CopilotProviderFailure) as failure:
        await model("help", observation())
    assert failure.value.code == "timeout"
    await model._cleanup_task
    assert client.sessions[0].disconnected and client.sessions[0].aborted
    await model.close()


@pytest.mark.asyncio
async def test_caller_cancellation_disconnects_session(tmp_path):
    model, client = provider(tmp_path, None, delay=10)
    await model.start()
    task = asyncio.create_task(model("help", observation()))
    while not client.sessions:
        await asyncio.sleep(0)
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    await model._cleanup_task
    assert client.sessions[0].disconnected and client.sessions[0].aborted
    assert not client.sessions[0].agent_active
    await model.close()


@pytest.mark.asyncio
async def test_agency_mcp_is_opt_in_exact_and_read_only(tmp_path):
    output = {
        "observationId": "obs-1",
        "stepId": "step-2",
        "targetId": None,
        "status": "needs_input",
        "instruction": "Verify the visible Microsoft setting.",
        "citationIds": ["learn-1"],
    }
    clients = []

    def factory(**kwargs):
        client = FakeClient(output, inherited_servers=["msft-learn", "unrelated-mcp"], **kwargs)
        clients.append(client)
        return client

    agency = AgencyMicrosoftLearnConfig(
        enabled=True, tools=AGENCY_LEARN_READ_ONLY_TOOLS
    )
    model = CopilotProvider(
        CopilotProviderConfig(
            model="gpt-5",
            base_directory=tmp_path,
            agency_microsoft_learn=agency,
        ),
        approved_context,
        client_factory=factory,
    )
    await model.start()
    await model("help", observation())
    options = clients[0].sessions[0].options
    mcp = options["mcp_servers"]["msft-learn"]
    assert mcp["command"] == "agency" and mcp["args"] == ["mcp", "msft-learn"]
    assert tuple(mcp["tools"]) == AGENCY_LEARN_READ_ONLY_TOOLS
    assert options["disabled_mcp_servers"] == ["github-mcp-server", "unrelated-mcp"]
    assert list(options["available_tools"]) == [
        "custom:submit_guidance",
        *[f"mcp:msft-learn-{name}" for name in AGENCY_LEARN_READ_ONLY_TOOLS],
    ]

    permission = options["on_permission_request"]
    assert type(permission(SimpleNamespace(tool_name="run_shell"), {})).__name__ == (
        "PermissionDecisionReject"
    )
    assert type(
        permission(
            SimpleNamespace(
                server_name="msft-learn",
                tool_name="microsoft_docs_search",
                read_only=True,
            ),
            {},
        )
    ).__name__ == "PermissionDecisionApproveOnce"
    assert type(
        permission(
            SimpleNamespace(
                server_name="msft-learn",
                tool_name="microsoft_docs_search",
                read_only=False,
            ),
            {},
        )
    ).__name__ == "PermissionDecisionReject"
    await model.close()


def test_invalid_context_and_agency_configuration_fail_closed(tmp_path):
    with pytest.raises(ValueError):
        AgencyMicrosoftLearnConfig(enabled=True, tools=("unknown",)).validate()
    with pytest.raises(ValueError):
        CopilotProviderConfig(
            model="gpt-6-astra",
            base_directory=tmp_path,
            reasoning_effort="maximum",
        ).validate()
    with pytest.raises(ValueError):
        CopilotProviderConfig(
            model="gpt-6-astra",
            base_directory=tmp_path,
            context_tier="largest",
        ).validate()

    model = CopilotProvider(
        CopilotProviderConfig(model="gpt-5", base_directory=tmp_path),
        lambda obs: ApprovedGuidanceContext(
            observationId="other", stepId="step-1"
        ),
        client_factory=lambda **kwargs: FakeClient(None, **kwargs),
    )

    async def run():
        await model.start()
        with pytest.raises(CopilotProviderFailure) as failure:
            await model("help", observation())
        assert failure.value.code == "invalid_context"
        await model.close()

    asyncio.run(run())


@pytest.mark.parametrize("source", ["config", "environment"])
def test_local_cli_path_is_forwarded_to_stdio_connection(
    tmp_path, monkeypatch, source
):
    cli_path = tmp_path / "copilot.exe"
    cli_path.write_bytes(b"fake test executable")
    connection = object()
    seen = []

    def stdio(*, path=None, args=None):
        seen.append((path, args))
        return connection

    monkeypatch.setattr(copilot_provider.RuntimeConnection, "for_stdio", stdio)
    if source == "environment":
        monkeypatch.setenv("COPILOT_CLI_PATH", str(cli_path))
        configured_path = None
    else:
        monkeypatch.setenv("COPILOT_CLI_PATH", str(tmp_path / "ignored.exe"))
        configured_path = cli_path

    clients = []

    def factory(**kwargs):
        client = FakeClient(None, **kwargs)
        clients.append(client)
        return client

    CopilotProvider(
        CopilotProviderConfig(
            model="gpt-5",
            base_directory=tmp_path,
            cli_path=configured_path,
        ),
        approved_context,
        client_factory=factory,
    )

    assert seen == [(str(cli_path), None)]
    assert clients[0].kwargs["connection"] is connection


@pytest.mark.parametrize("kind", ["relative", "missing", "directory"])
def test_local_cli_path_must_be_an_absolute_existing_file(tmp_path, kind):
    if kind == "relative":
        cli_path = Path(tmp_path.name) / "copilot.exe"
    elif kind == "missing":
        cli_path = tmp_path / "missing-copilot.exe"
    else:
        cli_path = tmp_path
    with pytest.raises(ValueError, match="Invalid Copilot CLI path"):
        CopilotProviderConfig(
            model="gpt-5", base_directory=tmp_path, cli_path=cli_path
        ).validate()


def valid_output(**changes):
    return {
        "observationId": "obs-1", "stepId": "step-2", "targetId": "continue",
        "status": "next_step", "instruction": "Select Continue.", "citationIds": [],
        **changes,
    }


@pytest.mark.asyncio
@pytest.mark.parametrize("status", ["blocked", "needs_input", "completion_candidate"])
async def test_explicit_non_action_outcomes_are_not_completion(tmp_path, status):
    model, client = provider(tmp_path, valid_output(
        status=status, targetId=None, remainingWork="Review the requested outcome."
    ))
    async with model:
        result = await model("help", observation())
        assert result.status == status and result.target is None
        assert result.remainingWork == "Review the requested outcome."
        assert result.status != "completed"
    assert client.sessions[0].aborted


@pytest.mark.asyncio
async def test_task_history_and_step_binding_survive_isolated_sessions(tmp_path):
    model, client = provider(tmp_path, lambda body: valid_output(
        observationId=body["serverApproved"]["observationId"],
        stepId=body["serverApproved"]["stepId"],
        remainingWork="Open the next page.",
    ))
    task_id = str(uuid4())
    progress = TaskProgress(taskId=task_id, step=1)
    async with model:
        first = await model("Original goal", observation(), task=progress)
        progress = TaskProgress(
            taskId=task_id, step=2, remainingWork=first.remainingWork,
            userInput="Keep the original goal.",
            history=[TaskStep(step=1, observationId="obs-1", afterObservationId="obs-2",
                              targetId="uia-continue", label="Continue", action="invoke",
                              outcome="screen_changed")],
        )
        second = observation().model_copy(update={"id": "obs-2", "ocrText": "New page"})
        await model("Original goal", second, task=progress)
    payloads = [json.loads(session.sent[0]) for session in client.sessions]
    assert payloads[0]["serverApproved"]["stepId"] != payloads[1]["serverApproved"]["stepId"]
    assert payloads[1]["request"] == "Original goal"
    assert payloads[1]["untrustedTask"] == progress.model_dump(mode="json")
    assert payloads[1]["untrustedObservation"]["ocrText"] == "New page"
    assert payloads[1]["serverApproved"]["observationId"] == "obs-2"
    assert all(not session.agent_active for session in client.sessions)


@pytest.mark.asyncio
@pytest.mark.parametrize("changes", [
    {"action": None}, {"targetId": None}, {"targetable": False},
    {"isEnabled": False}, {"isOffscreen": True}, {"isPassword": True},
    {"confidence": 0.79},
])
async def test_non_actionable_targets_rejected_even_by_custom_context(tmp_path, changes):
    observed = observation()
    observed.elements[0] = observed.elements[0].model_copy(update=changes)
    model, client = provider(tmp_path, valid_output())
    async with model:
        with pytest.raises(CopilotProviderFailure) as failure:
            await model("help", observed)
        assert failure.value.code == "invalid_context"
    assert not client.sessions


@pytest.mark.asyncio
@pytest.mark.parametrize("action,element_changes,inputs", [
    ("set_value", {"isReadOnly": False, "valueHash": "0" * 64, "valueLength": 0}, {"value": "Synthetic text"}),
    ("set_value", {"isReadOnly": False, "valueHash": "0" * 64, "valueLength": 0}, {"value": ""}),
    ("scroll", {"scrollDirections": ["down"], "verticalScrollPercent": 0.0}, {"scrollDirection": "down"}),
])
async def test_semantic_inputs_bound_to_current_capabilities(tmp_path, action, element_changes, inputs):
    observed = observation()
    observed.elements[0] = observed.elements[0].model_copy(update={"action": action, **element_changes})
    model, _ = provider(tmp_path, valid_output(**inputs))
    async with model:
        result = await model("Perform this synthetic step", observed)
    assert result.target.action == action
    assert result.target.value == inputs.get("value")
    assert result.target.scrollDirection == inputs.get("scrollDirection")
    assert result.target.valueHash == element_changes.get("valueHash")


@pytest.mark.asyncio
@pytest.mark.parametrize("action,changes,inputs", [
    ("set_value", {}, {}),
    ("set_value", {}, {"value": "x" * 1001}),
    ("set_value", {}, {"value": "\x00"}),
    ("set_value", {"isReadOnly": True}, {"value": "text"}),
    ("set_value", {"isPassword": True}, {"value": "text"}),
    ("scroll", {}, {"scrollDirection": "left"}),
    ("scroll", {}, {"scrollDirection": "down", "value": "text"}),
    ("invoke", {}, {"value": "text"}),
])
async def test_semantic_input_mismatch_never_returns_an_action(tmp_path, action, changes, inputs):
    observed = observation()
    observed.elements[0] = observed.elements[0].model_copy(update={
        "action": action, "isReadOnly": False, "valueHash": "0" * 64, "valueLength": 0,
        "scrollDirections": ["down"], **changes,
    })
    model, _ = provider(tmp_path, valid_output(**inputs))
    async with model:
        with pytest.raises(CopilotProviderFailure) as failure:
            await model("help", observed)
        assert failure.value.code in {"invalid_context", "invalid_result"}


@pytest.mark.asyncio
async def test_remaining_freshness_includes_setup_and_explicit_sdk_timeout(tmp_path):
    model, client = provider(tmp_path, None, create_delay=0.05, delay=5)
    observed = observation().model_copy(update={"capturedAt": now() - timedelta(seconds=54.7)})
    async with model:
        started = asyncio.get_running_loop().time()
        with pytest.raises(CopilotProviderFailure) as failure:
            await model("help", observed)
        assert failure.value.code == "timeout"
        assert asyncio.get_running_loop().time() - started < 0.8
        await model._cleanup_task
        assert 0 < client.sessions[0].sent_timeout < 0.28
        assert client.sessions[0].aborted and not client.sessions[0].agent_active
    assert client.stopped == 1


@pytest.mark.asyncio
async def test_no_inference_when_freshness_has_no_headroom(tmp_path):
    model, client = provider(tmp_path, valid_output())
    async with model:
        observed = observation().model_copy(update={"capturedAt": now() - timedelta(seconds=55)})
        with pytest.raises(CopilotProviderFailure) as failure:
            await model("help", observed)
        assert failure.value.code == "timeout"
        assert not client.sessions


@pytest.mark.asyncio
async def test_accepted_result_does_not_wait_for_slow_teardown_and_cleanup_is_single_slot(tmp_path):
    model, client = provider(tmp_path, valid_output(), disconnect_delay=0.35)
    async with model:
        started = asyncio.get_running_loop().time()
        result = await model("help", observation())
        elapsed = asyncio.get_running_loop().time() - started
        assert result.status == "next_step" and elapsed < 0.2
        assert not client.sessions[0].disconnected
        next_call = asyncio.create_task(model("next", observation()))
        await asyncio.sleep(0.03)
        assert len(client.sessions) == 1  # No accumulating detached sessions.
        await next_call
    assert len(client.sessions) == 2
    assert all(session.aborted and session.disconnected for session in client.sessions)
    assert not model._owned_cleanup


@pytest.mark.asyncio
async def test_each_cleanup_stage_can_use_its_shutdown_budget_without_poisoning_next_request(tmp_path):
    model, client = provider(tmp_path, valid_output(), abort_delay=0.1, disconnect_delay=0.1)
    object.__setattr__(model.config, "shutdown_timeout_seconds", 0.15)
    async with model:
        for _ in range(2):
            result = await model("Synthetic check", observation())
            await model._cleanup_task
            assert result.status == "next_step" and model._started and not model._cleanup_failed
    assert len(client.sessions) == 2 and client.stopped == 1
    assert all(session.aborted and session.disconnected for session in client.sessions)


@pytest.mark.asyncio
async def test_cleanup_timeout_is_bounded_and_prevents_more_owned_work(tmp_path):
    model, client = provider(tmp_path, valid_output(), abort_delay=5, disconnect_delay=5)
    object.__setattr__(model.config, "shutdown_timeout_seconds", 0.1)
    await model.start()
    result = await model("help", observation())
    assert result.status == "next_step"
    await asyncio.wait_for(model._cleanup_task, 0.5)
    with pytest.raises(CopilotProviderFailure) as failure:
        await model("next", observation())
    assert failure.value.code == "runtime" and len(client.sessions) == 1
    await model.close()
    assert not client.sessions[0].agent_active


@pytest.mark.asyncio
async def test_cancelled_turn_rejects_late_callbacks_and_does_not_block_next_turn(tmp_path):
    model, client = provider(tmp_path, None, delay=30)
    async with model:
        old = asyncio.create_task(model("old", observation()))
        while not client.sessions or client.sessions[0].sent is None:
            await asyncio.sleep(0)
        old.cancel()
        with pytest.raises(asyncio.CancelledError):
            await old
        rejected = await client.sessions[0].options["tools"][0].handler(
            SimpleNamespace(arguments=valid_output())
        )
        assert rejected.result_type == "rejected"
        client.output, client.delay = valid_output(), 0
        result = await asyncio.wait_for(model("next", observation()), 0.5)
        assert result.status == "next_step"
        assert client.sessions[0].aborted and not client.sessions[0].agent_active


@pytest.mark.asyncio
async def test_only_one_correction_is_accepted(tmp_path):
    model, _ = provider(tmp_path, [
        valid_output(targetId="invented"), valid_output(stepId="wrong"), valid_output()
    ])
    async with model:
        with pytest.raises(CopilotProviderFailure) as failure:
            await model("help", observation())
        assert failure.value.code == "invalid_result"


@pytest.mark.asyncio
async def test_shutdown_cancels_call_waiting_for_retirement_without_starting_another_session(tmp_path):
    model, client = provider(tmp_path, valid_output(), disconnect_delay=0.15)
    await model.start()
    await model("first", observation())
    waiting = asyncio.create_task(model("next", observation()))
    await asyncio.sleep(0.01)
    await asyncio.wait_for(model.close(), 0.5)
    with pytest.raises(asyncio.CancelledError):
        await waiting
    assert len(client.sessions) == 1 and client.stopped == 1
    assert not client.sessions[0].agent_active


@pytest.mark.asyncio
async def test_cancel_during_session_creation_stops_owned_runtime(tmp_path):
    model, client = provider(tmp_path, valid_output(), create_delay=1)
    await model.start()
    request = asyncio.create_task(model("help", observation()))
    await asyncio.sleep(0.01)
    request.cancel()
    with pytest.raises(asyncio.CancelledError):
        await request
    await asyncio.wait_for(model._cleanup_task, 0.5)
    assert client.stopped == 1 and not model._started
    with pytest.raises(CopilotProviderFailure):
        await model("next", observation())
    await model.close()


class PartialStartClient(FakeClient):
    def __init__(self, failure, stop="success"):
        super().__init__(None)
        self.failure, self.stop_behavior = failure, stop
        self.active = False
        self.acquired = asyncio.Event()
        self.stop_entered = asyncio.Event()
        self.release_stop = asyncio.Event()

    async def start(self):
        self.started += 1
        self.active = True
        self.acquired.set()
        if self.failure == "error":
            raise RuntimeError("private partial startup detail")
        if self.failure != "ready":
            await asyncio.Event().wait()

    async def stop(self):
        self.stopped += 1
        self.stop_entered.set()
        if self.stop_behavior == "error":
            raise RuntimeError("private shutdown detail")
        if self.stop_behavior == "timeout":
            await self.release_stop.wait()
        if self.stop_behavior == "resistant":
            while not self.release_stop.is_set():
                try:
                    await self.release_stop.wait()
                except asyncio.CancelledError:
                    pass  # Deliberately uncooperative SDK; tests always release it.
        self.active = False


def partial_provider(tmp_path, failure, stop="success"):
    client = PartialStartClient(failure, stop)
    model = CopilotProvider(
        CopilotProviderConfig(model="synthetic", base_directory=tmp_path,
                              startup_timeout_seconds=0.1, shutdown_timeout_seconds=0.1),
        approved_context, client_factory=lambda **_: client,
    )
    return model, client


async def failed_start(model, client, failure):
    starting = asyncio.create_task(model.start())
    await client.acquired.wait()
    if failure == "cancel":
        starting.cancel()
    expected = asyncio.CancelledError if failure == "cancel" else CopilotProviderFailure
    with pytest.raises(expected) as error:
        await asyncio.wait_for(starting, 1)
    if failure != "cancel":
        assert error.value.code == "startup" and "private" not in str(error.value)


@pytest.mark.asyncio
@pytest.mark.parametrize("failure", ["timeout", "error", "cancel"])
async def test_partial_start_cleans_acquired_runtime_even_before_explicit_close(tmp_path, failure):
    model, client = partial_provider(tmp_path, failure)
    await failed_start(model, client, failure)
    assert not client.active and client.stopped == 1
    assert not model._started and not model._runtime_owned
    await asyncio.gather(model.close(), model.close())
    assert client.stopped == 1 and not model._owned_cleanup
    with pytest.raises(CopilotProviderFailure) as error:
        await model("No readiness authority", observation())
    assert error.value.code == "not_started" and not client.sessions


@pytest.mark.asyncio
@pytest.mark.parametrize("failure", ["error", "cancel"])
@pytest.mark.parametrize("stop", ["error", "timeout", "resistant"])
async def test_partial_start_cleanup_failure_is_bounded_preserves_error_and_blocks_reuse(tmp_path, failure, stop):
    model, client = partial_provider(tmp_path, failure, stop)
    started = asyncio.get_running_loop().time()
    try:
        await failed_start(model, client, failure)
        assert asyncio.get_running_loop().time() - started < 0.8
        assert client.active and model._runtime_owned and model._cleanup_failed and not model._started
        with pytest.raises(CopilotProviderFailure) as error:
            await model.start()
        assert error.value.code == "runtime" and client.started == 1
        with pytest.raises(CopilotProviderFailure) as error:
            await model("Must not infer", observation())
        assert error.value.code == "runtime" and not client.sessions
        for _ in range(2):
            with pytest.raises(CopilotProviderFailure) as error:
                await asyncio.wait_for(model.close(), 0.5)
            assert error.value.code == "runtime" and model._runtime_owned and not model._started
            if stop == "resistant":
                assert client.stopped == 1  # Reuse the one pending stop, never queue another worker.
    finally:
        client.release_stop.set()
        if model._runtime_stop_task is not None:
            await asyncio.wait_for(asyncio.gather(model._runtime_stop_task, return_exceptions=True), 0.5)
        client.stop_behavior = "success"
        await model.close()
    assert not client.active and not model._runtime_owned and model._cleanup_failed
    with pytest.raises(CopilotProviderFailure):
        await model.start()


@pytest.mark.asyncio
async def test_close_cancels_partial_start_and_serializes_idempotent_shutdown(tmp_path):
    model, client = partial_provider(tmp_path, "blocked")
    starting = asyncio.create_task(model.start())
    await client.acquired.wait()
    await asyncio.wait_for(asyncio.gather(model.close(), model.close()), 0.8)
    with pytest.raises(asyncio.CancelledError):
        await starting
    assert not client.active and client.stopped == 1 and not model._runtime_owned
    client.failure = "ready"
    await asyncio.gather(model.start(), model.start())
    assert client.active and client.started == 2 and model._started
    await asyncio.gather(model.close(), model.close())
    assert not client.active and client.stopped == 2 and not model._runtime_owned


@pytest.mark.asyncio
async def test_cancelled_close_keeps_runtime_owned_until_late_acknowledgement(tmp_path):
    model, client = partial_provider(tmp_path, "ready", "resistant")
    await model.start()
    closing = asyncio.create_task(model.close())
    await client.stop_entered.wait()
    closing.cancel()
    try:
        with pytest.raises(asyncio.CancelledError):
            await closing
        assert model._runtime_owned and model._cleanup_failed and not model._started
        with pytest.raises(CopilotProviderFailure):
            await model("No work after cancelled shutdown", observation())
    finally:
        client.release_stop.set()
        await asyncio.wait_for(model._runtime_stop_task, 0.5)
        await model.close()
    assert not client.active and not model._runtime_owned and client.stopped == 1


@pytest.mark.asyncio
@pytest.mark.parametrize("failure", ["timeout", "error", "cancel"])
@pytest.mark.parametrize("stop", ["success", "error"])
async def test_api_lifespan_cleans_failed_start_without_masking_original_failure(tmp_path, monkeypatch, failure, stop):
    from src.main import Config, create_app

    model, client = partial_provider(tmp_path, failure, stop)
    app = create_app(Config(token="synthetic-token"), guidance_provider=model)
    app.state.sessions["synthetic"] = object()
    job = app.state.runner.start("synthetic", "local", "view_logs", {})
    closed = 0
    original_close = model.close

    async def counted_close():
        nonlocal closed
        closed += 1
        await original_close()

    monkeypatch.setattr(model, "close", counted_close)

    async def lifespan():
        async with app.router.lifespan_context(app):
            pytest.fail("Failed startup must never yield a ready API")

    running = asyncio.create_task(lifespan())
    await client.acquired.wait()
    if failure == "cancel":
        running.cancel()
    expected = asyncio.CancelledError if failure == "cancel" else CopilotProviderFailure
    with pytest.raises(expected) as error:
        await asyncio.wait_for(running, 1)
    if failure != "cancel":
        assert error.value.code == "startup" and "private" not in str(error.value)
    assert closed == 1 and not app.state.sessions and job.task.done()
    assert not model._started and client.stopped >= 1
    assert client.active == (stop == "error")
    if stop == "error":
        assert model._cleanup_failed
        client.stop_behavior = "success"
        await model.close()
    assert not client.active and not model._runtime_owned
