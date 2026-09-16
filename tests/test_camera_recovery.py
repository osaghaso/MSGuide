"""Deterministic camera-recovery state, evidence, and target-binding tests."""

from datetime import timedelta
import secrets

import pytest

from src.camera_recovery import CameraRecoveryEngine
from src.main import Config, create_app, now
from src.models import Observation
from tests.local_client import TestClient


PROFILE = "teams-camera-recovery-win11-24h2-en-US-fixture-v1"
LIVE_PROFILE = "teams-camera-recovery-new-teams-uia-probe-20260916-v1"
PINNED_PROFILE = "teams-camera-recovery-pinned-20260916-v2"


@pytest.fixture
def client():
    token = secrets.token_urlsafe(32)
    with TestClient(
        create_app(Config(token=token)),
        base_url="http://localhost",
        headers={"Authorization": f"Bearer {token}"},
    ) as value:
        yield value


def element(
    role, label, automation_id, framework, toggle=None, enabled=True, box=None,
    target_id=None, process_id=None, is_offscreen=None,
):
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
    if process_id is not None:
        value["processId"] = process_id
    if is_offscreen is not None:
        value["isOffscreen"] = is_offscreen
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


def request(session_id, observed, verification=None, profile=PROFILE):
    camera = {"profile": profile}
    if verification is not None:
        camera["verification"] = verification
    return {
        "sessionId": session_id,
        "prompt": "Recover my Teams camera",
        "consent": True,
        "observation": observed,
        "cameraRecovery": camera,
    }


def post(client, session_id, observed, verification=None, profile=PROFILE):
    return client.post(
        "/v1/guidance",
        json=request(session_id, observed, verification, profile),
    )


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
        "evidenceBasis": "fixture",
        "fixtureSupported": True,
        "settingsUiaProven": False,
        "rawPixelEvidenceUsed": False,
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


def test_live_teams_tree_allows_webview_process_different_from_selected_process(client):
    sid = start(client)
    observed = observation(
        "live-header",
        "Microsoft Teams",
        [
            element(
                "button",
                "Settings and more",
                "more-options-header",
                "WebView2",
                target_id="live.more",
                process_id=16836,
            )
        ],
    )
    observed["rootProcessId"] = 4444
    data = post(client, sid, observed, profile=LIVE_PROFILE).json()
    assert data["status"] == "next_step"
    assert data["target"]["targetId"] == "live.more"
    assert data["target"]["processId"] == 16836
    assert data["cameraRecovery"]["evidenceBasis"] == "liveProbe"
    assert data["cameraRecovery"]["fixtureSupported"] is False
    assert data["cameraRecovery"]["settingsUiaProven"] is False


def test_live_teams_settings_devices_navigation_is_probe_bounded(client):
    sid = start(client)
    settings_item = observation(
        "live-settings-item",
        "Microsoft Teams",
        [element("menuitem", "Settings", "settings", "WebView2", target_id="live.settings")],
    )
    settings = post(client, sid, settings_item, profile=LIVE_PROFILE).json()
    assert settings["cameraRecovery"]["state"] == "teams_settings_menu_open"
    assert settings["target"]["targetId"] == "live.settings"

    devices_tab = observation(
        "live-devices-tab",
        "Microsoft Teams",
        [element("tabitem", "Devices", "devices", "WebView2", target_id="live.devices")],
        offset_ms=1,
    )
    devices = post(client, sid, devices_tab, profile=LIVE_PROFILE).json()
    assert devices["cameraRecovery"]["state"] == "teams_settings_menu_open"
    assert devices["target"]["targetId"] == "live.devices"

    devices_page = observation(
        "live-devices-page",
        "Microsoft Teams",
        [
            element("group", "Audio settings", "AudioSettings", "WebView2",
                    target_id="live.audio"),
            element("group", "Video settings", "VideoSettings", "WebView2",
                    target_id="live.video"),
            element("button", "Open camera settings", "open_camera_settings", "WebView2",
                    target_id="live.open-camera"),
        ],
        offset_ms=2,
    )
    page = post(client, sid, devices_page, profile=LIVE_PROFILE).json()
    assert page["cameraRecovery"]["state"] == "teams_devices_open"
    assert page["cameraRecovery"]["evidence"][-1] == "open_system_camera_settings"
    assert page["target"]["targetId"] == "live.open-camera"


def test_live_windows_settings_uia_is_explicitly_unproven_and_untargeted(client):
    sid = start(client)
    observed = observation("settings-empty", "Settings", [])
    data = post(client, sid, observed, profile=LIVE_PROFILE).json()
    assert data["status"] == "clarification"
    assert data["target"] is None
    assert data["cameraRecovery"]["state"] == "system_camera_settings_uninspectable"
    assert data["cameraRecovery"]["settingsUiaProven"] is False
    assert data["cameraRecovery"]["evidence"] == [
        "system_camera_settings_surface",
        "uia_no_descendants",
    ]


