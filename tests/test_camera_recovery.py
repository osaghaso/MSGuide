"""Deterministic camera-recovery state, evidence, and target-binding tests."""

from datetime import timedelta
import secrets

import pytest

from src.camera_recovery import CameraRecoveryEngine
from src.main import Config, create_app, now
from src.models import Observation
from tests.local_client import TestClient


PROFILE = "teams-camera-recovery-win11-24h2-en-US-fixture-v1"


@pytest.fixture
def client():
    token = secrets.token_urlsafe(32)
    with TestClient(
        create_app(Config(token=token)),
        base_url="http://localhost",
        headers={"Authorization": f"Bearer {token}"},
    ) as value:
        yield value


def element(role, label, automation_id, framework, toggle=None, enabled=True, box=None, target_id=None):
    value = {
        "role": role,
        "label": label,
        "automationId": automation_id,
        "frameworkId": framework,
        "isEnabled": enabled,
        "box": box or [0.1, 0.2, 0.3, 0.1],
        "confidence": 0.95,
    }
    if toggle is not None:
        value["toggleState"] = toggle
    if target_id is not None:
        value["targetId"] = target_id
    return value


def observation(obs_id, application, elements, offset_ms=0):
    return {
        "id": obs_id,
        "windowId": "camera-window",
        "application": application,
        "capturedAt": (now() + timedelta(milliseconds=offset_ms)).isoformat(),
        "width": 1280,
        "height": 720,
        "ocrText": "fixture evidence only",
        "elements": elements,
    }


def teams(obs_id, camera="off", *, blocked=True, settings=True, offset_ms=0):
    elements = [
        element("togglebutton", "Camera", "teams-prejoin-camera-toggle", "WebView2",
                camera, target_id=f"{obs_id}.camera"),
    ]
    if blocked:
        elements.append(element("status", "Your camera is blocked",
                                "teams-camera-permission-blocked", "WebView2",
                                target_id=f"{obs_id}.blocked"))
    if settings:
        elements.append(element("button", "Open camera settings",
                                "teams-open-camera-settings", "WebView2",
                                box=[0.5, 0.2, 0.3, 0.1], target_id=f"{obs_id}.settings"))
    return observation(obs_id, "Microsoft Teams", elements, offset_ms)


def windows_settings(obs_id, toggle=None, *, enabled=True, duplicate=False, offset_ms=0):
    elements = []
    if toggle is not None:
        elements.append(element("switch", "Let desktop apps access your camera",
                                "windows-camera-app-permission-toggle", "XAML",
                                toggle, enabled, target_id=f"{obs_id}.permission"))
        if duplicate:
            elements.append(element("switch", "Let desktop apps access your camera",
                                    "windows-camera-app-permission-toggle", "XAML",
                                    toggle, enabled, box=[0.5, 0.5, 0.3, 0.1],
                                    target_id=f"{obs_id}.permission2"))
    return observation(obs_id, "Settings", elements, offset_ms)


def request(session_id, observed, verification=None):
    camera = {"profile": PROFILE}
    if verification is not None:
        camera["verification"] = verification
    return {
        "sessionId": session_id,
        "prompt": "Recover my Teams camera",
        "consent": True,
        "observation": observed,
        "cameraRecovery": camera,
    }


def post(client, session_id, observed, verification=None):
    return client.post("/v1/guidance", json=request(session_id, observed, verification))


def start(client):
    return client.post("/v1/sessions").json()["sessionId"]


def advance_permission(client, session_id):
    blocked = teams("teams-blocked", offset_ms=0)
    assert post(client, session_id, blocked).json()["cameraRecovery"]["state"] == "camera_block_confirmed"
    off = windows_settings("settings-off", "off", offset_ms=1)
    assert post(client, session_id, off).json()["cameraRecovery"]["state"] == "user_action_required"
    on = windows_settings("settings-on", "on", offset_ms=2)
    response = post(client, session_id, on)
    assert response.status_code == 200
    assert response.json()["cameraRecovery"]["state"] == "applicable_permission_on"


