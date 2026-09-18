"""Offline-only provider and PNG trust-boundary checks."""

import asyncio
import base64
from io import BytesIO
import json
import struct
import zlib

import httpx
import pytest

from src.images import MAX_BASE64_CHARS, MAX_IMAGE_BYTES, sanitize_png
from src.main import Config, create_app, now
from src.model_provider import MAX_RESPONSE_BYTES, ModelConfig, OpenAICompatibleProvider
from src.models import Observation
from tests.local_client import TestClient


def png(size=(2, 2), metadata=False):
    from PIL import Image, PngImagePlugin

    info = PngImagePlugin.PngInfo()
    if metadata:
        info.add_text("private", "never-upload-this-metadata")
    with Image.new("RGB", size, "red") as image:
        stream = BytesIO()
        image.save(stream, format="PNG", pnginfo=info)
        return stream.getvalue()


def encoded(raw):
    return base64.b64encode(raw).decode("ascii")


def observation(**changes):
    return {"id": "obs-1", "windowId": "window-1", "application": "Public sample",
            "capturedAt": now().isoformat(), "width": 2, "height": 2, "ocrText": "public test",
            "elements": [{"role": "button", "label": "First", "box": [0.1, 0.2, 0.3, 0.1], "confidence": 0.9},
                         {"role": "button", "label": "Second", "box": [0.5, 0.6, 0.2, 0.1],
                          "confidence": 0.8, "processId": 42, "targetId": "uia-second",
                          "automationId": "second-button", "frameworkId": "WPF",
                          "isEnabled": True, "isOffscreen": False, "targetable": True,
                          "isPassword": False, "action": "invoke"}],
            **changes}


def expected_target(element):
    return {key: element.get(key) for key in (
        "label", "box", "confidence", "processId", "targetId", "automationId",
        "frameworkId", "isEnabled", "isOffscreen", "toggleState", "action",
        "value", "scrollDirection", "valueHash",
    )}


def approved(**changes):
    # Reserved .invalid name, only used with injected MockTransport; never contacted.
    return ModelConfig(**{"url": "https://model.invalid/v1/chat/completions", "name": "test-model",
                          "api_key": "test-key", "allow_remote": True, **changes})


def reply(output=None, **message_fields):
    if output is None:
        output = {"instruction": "Select Second yourself.", "status": "next_step", "targetIndex": 1}
    return {"choices": [{"finish_reason": "stop", "message": {
        "role": "assistant", "content": json.dumps(output), **message_fields}}]}


def model_client(handler, config=None):
    config = config or approved()
    provider = OpenAICompatibleProvider(config, transport=httpx.MockTransport(handler))
    app = create_app(Config(token="local-test", guidance_provider="openai-compatible", model_config=config),
                     guidance_provider=provider)
    return TestClient(app, headers={"Authorization": "Bearer local-test"})


def request(client, obs=None):
    sid = client.post("/v1/sessions").json()["sessionId"]
    return client.post("/v1/guidance", json={"sessionId": sid, "consent": True,
                      "prompt": "Help with this public screen", "observation": obs or observation()})


@pytest.mark.parametrize("changes", [
    {"allow_remote": False}, {"url": ""}, {"name": ""}, {"api_key": ""},
    {"name": " "}, {"api_key": "secret\nvalue"}, {"auth_header": "custom"},
    {"url": "http://localhost/v1/chat/completions"}, {"url": "https://user:pass@model.invalid/chat"},
    {"url": "https://model.invalid/chat#fragment"}, {"url": "https://model.invalid/chat#"},
    {"url": "https://model.invalid"}, {"url": "https://model.invalid:bad/chat"},
    {"url": "https://model.invalid:0/chat"}, {"url": "https://model.invalid/\nchat"},
    {"url": "https://model.invalid\\evil/chat"},
])
def test_invalid_config_refuses_startup(changes):
    with pytest.raises(ValueError, match="Invalid or unapproved model configuration"):
        create_app(Config(guidance_provider="openai-compatible", model_config=approved(**changes)))


