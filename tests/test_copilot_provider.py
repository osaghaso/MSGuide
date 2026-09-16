"""Copilot SDK provider tests use a fake runtime only."""

import asyncio
import base64
from io import BytesIO
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

import src.copilot_provider as copilot_provider
from src.copilot_provider import (
    AGENCY_LEARN_READ_ONLY_TOOLS,
    AgencyMicrosoftLearnConfig,
    ApprovedGuidanceContext,
    CopilotProvider,
    CopilotProviderConfig,
    CopilotProviderFailure,
)
from src.main import now
from src.models import Observation


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
    def __init__(self, options, output, delay=0):
        self.options = options
        self.output = output
        self.delay = delay
        self.sent = None
        self.disconnected = False

    async def send_and_wait(self, prompt, *, attachments):
        self.sent = (prompt, attachments)
        if self.delay:
            await asyncio.sleep(self.delay)
        if self.output is not None:
            invocation = SimpleNamespace(arguments=self.output)
            await self.options["tools"][0].handler(invocation)

    async def disconnect(self):
        self.disconnected = True


class FakeClient:
    def __init__(self, output, delay=0, fail_start=False, **kwargs):
        self.output = output
        self.delay = delay
        self.fail_start = fail_start
        self.kwargs = kwargs
        self.started = 0
        self.stopped = 0
        self.sessions = []

    async def start(self):
        self.started += 1
        if self.fail_start:
            raise RuntimeError("private runtime error")

    async def stop(self):
        self.stopped += 1

    async def create_session(self, **options):
        session = FakeSession(options, self.output, self.delay)
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


@pytest.mark.asyncio
async def test_persistent_client_and_short_lived_validated_sessions(tmp_path):
    output = {
        "observationId": "obs-1",
        "stepId": "step-2",
        "targetId": "continue",
        "instruction": "Select Continue.",
        "citationIds": ["learn-1"],
    }
    model, client = provider(tmp_path, output)
    await model.start()
    first = await model("Help me", observation(image=True))
    second = await model("Help again", observation())
    await model.close()

    assert client.started == client.stopped == 1
    assert len(client.sessions) == 2
    assert all(session.disconnected for session in client.sessions)
    assert first.model_dump(mode="json") == second.model_dump(mode="json")
    assert first.status == "next_step" and first.target.label == "Continue"
    assert first.citations[0].source == "https://learn.microsoft.com/example"
    assert client.kwargs["mode"] == "empty"
    options = client.sessions[0].options
    assert list(options["available_tools"]) == ["custom:submit_guidance"]
    assert options["enable_session_store"] is False
    assert options["infinite_sessions"] == {"enabled": False}
    assert options["memory"] == {"enabled": False}
    payload, attachments = client.sessions[0].sent
    body = json.loads(payload)
    assert body["untrustedObservation"]["ocrText"] == "untrusted screen text"
    assert "imageBase64" not in payload and "box" not in body["serverApproved"]["targets"][0]
    assert attachments == [
        {
            "type": "blob",
            "data": observation(image=True).imageBase64,
            "mimeType": "image/png",
            "displayName": "approved-observation.png",
        }
    ]
    assert client.sessions[1].sent[1] == []


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
async def test_invalid_or_unapproved_tool_result_fails_closed(tmp_path, changes):
    output = {
        "observationId": "obs-1",
        "stepId": "step-2",
        "targetId": None,
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
    assert client.sessions[0].disconnected
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
    assert client.sessions[0].disconnected
    await model.close()


@pytest.mark.asyncio
async def test_agency_mcp_is_opt_in_exact_and_read_only(tmp_path):
    output = {
        "observationId": "obs-1",
        "stepId": "step-2",
        "targetId": None,
        "instruction": "Verify the visible Microsoft setting.",
        "citationIds": ["learn-1"],
    }
    clients = []

    def factory(**kwargs):
        client = FakeClient(output, **kwargs)
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