def test_strict_sequence_requires_local_readiness_verifier(client):
    sid = start(client)
    blocked = post(client, sid, teams("teams-blocked", offset_ms=0))
    assert blocked.status_code == 200
    data = blocked.json()
    assert data["status"] == "next_step"
    assert data["cameraRecovery"] == {
        "profile": PROFILE,
        "fixtureSupported": True,
        "state": "camera_block_confirmed",
        "evidence": ["teams_prejoin_surface", "camera_toggle_off", "camera_block_indicator"],
        "permissionState": "unknown",
        "verificationRequired": True,
    }
    assert data["target"]["targetId"] == "teams-blocked.settings"
    assert data["target"]["automationId"] == "teams-open-camera-settings"
    assert data["target"]["frameworkId"] == "WebView2"
    assert data["target"]["isEnabled"] is True

    off = post(client, sid, windows_settings("settings-off", "off", offset_ms=1)).json()
    assert off["status"] == "next_step"
    assert off["cameraRecovery"]["state"] == "user_action_required"
    assert off["cameraRecovery"]["permissionState"] == "off"
    assert off["target"]["toggleState"] == "off"

    on = post(client, sid, windows_settings("settings-on", "on", offset_ms=2)).json()
    assert on["status"] == "clarification"
    assert on["cameraRecovery"]["state"] == "applicable_permission_on"
    assert on["cameraRecovery"]["verificationRequired"] is True
    assert on["target"] is None

    ready_observation = teams("teams-return", camera="on", blocked=False, settings=False, offset_ms=3)
    returned = post(client, sid, ready_observation).json()
    assert returned["status"] == "clarification"
    assert returned["cameraRecovery"]["state"] == "return_to_teams"
    assert returned["cameraRecovery"]["verificationRequired"] is True

    verification = {
        "kind": "localCameraReady",
        "source": "desktopLocalCameraVerifier",
        "evidenceId": "camera-verifier-1",
        "sessionId": sid,
        "observationId": ready_observation["id"],
        "windowId": ready_observation["windowId"],
        "capturedAt": ready_observation["capturedAt"],
        "cameraActive": True,
        "framesObserved": 3,
    }
    completed = post(client, sid, ready_observation, verification).json()
    assert completed["status"] == "completed"
    assert completed["cameraRecovery"]["state"] == "camera_ready_verified"
    assert completed["cameraRecovery"]["verificationRequired"] is False
    assert completed["cameraRecovery"]["evidence"][-1] == "local_camera_verifier"


def test_camera_path_never_calls_guidance_provider():
    async def forbidden_provider(prompt, observed):
        raise AssertionError("Camera scenario reached provider")

    app = create_app(Config(token="local-test"), guidance_provider=forbidden_provider)
    with TestClient(app, headers={"Authorization": "Bearer local-test"}) as client:
        response = post(client, start(client), teams("provider-bypass"))
        assert response.status_code == 200
        assert response.json()["cameraRecovery"]["state"] == "camera_block_confirmed"


@pytest.mark.parametrize(
    "observed,state",
    [
        (observation("other", "Other app", []), "unsupported"),
        (teams("already-on", camera="on", blocked=False, settings=False), "unsupported"),
        (teams("no-block", blocked=False), "teams_prejoin_observed"),
    ],
)
def test_wrong_cause_and_unsupported_surfaces_fail_closed(client, observed, state):
    data = post(client, start(client), observed).json()
    assert data["status"] == "clarification"
    assert data["target"] is None
    assert data["cameraRecovery"]["state"] == state


def test_settings_states_fail_closed(client):
    sid = start(client)
    post(client, sid, teams("blocked", offset_ms=0))

    missing = post(client, sid, windows_settings("missing", offset_ms=1)).json()
    assert missing["cameraRecovery"]["state"] == "camera_settings_open"
    assert missing["target"] is None

    unknown_enabled = windows_settings("unknown-enabled", "off", offset_ms=2)
    del unknown_enabled["elements"][0]["isEnabled"]
    data = post(client, sid, unknown_enabled).json()
    assert data["cameraRecovery"]["state"] == "applicable_permission_off"
    assert data["target"] is None

    managed = post(client, sid, windows_settings("managed", "off", enabled=False, offset_ms=3)).json()
    assert managed["cameraRecovery"]["state"] == "admin_managed"
    assert managed["cameraRecovery"]["permissionState"] == "managed"
    assert managed["target"] is None


def test_already_on_and_ambiguous_permission_do_not_advance(client):
    sid = start(client)
    post(client, sid, teams("blocked", offset_ms=0))
    already_on = post(client, sid, windows_settings("already-on", "on", offset_ms=1)).json()
    assert already_on["cameraRecovery"]["state"] == "unsupported"
    assert already_on["status"] == "clarification"

    ambiguous = post(client, sid, windows_settings("duplicate", "off", duplicate=True, offset_ms=2)).json()
    assert ambiguous["cameraRecovery"]["state"] == "ambiguous"
    assert ambiguous["target"] is None


