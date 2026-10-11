"""Black-box integration checks for the synthetic portfolio API."""
import json
from pathlib import Path
import socket
import subprocess
import time
import urllib.error
import urllib.request

with socket.socket() as probe:
    probe.bind(("127.0.0.1", 0))
    port = probe.getsockname()[1]
BASE = f"http://127.0.0.1:{port}"
PROJECT = Path(__file__).resolve().parent


def call(path, actor=None, method="GET", key=None):
    headers = {}
    if actor:
        headers["X-Actor-Id"] = actor
    if key:
        headers["Idempotency-Key"] = key
    req = urllib.request.Request(BASE + path, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=2) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as exc:
        return exc.code, json.load(exc) if exc.headers.get_content_type() == "application/json" else None


proc = subprocess.Popen(
    ["dotnet", "run", "--project", "TenantPortal.csproj", "--urls", BASE],
    cwd=PROJECT,
    stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT,
)
try:
    for _ in range(100):
        if proc.poll() is not None:
            raise RuntimeError("API exited before becoming healthy")
        try:
            if call("/health")[0] == 200:
                break
        except (OSError, TimeoutError):
            time.sleep(0.1)
    else:
        raise RuntimeError("API did not start")

    status, tasks = call("/tenants/alpha/tasks", "alice")
    assert status == 200 and {t["id"] for t in tasks} == {"A-101", "A-102"}
    assert call("/tenants/beta/tasks", "alice")[0] == 403
    assert call("/tenants/alpha/tasks")[0] == 403
    assert call("/tenants/alpha/tasks/B-201/sync", "alice", "POST", "x")[0] == 404
    assert call("/tenants/alpha/tasks/A-101/sync", "alice", "POST")[0] == 400

    status, drift = call("/tenants/alpha/reconciliation", "alice")
    assert status == 200 and {d["taskId"] for d in drift} == {"A-101", "A-102"}
    status, first = call("/tenants/alpha/tasks/A-101/sync", "alice", "POST", "request-1")
    assert status == 200 and first["replayed"] is False
    status, second = call("/tenants/alpha/tasks/A-101/sync", "alice", "POST", "request-1")
    assert status == 200 and second["replayed"] is True
    assert call("/tenants/alpha/tasks/A-102/sync", "alice", "POST", "request-1")[0] == 409
    status, drift = call("/tenants/alpha/reconciliation", "alice")
    assert status == 200 and [d["taskId"] for d in drift] == ["A-102"]
    assert call("/tenants/beta/reconciliation", "alice")[0] == 403
    print("PASS: tenant isolation, idempotency, conflict, and reconciliation")
finally:
    proc.terminate()
    try:
        proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        proc.kill()
