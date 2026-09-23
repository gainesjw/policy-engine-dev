"""Collect per-item policy reviews. Run AFTER platform_fabric verify, BEFORE pipeline selection."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
from urllib.parse import urlparse
from urllib.request import Request, build_opener, HTTPRedirectHandler


class NoRedirects(HTTPRedirectHandler):
    # Do not forward the policy key to a redirect target.
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release", type=Path, required=True)
    parser.add_argument("--environment", choices=("dev", "test", "prod"), required=True)
    parser.add_argument("--url", required=True, help="Policy API base URL")
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--policy-digest", help="Optional expected policy SHA-256 for coordinated rollout")
    args = parser.parse_args()
    url = urlparse(args.url)
    if url.scheme != "https" and not (url.scheme == "http" and url.hostname in {"127.0.0.1", "localhost", "::1"}):
        raise ValueError("Use HTTPS, or HTTP on loopback for local development")

    def read(relative):
        return json.loads((args.release / relative).read_text(encoding="utf-8-sig"))

    workspace_id = read("config.json")["environments"][args.environment]["workspaceId"]
    manifest = read("inventory/manifest.json")
    payload = json.dumps({"workspaceId": workspace_id, "manifest": manifest,
                          "dependencies": read("inventory/dependencies.json")}, separators=(",", ":")).encode()
    headers = {"Content-Type": "application/json"}
    if key := os.environ.get("POLICY_API_KEY"):
        headers["X-Policy-Key"] = key
    request = Request(args.url.rstrip("/") + "/v1/evaluations", data=payload, headers=headers, method="POST")
    with build_opener(NoRedirects).open(request, timeout=15) as response:
        if response.status != 200:
            raise ValueError("Policy API did not return HTTP 200")
        result = json.load(response)
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    args.receipt.write_text(json.dumps(result, indent=2) + "\n")
    expected_ids = sorted(i["logicalId"] for i in manifest["items"])
    checks = [
        str(result.get("workspaceId", "")).lower() == workspace_id.lower(),
        result.get("environment") == args.environment,
        result.get("manifest") == manifest,
        result.get("requestDigest") == hashlib.sha256(payload).hexdigest(),
        bool(expected_ids),
        sorted(i["logicalId"] for i in result.get("items", [])) == expected_ids,
        all(type(i.get("approved")) is bool and isinstance(i.get("findings"), list)
            for i in result.get("items", [])),
        bool(result.get("policyVersion")),
        len(result.get("policyDigest", "")) == 64,
        args.policy_digest is None or result.get("policyDigest") == args.policy_digest,
    ]
    if not all(checks):
        raise ValueError(f"Invalid policy review; inspect {args.receipt}")
    passing = sum(i["approved"] for i in result["items"])
    print(f"Reviewed {args.environment}: {passing} passing, {len(expected_ids) - passing} failing; pipeline selects deployment")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"Policy review failed: {error}", file=sys.stderr)
        sys.exit(1)
