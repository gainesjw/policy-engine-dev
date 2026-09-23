"""Build first. Exercises the real API and policy review client with temporary local data."""

import argparse
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
import json
import os
from pathlib import Path
import socket
import shutil
import subprocess
import sys
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
from unittest.mock import MagicMock, patch

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--fabric-root", type=Path, help="Also test inventories generated from sibling Fabric repositories")
args = parser.parse_args()
with socket.socket() as port_socket:
    port_socket.bind(("127.0.0.1", 0))
    port = port_socket.getsockname()[1]
base = f"http://127.0.0.1:{port}"
dll = ROOT / "src/Fabric.Policy.Api/bin/Debug/net8.0/Fabric.Policy.Api.dll"
env = dict(os.environ, ASPNETCORE_ENVIRONMENT="Production", ASPNETCORE_URLS=base,
           Authentication__ApiKey="local-smoke-test-key")


def post(payload, key="local-smoke-test-key", raw=False):
    data = payload if raw else json.dumps(payload).encode()
    request = Request(base + "/v1/evaluations", data=data, headers={
        "Content-Type": "application/json", "X-Policy-Key": key}, method="POST")
    try:
        with urlopen(request, timeout=10) as response:
            return response.status, json.load(response)
    except HTTPError as error:
        return error.code, None