def test_verification_is_strict_fresh_and_bound(client):
    sid = start(client)
    advance_permission(client, sid)
    observed = teams("ready", camera="on", blocked=False, settings=False, offset_ms=3)
    base = {
        "kind": "localCameraReady",
        "source": "desktopLocalCameraVerifier",
        "evidenceId": "verify-1",
        "sessionId": sid,
        "observationId": observed["id"],
        "windowId": observed["windowId"],
        "capturedAt": observed["capturedAt"],
        "cameraActive": True,
        "framesObserved": 2,
    }
    for field, value in [
        ("sessionId", "other-session"),
        ("observationId", "other-observation"),
        ("windowId", "other-window"),
        ("cameraActive", False),
        ("framesObserved", 1),
    ]:
        invalid = dict(base)
        invalid[field] = value
        assert post(client, sid, observed, invalid).status_code == 422
    stale = dict(base)
    stale["capturedAt"] = (now() - timedelta(seconds=61)).isoformat()
    assert post(client, sid, observed, stale).status_code == 422


def test_verifier_conflicting_with_camera_off_is_rejected(client):
    sid = start(client)
    advance_permission(client, sid)
    observed = teams("returned-off", camera="off", blocked=False, settings=False, offset_ms=3)
    verification = {
        "kind": "localCameraReady",
        "source": "desktopLocalCameraVerifier",
        "evidenceId": "verify-off",
        "sessionId": sid,
        "observationId": observed["id"],
        "windowId": observed["windowId"],
        "capturedAt": observed["capturedAt"],
        "cameraActive": True,
        "framesObserved": 2,
    }
    assert post(client, sid, observed, verification).status_code == 422


def test_later_permission_off_revokes_prior_permission_on_progress(client):
    sid = start(client)
    advance_permission(client, sid)
    reverted = post(client, sid, windows_settings("reverted-off", "off", offset_ms=3)).json()
    assert reverted["cameraRecovery"]["state"] == "user_action_required"
    teams_ready = teams("not-ready", camera="on", blocked=False, settings=False, offset_ms=4)
    data = post(client, sid, teams_ready).json()
    assert data["status"] == "clarification"
    assert data["cameraRecovery"]["state"] == "unsupported"


def test_target_binding_rejects_moved_mismatched_or_other_session_evidence():
    engine = CameraRecoveryEngine(b"x" * 32)
    observed = Observation.model_validate(teams("binding"))
    decision = engine.guide("session-1", observed, None)
    target = decision.guidance.target
    assert engine.target_matches("session-1", observed, target)
    moved_element = observed.elements[2].model_copy(update={"box": (0.4, 0.4, 0.2, 0.1)})
    moved = observed.model_copy(update={"elements": [*observed.elements[:2], moved_element]})
    assert not engine.target_matches("session-1", moved, target)
    assert not engine.target_matches("session-2", observed, target)


def test_server_generates_binding_id_for_legacy_element_without_target_id(client):
    sid = start(client)
    observed = teams("generated")
    del observed["elements"][2]["targetId"]
    target = post(client, sid, observed).json()["target"]
    assert target["targetId"].startswith("tgt.")


def test_wrong_automation_id_does_not_fall_back_to_english_label(client):
    sid = start(client)
    observed = teams("wrong-automation")
    observed["elements"][2]["automationId"] = "unrecognized-control"
    data = post(client, sid, observed).json()
    assert data["cameraRecovery"]["state"] == "camera_block_confirmed"
    assert data["target"] is None


@pytest.mark.parametrize(
    "mutate",
    [
        lambda body: body["observation"]["elements"][0].update(isEnabled="true"),
        lambda body: body["observation"]["elements"][0].update(toggleState="invalid"),
        lambda body: body["observation"]["elements"][0].update(unbounded="value"),
        lambda body: body["observation"]["elements"].append(
            dict(body["observation"]["elements"][0])
        ),
    ],
)
def test_new_evidence_fields_are_strict_and_bounded(client, mutate):
    sid = start(client)
    body = request(sid, teams("strict"))
    mutate(body)
    assert client.post("/v1/guidance", json=body).status_code == 422


def test_out_of_order_or_changed_observation_id_is_rejected(client):
    sid = start(client)
    first = teams("same-id", offset_ms=2)
    assert post(client, sid, first).status_code == 200
    changed = teams("same-id", offset_ms=3)
    changed["elements"][2]["box"] = [0.4, 0.4, 0.2, 0.1]
    assert post(client, sid, changed).status_code == 422
    older = teams("older-id")
    older["capturedAt"] = (now() - timedelta(seconds=1)).isoformat()
    assert post(client, sid, older).status_code == 422