def test_env_gate_no_defaults(monkeypatch):
    for key in ("URL", "NAME", "API_KEY", "AUTH_HEADER"):
        monkeypatch.delenv("MSGUIDE_MODEL_" + key, raising=False)
    monkeypatch.delenv("MSGUIDE_ALLOW_REMOTE_MODEL", raising=False)
    config = ModelConfig.from_env()
    assert config.url == config.name == config.api_key == "" and not config.allow_remote
    for key, value in {"URL": approved().url, "NAME": "test-model", "API_KEY": "test-key"}.items():
        monkeypatch.setenv("MSGUIDE_MODEL_" + key, value)
    for gate in ("", "false", "1", "yes"):
        monkeypatch.setenv("MSGUIDE_ALLOW_REMOTE_MODEL", gate)
        with pytest.raises(ValueError):
            ModelConfig.from_env().validate()
    monkeypatch.setenv("MSGUIDE_ALLOW_REMOTE_MODEL", "true")
    ModelConfig.from_env().validate()
    monkeypatch.setenv("MSGUIDE_GUIDANCE_PROVIDER", "openai-compatible")
    monkeypatch.setenv("MSGUIDE_MODE", "demo")
    app = create_app()
    assert isinstance(app.state.guidance_provider, OpenAICompatibleProvider)
    assert "test-key" not in repr(ModelConfig.from_env())
    with pytest.raises(ValueError):
        create_app(Config(mode="enterprise"))


@pytest.mark.parametrize("auth", ["bearer", "api-key"])
def test_grounding_image_and_transport_settings(auth, monkeypatch, caplog):
    seen = []
    original = httpx.AsyncClient
    settings = []

    def client_factory(*args, **kwargs):
        if "trust_env" in kwargs:
            settings.append(kwargs)
        return original(*args, **kwargs)

    monkeypatch.setattr(httpx, "AsyncClient", client_factory)
    monkeypatch.setenv("HTTPS_PROXY", "http://unapproved.invalid:9999")

    def handler(req):
        seen.append(req)
        body = json.loads(req.content)
        assert body["model"] == "test-model" and body["stream"] is False
        assert not ({"tools", "functions", "tool_choice"} & set(body))
        assert [m["role"] for m in body["messages"]] == ["system", "user"]
        system = body["messages"][0]["content"]
        assert "untrusted" in system and "synthetic/public" in system and "0.8" in system
        content = body["messages"][1]["content"]
        image = base64.b64decode(content[0]["image_url"]["url"].split(",", 1)[1])
        assert b"never-upload-this-metadata" not in image
        data = json.loads(content[1]["text"])
        assert "imageBase64" not in data["untrustedObservation"]
        assert data["untrustedObservation"]["elements"][1]["label"] == "Second"
        if auth == "bearer":
            assert req.headers["Authorization"] == "Bearer test-key" and "api-key" not in req.headers
        else:
            assert req.headers["api-key"] == "test-key" and "Authorization" not in req.headers
        return httpx.Response(200, json=reply())

    with model_client(handler, approved(auth_header=auth)) as client:
        assert client.get("/health").json()["mode"] == "model"
        response = request(client, observation(imageBase64=encoded(png(metadata=True))))
        assert response.status_code == 200, response.text
        data = response.json()
        element = observation()["elements"][1]
        assert data["target"] == expected_target(element)
        assert data["mode"] == "model" and data["citations"] == []
        assert data["observationId"] == "obs-1" and data["windowId"] == "window-1"
        assert not client.app.state.runner.jobs
    assert len(seen) == 1
    assert settings[0]["trust_env"] is False and settings[0]["follow_redirects"] is False
    assert all(0 < value <= 10 for value in settings[0]["timeout"].as_dict().values())
    assert "test-key" not in caplog.text and "public test" not in caplog.text


@pytest.mark.parametrize("output", [
    {}, [], {"instruction": "x", "status": "bad"},
    {"instruction": "", "status": "next_step"}, {"instruction": " ", "status": "next_step"},
    {"instruction": 1, "status": "next_step"}, {"instruction": "x" * 3801, "status": "next_step"},
    *[{"instruction": "x", "status": "next_step", "targetIndex": index} for index in (-1, 2, True, "1", 1.0)],
    {"instruction": "x", "status": "completed", "targetIndex": 0},
    {"instruction": "x", "status": "clarification", "targetIndex": 0},
    *[{"instruction": "x", "status": "next_step", key: value} for key, value in
      (("mode", "demo"), ("citations", []), ("target", {"box": [0, 0, 1, 1]}), ("tools", []), ("url", "https://evil.invalid"))],
    {"instruction": "Visit https://evil.invalid", "status": "next_step"},
])
def test_invalid_output_fails_closed(output):
    with model_client(lambda req: httpx.Response(200, json=reply(output))) as client:
        response = request(client)
        assert response.status_code == 502
        assert response.json() == {"detail": "Invalid guidance result"}


