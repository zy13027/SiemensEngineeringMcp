"""Exercise operation journal updates while polling; never attach to a simulator."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import uuid

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_HOST = ROOT / "artifacts" / "Release" / "win-x64" / "SiemensEngineeringMcp.exe"
STATE = Path(os.environ["LOCALAPPDATA"]) / "SiemensEngineeringMcp"


def main(host=DEFAULT_HOST):
	host = Path(host).resolve()
	instance = "MCP_NO_TARGET_" + uuid.uuid4().hex
	requests = []
	with tempfile.TemporaryDirectory(prefix="journal-test-", dir=ROOT / "tests") as temp:
		profile = json.loads((ROOT / "samples" / "workstation.example.json").read_text(encoding="utf-8"))
		profile.update(projectPath=str(Path(temp) / "absent.ap21"), fileRoot=temp,
					   simInstance=instance, allowSimulationWrite=True, writableTags=[])
		profile_path = Path(temp) / "profile.json"
		profile_path.write_text(json.dumps(profile), encoding="utf-8")
		try:
			with (subprocess.Popen([str(host), "--profile", str(profile_path)],
								   stdin=subprocess.PIPE, stdout=subprocess.PIPE,
								   stderr=subprocess.DEVNULL, text=True, encoding="utf-8") as client,
				  subprocess.Popen([str(host), "--profile", str(profile_path)],
								   stdin=subprocess.PIPE, stdout=subprocess.PIPE,
								   stderr=subprocess.DEVNULL, text=True, encoding="utf-8") as observer):
				sequence = 0

				def request(method, params, process=client):
					nonlocal sequence
					sequence += 1
					process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": sequence,
													"method": method, "params": params}) + "\n")
					process.stdin.flush()
					for line in process.stdout:
						reply = json.loads(line)
						if reply.get("id") == sequence:
							if "error" in reply:
								raise RuntimeError(reply["error"])
							return reply["result"]
					raise RuntimeError("MCP host closed its output")

				def call(name, arguments, process=client):
					return request("tools/call", {"name": name, "arguments": arguments}, process)

				try:
					for process in (client, observer):
						request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
											   "clientInfo": {"name": "journal-test", "version": "1"}}, process)
						process.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
						process.stdin.flush()
					for _ in range(40):
						request_id = str(uuid.uuid4())
						requests.append(uuid.UUID(request_id).hex)
						args = {"requestId": request_id, "tag": "NOT_ALLOWLISTED", "value": True}
						start = call("sim_write", args)
						assert start["structuredContent"].get("status") in ("running", "failed"), start
						deadline = time.monotonic() + 10
						while True:
							result = call("operation_status", {"requestId": request_id}, observer)
							status = result["structuredContent"].get("status")
							if status != "running":
								break
							if time.monotonic() >= deadline:
								raise TimeoutError(request_id)
						assert status == "failed", result
						assert "disabled by the profile" in result["structuredContent"]["error"], result
						replay = call("sim_write", args)
						assert replay["structuredContent"] == result["structuredContent"], replay
					print("40 denied operations retained their final status under cross-host polling and replay.")
				finally:
					for process in (client, observer):
						process.stdin.close()
						process.wait(timeout=15)
		finally:
			# Delete only files belonging to this run's unique request IDs and target.
			operations = STATE / "operations"
			if operations.exists():
				for request_id in requests:
					for record in operations.glob("*/" + request_id + ".json"):
						for suffix in (".json", ".json.tmp", ".prepare", ".running", ".record"):
							record.with_name(request_id + suffix).unlink(missing_ok=True)
						try:
							record.parent.rmdir()
						except OSError:
							pass
			lease_hash = hashlib.sha256(("sim:" + instance).upper().encode("utf-8")).hexdigest().upper()
			(STATE / "leases" / (lease_hash + ".lock")).unlink(missing_ok=True)


if __name__ == "__main__":
	import sys
	main(DEFAULT_HOST if len(sys.argv) == 1 else Path(sys.argv[1]).resolve())
