"""Synthetic MCP discovery and write-policy checks. No Siemens SDK calls."""
import json
from pathlib import Path
import subprocess
import sys


def check(executable, profile):
    with subprocess.Popen(
        [executable, "--profile", profile],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8",
    ) as client:
        def send(message):
            client.stdin.write(json.dumps({"jsonrpc": "2.0", **message}) + "\n")
            client.stdin.flush()

        def request(identifier, method, params):
            send({"id": identifier, "method": method, "params": params})
            while line := client.stdout.readline():
                reply = json.loads(line)
                if reply.get("id") == identifier:
                    if "error" in reply:
                        raise RuntimeError(reply["error"])
                    return reply["result"]
            raise RuntimeError("MCP process closed its output")

        try:
            request(1, "initialize", {
                "protocolVersion": "2025-11-25",
                "capabilities": {},
                "clientInfo": {"name": "workstation-smoke", "version": "1"},
            })
            send({"method": "notifications/initialized"})
            catalogue = {tool["name"]: tool for tool in request(2, "tools/list", {})["tools"]}
            expected = {"doctor", "engineering_discover", "plc_read", "plc_preview",
                        "hmi_discover", "hmi_preview", "sim_read", "sim_write",
                        "runtime_read", "operation_status", "engineering_save"}
            assert expected.issubset(catalogue), expected.difference(catalogue)
            assert "service" not in catalogue["sim_write"]["inputSchema"]["properties"]
            for name in ("sim_write", "sim_wait"):
                value_schema = catalogue[name]["inputSchema"]["properties"]["value"]
                assert isinstance(value_schema, dict), (name, value_schema)
                assert {variant.get("type") for variant in value_schema.get("anyOf", [])} == {
                    "boolean", "number", "string"
                }, (name, value_schema)
            request_id = "c45f684c-1246-4e8f-8655-ecd1be19dfef"
            for identifier, (name, arguments) in enumerate([
                ("engineering_save", {"requestId": request_id}),
                ("sim_write", {"requestId": request_id, "tag": "McpFixture.Input", "value": True}),
                ("runtime_read", {"tagName": "OutsideAllowedPrefix"}),
                ("operation_status", {"requestId": request_id}),
            ], 3):
                result = request(identifier, "tools/call", {"name": name, "arguments": arguments})
                assert result.get("isError"), (name, result)
            print("MCP discovery, input schema, write denial and unknown operation checks passed.")
        finally:
            client.stdin.close()
            try:
                client.wait(timeout=5)
            except subprocess.TimeoutExpired:
                client.kill()


if __name__ == "__main__":
    root = Path(__file__).resolve().parent.parent
    host = root / "artifacts" / "Release" / "win-x64" / "SiemensEngineeringMcp.exe"
    sample = root / "samples" / "workstation.example.json"
    check(str(host if len(sys.argv) == 1 else Path(sys.argv[1]).resolve()), str(sample))