@pytest.mark.parametrize("kind", ["malformed", "duplicate", "nan", "large-content", "large-envelope",
                                   "tools", "function", "refusal", "truncated", "many", "compressed"])
def test_invalid_envelope(kind):
    envelope = reply()
    message = envelope["choices"][0]["message"]
    if kind == "malformed":
        message["content"] = "private invalid json"
    elif kind == "duplicate":
        message["content"] = '{"instruction":"x","status":"completed","status":"next_step"}'
    elif kind == "nan":
        message["content"] = '{"instruction":"x","status":"next_step","targetIndex":NaN}'
    elif kind == "large-content":
        message["content"] = " " * 16385
    elif kind in {"tools", "function"}:
        message["tool_calls" if kind == "tools" else "function_call"] = {"name": "run_command"}
    elif kind == "refusal":
        message["refusal"] = "private refusal"
    elif kind == "truncated":
        envelope["choices"][0]["finish_reason"] = "length"
    elif kind == "many":
        envelope["choices"] *= 2
    elif kind == "large-envelope":
        envelope["padding"] = "x" * MAX_RESPONSE_BYTES
    def handler(req):
        if kind == "compressed":
            return httpx.Response(200, content=zlib.compress(json.dumps(envelope).encode()),
                                  headers={"Content-Encoding": "deflate"})
        return httpx.Response(200, json=envelope)
    with model_client(handler) as client:
        response = request(client)
        assert response.status_code == 502 and "private" not in response.text


@pytest.mark.parametrize("status", [301, 302, 307, 308, 401, 429, 500])
def test_no_redirect_retry_or_fallback(status):
    seen = []
    def handler(req):
        seen.append(req)
        return httpx.Response(status, headers={"Location": "https://unapproved.invalid/"}, content=b"private key error")
    with model_client(handler) as client:
        response = request(client)
        assert response.status_code == 502 and "private" not in response.text
    assert len(seen) == 1


@pytest.mark.parametrize("error", [httpx.ConnectTimeout, httpx.ReadTimeout, httpx.ConnectError])
def test_transport_errors_redacted(error):
    seen = []
    def handler(req):
        seen.append(req)
        raise error("private-screen-and-key", request=req)
    with model_client(handler) as client:
        response = request(client)
        assert response.status_code == (502 if error is httpx.ConnectError else 504)
        assert "private" not in response.text
    assert len(seen) == 1


def test_low_confidence_and_completion_suggestion():
    with model_client(lambda req: httpx.Response(200, json=reply())) as client:
        obs = observation()
        obs["elements"][1]["confidence"] = 0.799
        assert request(client, obs).status_code == 502
    for status in ("next_step", "clarification", "completed"):
        with model_client(lambda req: httpx.Response(200, json=reply({"instruction": "Check the visible result.", "status": status}))) as client:
            response = request(client, observation(elements=[], imageBase64=encoded(png())))
            assert response.status_code == 200
            data = response.json()
            assert data["target"] is None and data["citations"] == [] and data["mode"] == "model"
            assert data["status"] == ("completion_candidate" if status == "completed" else status)
            if status == "completed":
                assert "Suggested completion only" in data["instruction"]


def test_inert_message_metadata_is_accepted():
    with model_client(lambda req: httpx.Response(200, json=reply(
            annotations=[], tool_calls=None, function_call=None))) as client:
        assert request(client).status_code == 200


def test_uia_only_exact_target_and_confidence():
    with model_client(lambda req: httpx.Response(200, json=reply())) as client:
        result = request(client).json()
        assert result["target"] == expected_target(observation()["elements"][1])
        assert result["mode"] == "model" and result["citations"] == []
        obs = observation()
        obs["elements"][1]["confidence"] = 0.799
        assert request(client, obs).status_code == 502


def test_png_metadata_dimensions_and_demo():
    from PIL import Image

    original = encoded(png(metadata=True))
    clean = sanitize_png(original, 2, 2)
    with Image.open(BytesIO(base64.b64decode(clean))) as image:
        assert image.size == (2, 2) and not image.info
        assert image.getpixel((0, 0)) == (255, 0, 0, 255)
    assert "never-upload" not in str(base64.b64decode(clean))
    assert sanitize_png(clean, 2, 2) == clean
    with TestClient(create_app(Config(token="local-test")), headers={"Authorization": "Bearer local-test"}) as client:
        assert request(client, observation(imageBase64=original)).status_code == 200
        assert client.get("/health").json()["mode"] == "demo"
    assert Observation.model_validate(observation(width=16384, height=16384)).width == 16384
    assert sanitize_png(encoded(png((1600, 1600))), 1600, 1600)


