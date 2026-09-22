from importlib.metadata import version
from pathlib import Path
import re
import sys
import sysconfig

from packaging.requirements import Requirement
from packaging.utils import canonicalize_name
import pytest

from scripts.lock_python_dependencies import main, render_requirements


def test_lock_rendering_keeps_hashes_not_mirror_urls():
    wheel = {"url": "https://private-mirror.invalid/package.whl",
             "hashes": {"sha256": "a" * 64}}
    lock = {"lock-version": "1.0", "packages": [
        {"name": "Example_Package", "version": "1.2.3", "wheels": [wheel, wheel]}
    ]}
    rendered = render_requirements(lock)
    assert "example-package==1.2.3" in rendered
    assert rendered.count("--hash=sha256:" + "a" * 64) == 1
    assert "https:" not in rendered and "private-mirror" not in rendered
    for change in ({"wheels": []}, {"name": "--index-url"},
                   {"version": "1.0\n--trusted-host=example.invalid"},
                   {"wheels": [{"hashes": {"sha256": "invalid"}}]}):
        with pytest.raises(ValueError):
            render_requirements({"lock-version": "1.0", "packages": [{**lock["packages"][0], **change}]})
    with pytest.raises(ValueError):
        render_requirements({**lock, "packages": lock["packages"] * 2})


@pytest.mark.parametrize("target,implementation", [
    ("win32", "cpython"),
    ("win-arm64", "cpython"),
    ("win-amd64", "pypy"),
])
def test_lock_generation_rejects_unsupported_interpreters_before_side_effects(
    monkeypatch, target, implementation
):
    monkeypatch.setattr(sys, "argv", ["lock_python_dependencies.py"])
    monkeypatch.setattr(sys, "platform", "win32")
    monkeypatch.setattr(sys, "version_info", (3, 11, 9))
    monkeypatch.setattr(sys.implementation, "name", implementation)
    monkeypatch.setattr(sysconfig, "get_platform", lambda: target)
    monkeypatch.setattr("scripts.lock_python_dependencies.subprocess.run",
                        lambda *args, **kwargs: pytest.fail("Unsupported interpreter invoked pip"))
    monkeypatch.setattr(Path, "write_text",
                        lambda *args, **kwargs: pytest.fail("Unsupported interpreter changed locks"))
    with pytest.raises(SystemExit, match="Windows x64 CPython 3.11"):
        main()


def test_checked_in_locks_match_manifests_and_installed_environment():
    root = Path(__file__).resolve().parents[1]
    locks = {}
    for name in ("requirements.lock.txt", "requirements-dev.lock.txt"):
        contents = (root / name).read_text(encoding="utf-8")
        locks[name] = dict(re.findall(r"^([a-z0-9-]+)==([^\s\\]+)", contents, re.MULTILINE))
        assert locks[name] and contents.count("--hash=sha256:") >= len(locks[name])
        assert "://" not in contents
    runtime, development = locks.values()
    assert runtime.items() <= development.items()
    for name, release in development.items():
        assert version(name) == release, f"Install requirements-dev.lock.txt: {name} differs from its lock"
    for file, locked in (("requirements.txt", runtime), ("requirements-dev.txt", development)):
        for line in (root / file).read_text(encoding="utf-8").splitlines():
            line = line.partition("#")[0].strip()
            if not line or line.startswith("-r "):
                continue
            requirement = Requirement(line)
            if requirement.marker is None or requirement.marker.evaluate():
                assert locked[canonicalize_name(requirement.name)] in requirement.specifier