def test_live_profile_cannot_claim_permission_or_completion(client):
    sid = start(client)
    observed = observation("settings-synthetic", "Settings", [
        element("switch", "Let desktop apps access your camera",
                "windows-camera-app-permission-toggle", "XAML", "on"),
    ])
    data = post(client, sid, observed, profile=LIVE_PROFILE).json()
    assert data["status"] == "clarification"
    assert data["cameraRecovery"]["state"] == "system_camera_settings_uninspectable"
    assert data["cameraRecovery"]["permissionState"] == "unknown"

    verification = {
        "kind": "localCameraReady",
        "source": "desktopLocalCameraVerifier",
        "evidenceId": "live-not-supported",
        "sessionId": sid,
        "observationId": observed["id"],
        "windowId": observed["windowId"],
        "capturedAt": observed["capturedAt"],
        "cameraActive": True,
        "framesObserved": 2,
    }
    assert post(client, sid, observed, verification, LIVE_PROFILE).status_code == 422


def pinned_settings(obs_id, teams_toggle, *, global_toggle="on", teams_enabled=True,
                    teams_offscreen=False, offset_ms=0):
    observed = observation(
        obs_id,
        "Settings",
        [
            element(
                "switch", "Camera access",
                "SystemSettings_CapabilityAccess_Camera_SystemGlobal_ToggleSwitch",
                "XAML", global_toggle, target_id=f"{obs_id}.system",
                process_id=9120, is_offscreen=False,
            ),
            element(
                "switch", "Let apps access your camera",
                "SystemSettings_CapabilityAccess_Camera_UserGlobal_ToggleSwitch",
                "XAML", global_toggle, target_id=f"{obs_id}.user",
                process_id=9120, is_offscreen=False,
            ),
            element(
                "switch", "Microsoft Teams Currently in use",
                "MSTeams_8wekyb3d8bbwe_ToggleSwitch",
                "XAML", teams_toggle, teams_enabled, target_id=f"{obs_id}.teams",
                process_id=9120, is_offscreen=teams_offscreen,
            ),
            element(
                "switch", "Let desktop apps access your camera",
                "SystemSettings_CapabilityAccess_Camera_ClassicGlobal_ToggleSwitch",
                "XAML", "on", target_id=f"{obs_id}.desktop",
                process_id=9120, is_offscreen=True,
            ),
        ],
        offset_ms,
    )
    observed["rootProcessId"] = 7000
    return observed


def test_pinned_settings_page_is_verified_before_targeting_golden_toggle(client):
    sid = start(client)
    off = post(
        client,
        sid,
        pinned_settings("pinned-off", "off"),
        profile=PINNED_PROFILE,
    ).json()
    assert off["status"] == "next_step"
    assert off["cameraRecovery"]["state"] == "user_action_required"
    assert off["cameraRecovery"]["settingsUiaProven"] is True
    assert off["cameraRecovery"]["rawPixelEvidenceUsed"] is False
    assert off["cameraRecovery"]["evidence"][:4] == [
        "system_camera_settings_surface",
        "camera_settings_page_verified",
        "system_camera_global_on",
        "user_camera_global_on",
    ]
    assert off["target"]["automationId"] == "MSTeams_8wekyb3d8bbwe_ToggleSwitch"
    assert off["target"]["processId"] == 9120
    assert off["target"]["isOffscreen"] is False

    on_observation = pinned_settings("pinned-on", "on", offset_ms=1)
    on = post(client, sid, on_observation, profile=PINNED_PROFILE).json()
    assert on["status"] == "clarification"
    assert on["cameraRecovery"]["state"] == "applicable_permission_on"
    assert on["cameraRecovery"]["permissionState"] == "on"

    teams_devices = observation(
        "pinned-teams-devices",
        "Microsoft Teams",
        [
            element("group", "Video settings", "VideoSettings", "WebView2",
                    target_id="pinned.video", process_id=16836),
            element("combobox", "Camera", "camera-selector", "WebView2",
                    target_id="pinned.camera", process_id=16836,
                    is_offscreen=False),
        ],
        offset_ms=2,
    )
    teams_devices["rootProcessId"] = 4444
    pending = post(client, sid, teams_devices, profile=PINNED_PROFILE).json()
    assert pending["status"] == "clarification"
    assert pending["cameraRecovery"]["state"] == "return_to_teams"
    verification = {
        "kind": "localCameraReady",
        "source": "desktopLocalCameraVerifier",
        "evidenceId": "pinned-preview-verified",
        "sessionId": sid,
        "observationId": teams_devices["id"],
        "windowId": teams_devices["windowId"],
        "capturedAt": teams_devices["capturedAt"],
        "cameraActive": True,
        "framesObserved": 3,
    }
    completed = post(
        client, sid, teams_devices, verification, PINNED_PROFILE
    ).json()
    assert completed["status"] == "completed"
    assert completed["cameraRecovery"]["state"] == "camera_ready_verified"
    assert completed["cameraRecovery"]["rawPixelEvidenceUsed"] is False