@pytest.mark.parametrize("kind", ["empty", "bad-base64", "whitespace", "noncanonical", "too-many-chars",
                                   "too-many-bytes", "jpeg", "truncated", "corrupt", "animated", "wide", "tall", "mismatch", "bomb"])
def test_invalid_images_before_model_call(kind, monkeypatch):
    from PIL import Image

    raw = png()
    obs = observation()
    if kind == "empty":
        value = ""
    elif kind == "bad-base64":
        value = "private%%%"
    elif kind == "whitespace":
        value = encoded(raw) + "\n"
    elif kind == "noncanonical":
        value = "Zh=="
    elif kind == "too-many-chars":
        value = "A" * (MAX_BASE64_CHARS + 1)
    elif kind == "too-many-bytes":
        value = encoded(b"x" * (MAX_IMAGE_BYTES + 1))
    elif kind == "jpeg":
        stream = BytesIO()
        Image.new("RGB", (2, 2)).save(stream, format="JPEG")
        value = encoded(stream.getvalue())
    elif kind == "truncated":
        value = encoded(raw[:-20])
    elif kind == "corrupt":
        value = encoded(raw[:40] + bytes([raw[40] ^ 0xFF]) + raw[41:])
    elif kind == "animated":
        stream = BytesIO()
        Image.new("RGB", (2, 2), "red").save(stream, format="PNG", save_all=True,
                                               append_images=[Image.new("RGB", (2, 2), "blue")])
        value = encoded(stream.getvalue())
    elif kind in {"wide", "tall"}:
        size = (1601, 2) if kind == "wide" else (2, 1601)
        obs.update(width=size[0], height=size[1])
        value = encoded(png(size))
    else:
        value = encoded(raw)
        if kind == "mismatch":
            obs["width"] = 3
        else:
            monkeypatch.setattr(Image, "MAX_IMAGE_PIXELS", 3)
    obs["imageBase64"] = value
    with model_client(lambda req: pytest.fail("Invalid image reached transport")) as client:
        response = request(client, obs)
        assert response.status_code == 422 and "private" not in response.text


def test_exact_byte_boundary_and_revalidation():
    raw = png()
    # Legal private ancillary chunk makes a PNG exactly 2 MB without a text decompression bomb.
    data = b"x" * (MAX_IMAGE_BYTES - len(raw) - 12)
    chunk = struct.pack(">I", len(data)) + b"vpAg" + data + struct.pack(">I", zlib.crc32(b"vpAg" + data))
    raw = raw[:-12] + chunk + raw[-12:]
    assert len(raw) == MAX_IMAGE_BYTES and len(encoded(raw)) == MAX_BASE64_CHARS
    assert sanitize_png(encoded(raw), 2, 2)
    with model_client(lambda req: httpx.Response(200, json=reply())) as client:
        assert request(client, observation(imageBase64=encoded(raw))).status_code == 200
    broken = Observation.model_validate(observation()).model_copy(update={"imageBase64": "private malformed"})
    provider = OpenAICompatibleProvider(approved(), transport=httpx.MockTransport(lambda req: pytest.fail("Malformed image uploaded")))
    with pytest.raises(ValueError, match="Guidance provider failed"):
        asyncio.run(provider("help", broken))


def test_injected_provider_and_new_body_limit():
    async def injected(prompt, obs):
        return {"status": "clarification", "instruction": "injected"}
    app = create_app(Config(token="local-test"), guidance_provider=injected)
    assert app.state.guidance_provider is injected
    assert Config().max_body_bytes == 3_000_000
    with TestClient(app, headers={"Authorization": "Bearer local-test"}) as client:
        assert request(client).json()["instruction"] == "injected"
        assert client.post("/v1/guidance", content=b"x" * 3_000_001).status_code == 413


def test_response_stream_is_bounded_and_closed():
    class Body(httpx.AsyncByteStream):
        chunks = 0
        closed = False

        async def __aiter__(self):
            for _ in range(100):
                self.chunks += 1
                yield b"x" * 4096

        async def aclose(self):
            self.closed = True

    stream = Body()
    with model_client(lambda req: httpx.Response(200, stream=stream)) as client:
        assert request(client).status_code == 502
    assert stream.closed and stream.chunks == MAX_RESPONSE_BYTES // 4096 + 1