with tempfile.TemporaryDirectory(prefix="fabric-policy-http-") as temporary:
    folder = Path(temporary)
    with (folder / "api.log").open("w+") as log:
        process = subprocess.Popen(["dotnet", str(dll)], cwd=ROOT, env=env, stdout=log, stderr=log)
        try:
            for _ in range(100):
                if process.poll() is not None:
                    log.seek(0)
                    raise RuntimeError(log.read())
                try:
                    with urlopen(base + "/health/ready", timeout=1) as response:
                        assert response.status == 200
                    break
                except (URLError, TimeoutError):
                    time.sleep(0.1)
            else:
                raise TimeoutError("API did not become ready")

            sample = json.loads((ROOT / "examples/evaluation.json").read_text())
            status, result = post(sample)
            assert status == 200 and all(i["approved"] for i in result["items"])
            assert "approved" not in result
            assert result["manifest"] == sample["manifest"]
            mixed = deepcopy(sample)
            mixed["manifest"]["customMetadata"] = {"release": "original"}
            mixed["manifest"]["items"][0]["files"] = {"source.tmdl": "hash"}
            mixed["manifest"]["items"][1]["governance"] = {"owner": "unapproved", "classification": "internal"}
            status, mixed_result = post(mixed)
            assert status == 200 and mixed_result["manifest"] == mixed["manifest"]
            assert sorted(i["approved"] for i in mixed_result["items"]) == [False, True]
            assert "approved" not in mixed_result
            capitalized = dict(sample)
            capitalized["Manifest"] = capitalized.pop("manifest")
            assert post(capitalized)[1]["manifest"] == sample["manifest"]
            assert post(sample, key="")[0] == 401
            assert post(sample, key="wrong")[0] == 401
            prod = dict(sample, workspaceId="33333333-3333-3333-3333-333333333333")
            assert all(not i["approved"] for i in post(prod)[1]["items"])
            for invalid in (None, {}, {"workspaceId": "not-a-guid"}, dict(sample, manifest=None),
                            dict(sample, dependencies=None), dict(sample, manifest={"schemaVersion": 2})):
                assert post(invalid)[0] == 400, invalid
            assert post(b'{"broken":', raw=True)[0] == 400
            assert post(b'{"padding":"' + b'x' * (2 * 1024 * 1024) + b'"}', raw=True)[0] == 413
            with ThreadPoolExecutor(max_workers=16) as pool:
                results = list(pool.map(post, [sample] * 64))
            assert all(status == 200 and all(i["approved"] for i in result["items"]) for status, result in results)
            print("PASS HTTP approval/denial, auth, validation, body limit and 64 concurrent client requests")

            release = folder / "release"
            (release / "inventory").mkdir(parents=True)
            (release / "config.json").write_text(json.dumps({"environments": {
                "dev": {"workspaceId": sample["workspaceId"]}, "prod": {"workspaceId": prod["workspaceId"]}}}))
            (release / "inventory/manifest.json").write_text(json.dumps(sample["manifest"]))
            (release / "inventory/dependencies.json").write_text(json.dumps(sample["dependencies"]))
            gate = [sys.executable, str(ROOT / "scripts/check_deployment.py"), "--release", str(release),
                    "--url", base, "--receipt", str(folder / "receipt.json")]
            gate_env = dict(os.environ, POLICY_API_KEY="local-smoke-test-key")
            for extra, expected in [(["--environment", "dev"], 0), (["--environment", "prod"], 0),
                                    (["--environment", "dev", "--policy-digest", "0" * 64], 1)]:
                completed = subprocess.run(gate + extra, env=gate_env, capture_output=True, text=True)
                assert completed.returncode == expected, completed.stdout + completed.stderr
            print("PASS policy review client approval, denial and pinned-policy mismatch")

            if args.fabric_root:
                sys.path.insert(0, str(args.fabric_root / "platform-dev/python"))
                from platform_fabric.inventory import generate

                for repository, workspace_id in [
                    ("analytics-dev", sample["workspaceId"]),
                    ("data-engineering-dev", "44444444-4444-4444-4444-444444444444")]:
                    consumer = args.fabric_root / repository
                    config = json.loads((consumer / ".fabric/config.json").read_text())
                    # Substitute only the unconfigured external targets in memory.
                    for external in config.get("externalDependencies", {}).values():
                        external["environments"] = deepcopy(sample["manifest"]["externalDependencies"]["engineering-lakehouse"]["environments"])
                    manifest, graph = generate(consumer, config)
                    status, result = post({"workspaceId": workspace_id, "manifest": manifest, "dependencies": graph})
                    assert status == 200 and all(i["approved"] for i in result["items"]), (status, result)
                    assert result["manifest"] == manifest
                    print(f"PASS generated {repository} inventory ({len(manifest['items'])} items)")

                    if repository == "data-engineering-dev":
                        from platform_fabric.inventory import read_json, write_json
                        from platform_fabric.release import build, verify
                        from platform_fabric.policy import review
                        from platform_fabric.deploy import deploy

                        source = folder / "engineering-source"
                        for item in manifest["items"]:
                            for name in item["files"]:
                                destination = source / name
                                destination.parent.mkdir(parents=True, exist_ok=True)
                                shutil.copy2(consumer / name, destination)
                        failed_id = next(i["logicalId"] for i in manifest["items"] if i["type"] == "Notebook")
                        config["itemOverrides"][failed_id] = {"owner": "unapproved"}
                        config["environments"]["dev"]["workspaceId"] = workspace_id
                        write_json(source / ".fabric/config.json", config)
                        reviewed_release = folder / "engineering-release"
                        review_path, deployment_path = folder / "review.json", folder / "deployment.json"
                        build(source, reviewed_release, {})
                        with patch.dict(os.environ, POLICY_API_KEY="local-smoke-test-key"):
                            reviewed = review(reviewed_release, "dev", base, review_path, {})
                        assert len(reviewed["manifest"]["items"]) == len(manifest["items"])
                        selected = {i["logicalId"] for i in reviewed["items"] if i["approved"]}
                        assert selected and failed_id not in selected
                        api, publisher = MagicMock(), MagicMock()
                        api.items.return_value = [dict(id=i["logicalId"], type=i["type"], displayName=i["name"])
                                                  for i in manifest["items"] if i["logicalId"] in selected]
                        with patch("platform_fabric.deploy.FabricAPI", return_value=api):
                            deploy(reviewed_release, "dev", deployment_path, {}, object(), publisher,
                                   policy_review=review_path)
                        assert {i["logicalId"] for i in read_json(deployment_path)["items"]} == selected
                        publisher.assert_called_once()
                        verify(reviewed_release)
                        print("PASS verified release → full-manifest HTTP review → selective deployment (Fabric mocked)")
        finally:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()

    no_key_env = dict(env)
    del no_key_env["Authentication__ApiKey"]
    # Process termination is expected; disable core dumps from the startup exception.
    import resource
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    no_key = subprocess.run(["dotnet", str(dll)], cwd=ROOT, env=no_key_env, capture_output=True, text=True, timeout=10)
    assert no_key.returncode != 0 and "Set Authentication__ApiKey" in no_key.stderr
    print("PASS Production startup rejects missing authentication configuration")