def test_pinned_deep_link_landing_must_be_verified_from_exact_page_ids(client):
    sid = start(client)
    settings_home = observation(
        "settings-home",
        "Settings",
        [element("button", "System", "SettingsPageSystem", "XAML")],
    )
    data = post(client, sid, settings_home, profile=PINNED_PROFILE).json()
    assert data["status"] == "clarification"
    assert data["target"] is None
    assert data["cameraRecovery"]["state"] == "system_camera_settings_unverified"

    empty = post(
        client,
        sid,
        observation("settings-empty-pinned", "Settings", [], offset_ms=1),
        profile=PINNED_PROFILE,
    ).json()
    assert empty["cameraRecovery"]["state"] == "system_camera_settings_uninspectable"


def test_pinned_exact_toggle_ids_do_not_depend_on_unprobed_framework_value(client):
    sid = start(client)
    observed = pinned_settings("no-framework", "off")
    for item in observed["elements"][:3]:
        del item["frameworkId"]
    data = post(client, sid, observed, profile=PINNED_PROFILE).json()
    assert data["status"] == "next_step"
    assert data["target"]["automationId"] == "MSTeams_8wekyb3d8bbwe_ToggleSwitch"


def test_pinned_wrong_cause_managed_and_visibility_states_fail_closed(client):
    sid = start(client)
    already_on = post(
        client, sid, pinned_settings("already-on-pinned", "on"),
        profile=PINNED_PROFILE,
    ).json()
    assert already_on["cameraRecovery"]["state"] == "unsupported"
    assert already_on["target"] is None

    sid = start(client)
    global_off = post(
        client, sid, pinned_settings("global-off", "off", global_toggle="off"),
        profile=PINNED_PROFILE,
    ).json()
    assert global_off["cameraRecovery"]["state"] == "unsupported"

    sid = start(client)
    managed = post(
        client, sid, pinned_settings("managed-pinned", "off", teams_enabled=False),
        profile=PINNED_PROFILE,
    ).json()
    assert managed["cameraRecovery"]["state"] == "admin_managed"
    assert managed["target"] is None

    sid = start(client)
    offscreen = post(
        client, sid, pinned_settings("offscreen-pinned", "off", teams_offscreen=True),
        profile=PINNED_PROFILE,
    ).json()
    assert offscreen["cameraRecovery"]["state"] == "ambiguous"
    assert offscreen["target"] is None

    sid = start(client)
    wrong_name = pinned_settings("wrong-name", "off")
    wrong_name["elements"][2]["label"] = "Another app"
    named = post(client, sid, wrong_name, profile=PINNED_PROFILE).json()
    assert named["cameraRecovery"]["state"] == "system_camera_settings_unverified"
    assert named["target"] is None


def test_profile_switch_cannot_reuse_fixture_permission_progress(client):
    sid = start(client)
    advance_permission(client, sid)
    devices = observation(
        "switched-profile",
        "Microsoft Teams",
        [
            element("group", "Video settings", "VideoSettings", "WebView2"),
            element("combobox", "Camera", "camera-selector", "WebView2",
                    is_offscreen=False),
            element("button", "Open camera settings", "open_camera_settings", "WebView2"),
        ],
        offset_ms=3,
    )
    data = post(client, sid, devices, profile=PINNED_PROFILE).json()
    assert data["status"] == "next_step"
    assert data["cameraRecovery"]["state"] == "teams_devices_open"
    assert data["cameraRecovery"]["settingsLaunchUri"] == "ms-settings:privacy-webcam"
    assert data["target"] is None


def test_pinned_devices_uses_verified_uri_flow_without_unrealized_button(client):
    sid = start(client)
    devices = observation(
        "stable-video-settings",
        "Microsoft Teams",
        [
            element(
                "group", "Video settings", "VideoSettings", "WebView2",
                target_id="stable.video", process_id=16836,
            ),
            element(
                "combobox", "Camera", "camera-selector", "WebView2",
                target_id="stable.camera", process_id=16836,
                is_offscreen=False,
            ),
        ],
    )
    devices["rootProcessId"] = 4444
    data = post(client, sid, devices, profile=PINNED_PROFILE).json()
    assert data["status"] == "next_step"
    assert data["target"] is None
    assert data["cameraRecovery"]["settingsLaunchUri"] == "ms-settings:privacy-webcam"
    assert data["cameraRecovery"]["evidence"] == [
        "teams_selected_window",
        "teams_devices_surface",
        "pinned_camera_settings_uri",
    ]


def test_unknown_camera_profile_is_rejected(client):
    sid = start(client)
    body = request(sid, teams("unknown-profile"))
    body["cameraRecovery"]["profile"] = "unprobed-profile"
    assert client.post("/v1/guidance", json=body).status_code == 422


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
        lambda body: body["observation"]["elements"][0].update(processId="16836"),
        lambda body: body["observation"]["elements"][0].update(isOffscreen="false"),
        lambda body: body["observation"].update(rootProcessId="4444"),
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
