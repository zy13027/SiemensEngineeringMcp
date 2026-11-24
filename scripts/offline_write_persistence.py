"""Offline MCP write persistence: preview → apply → compile → save → reopen → readback."""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
REVISION_PATTERN = re.compile(
    r'("MCP_Verification"\.Revision\s*:=\s*)\d+(\s*;)',
    re.IGNORECASE,
)

FIXTURES = {
    "21.0": {
        "source": ROOT / "session-state" / "fixtures" / "V21.0" / "SiemensEngineeringMcp_V210",
        "projectFile": "SiemensEngineeringMcp_V210.ap21",
        "projectName": "SiemensEngineeringMcp_V210",
        "defaultRevision": 21025,
        "defaultTag": "McpVerify_21025",
    },
    "21.1": {
        "source": ROOT / "session-state" / "fixtures" / "V21.1" / "SiemensEngineeringMcp_V211",
        "projectFile": "SiemensEngineeringMcp_V211.ap21_1",
        "projectName": "SiemensEngineeringMcp_V211",
        "defaultRevision": 21125,
        "defaultTag": "McpVerify_21125",
    },
}


def resolve_tia_api_directory(relative: Path, template_directory) -> str:
    roots = []
    if template_directory:
        template_path = Path(template_directory)
        for parent in template_path.parents:
            if parent.name == "Program Files":
                roots.append(parent)
                break
    roots.extend(
        Path(root)
        for root in (
            Path(os.environ.get("ProgramFiles", r"C:\Program Files")),
            Path(r"F:\Program Files"),
            Path(r"D:\Program Files"),
            Path(r"E:\Program Files"),
        )
    )
    seen = set()
    for root in roots:
        key = str(root).lower()
        if key in seen:
            continue
        seen.add(key)
        candidate = root / relative
        if (candidate / "Siemens.Engineering.Base.dll").is_file():
            return str(candidate)
    raise RuntimeError(f"Could not locate TIA PublicAPI directory for {relative}")


def payload(result):
    return {
        "isError": result.get("isError", False),
        "data": result.get("structuredContent", result),
        "content": result.get("content"),
    }


def with_revision(source: str, revision: int) -> str:
    updated, count = REVISION_PATTERN.subn(rf"\g<1>{revision}\g<2>", source, count=1)
    if count != 1:
        raise RuntimeError("Could not locate exactly one MCP_Verification.Revision assignment in Main.")
    if updated == source:
        raise RuntimeError(f"Main already uses revision {revision}; choose a new revision.")
    return updated


def compile_errors(operation) -> int:
    data = operation["final"]["data"]
    if operation["final"]["isError"] or data.get("status") != "succeeded":
        raise RuntimeError(json.dumps(data, ensure_ascii=False))
    return int(data["data"]["errors"])


OB1_TEMPS = (
    "OB1_EV_CLASS",
    "OB1_STRT_INF",
    "OB1_PRIORITY",
    "OB1_OB_NUMBR",
    "OB1_RESERVED_1",
    "OB1_RESERVED_2",
    "OB1_PREV_CYCLE",
    "OB1_MIN_CYCLE",
    "OB1_MAX_CYCLE",
    "OB1_DATE_TIME",
)


def assert_declaration_preserved(before: str, after: str) -> dict:
    """TIA may re-export MLC annotations after import; require the OB1 interface to remain."""
    if "ORGANIZATION_BLOCK \"Main\"" not in after:
        raise RuntimeError("Persisted declaration is missing ORGANIZATION_BLOCK \"Main\"")
    missing = [name for name in OB1_TEMPS if name not in after]
    if missing:
        raise RuntimeError(f"Persisted declaration lost temp members: {', '.join(missing)}")
    return {
        "exactTextMatch": before == after,
        "organizationBlockPresent": True,
        "ob1TempsPreserved": True,
        "note": None
        if before == after
        else "TIA re-export added metadata; original OB1 temp interface preserved.",
    }


def assert_hmi(readback, *, tag_name: str):
    data = readback["data"]
    binding = data.get("binding") or {}
    tag = data.get("tag") or {}
    checks = {
        "screenBindingTag": binding.get("tag") == tag_name,
        "tagName": tag.get("name") == tag_name,
        "tagTable": data.get("tagTablePath") == "MCP_Verification",
        "plcTag": tag.get("plcTag") == "MCP_Verification.Output",
        "connection": tag.get("connection") == "HMI_Connection_1",
        "plcName": tag.get("plcName") == "PLC_1",
        "tagType": tag.get("tagType") == "Simple",
        "property": data.get("property") == "ProcessValue",
    }
    failed = [name for name, ok in checks.items() if not ok]
    if failed:
        raise RuntimeError(f"HMI readback checks failed: {', '.join(failed)}")
    return checks


def run_version(args) -> dict:
    meta = FIXTURES[args.version]
    revision = args.revision if args.revision is not None else meta["defaultRevision"]
    tag_name = args.hmi_tag if args.hmi_tag else meta["defaultTag"]
    run_dir = Path(args.run_directory).resolve()
    run_dir.mkdir(parents=True, exist_ok=True)

    version_dir = run_dir / f"V{args.version.replace('.', '')}"
    if version_dir.exists():
        raise RuntimeError(f"Run directory already contains '{version_dir}'. Choose a new run directory.")

    fixture_copy = version_dir / meta["source"].name
    shutil.copytree(meta["source"], fixture_copy)
    project_path = fixture_copy / meta["projectFile"]
    if not project_path.is_file():
        raise RuntimeError(f"Fixture copy is missing project file: {project_path}")

    template = json.loads(Path(args.profile).read_text(encoding="utf-8"))
    api_relative = {
        "21.0": Path("Siemens") / "Automation" / "Portal V21" / "PublicAPI" / "V21" / "net48",
        "21.1": Path("Siemens") / "Automation" / "Portal V21.1" / "PublicAPI" / "V21.1" / "net48",
    }[args.version]
    api_directory = resolve_tia_api_directory(api_relative, template.get("tiaApiDirectory"))
    profile = {
        **template,
        "tiaVersion": args.version,
        "tiaApiDirectory": api_directory,
        "projectPath": str(project_path),
        "portalProcessId": None,
        "plcPath": template.get("plcPath") or "PLC_1",
        "hmiPath": template.get("hmiPath") or "PC_1",
        "fileRoot": str(version_dir / "exchange"),
        "allowEngineeringWrite": True,
        "allowSimulationWrite": False,
        "writableTags": [],
        "runtimeTagPrefixes": template.get("runtimeTagPrefixes") or ["McpVerify_"],
        "simInstance": "UNCONFIRMED_DO_NOT_CONNECT",
    }

    (version_dir / "exchange").mkdir(parents=True, exist_ok=True)
    profile_path = version_dir / "profile.write.json"
    profile_path.write_text(json.dumps(profile, indent=2), encoding="utf-8")
    report_path = version_dir / "write-persistence-report.json"

    report = {
        "version": args.version,
        "revision": revision,
        "hmiTag": tag_name,
        "host": str(Path(args.host).resolve()),
        "inspectionProfile": str(Path(args.profile).resolve()),
        "generatedProfile": str(profile_path),
        "projectPath": str(project_path),
        "sourceFixture": str(meta["source"]),
        "runDirectory": str(version_dir),
        "operationIds": {},
        "passed": False,
    }

    host = Path(args.host).resolve()
    with subprocess.Popen(
        [str(host), "--profile", str(profile_path)],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        bufsize=1,
    ) as client:
        sequence = 0

        def send(message):
            client.stdin.write(json.dumps({"jsonrpc": "2.0", **message}) + "\n")
            client.stdin.flush()

        def request(method, params):
            nonlocal sequence
            sequence += 1
            identifier = sequence
            send({"id": identifier, "method": method, "params": params})
            for line in client.stdout:
                reply = json.loads(line)
                if reply.get("id") == identifier:
                    if "error" in reply:
                        raise RuntimeError(json.dumps(reply["error"], ensure_ascii=False))
                    return reply["result"]
            raise RuntimeError("MCP host closed stdout before responding")

        def call(name, arguments=None):
            return payload(request("tools/call", {"name": name, "arguments": arguments or {}}))

        def operation(name, arguments=None, timeout_s=300):
            request_id = str(uuid.uuid4())
            started = call(name, {**(arguments or {}), "requestId": request_id})
            status = started
            deadline = time.monotonic() + timeout_s
            while not status["isError"] and status["data"].get("status") == "running":
                if time.monotonic() >= deadline:
                    raise TimeoutError(f"{name} timed out for {request_id}")
                time.sleep(0.25)
                status = call("operation_status", {"requestId": request_id})
            return {"requestId": request_id, "start": started, "final": status}

        try:
            report["initialize"] = request(
                "initialize",
                {
                    "protocolVersion": "2025-11-25",
                    "capabilities": {},
                    "clientInfo": {"name": "offline-write-persistence", "version": "1"},
                },
            )
            send({"method": "notifications/initialized"})

            doctor = call("doctor")
            discover = call("engineering_discover")
            report["doctor"] = doctor
            report["engineering_discover"] = discover
            if doctor["isError"] or discover["isError"]:
                raise RuntimeError("doctor or engineering_discover failed")

            doctor_data = doctor["data"]
            discover_data = discover["data"]
            if doctor_data.get("tiaVersion") != args.version:
                raise RuntimeError(
                    f"SDK/version mismatch: doctor reported {doctor_data.get('tiaVersion')}, expected {args.version}"
                )
            if Path(discover_data.get("projectPath", "")).resolve() != project_path.resolve():
                raise RuntimeError(
                    f"Project identity mismatch: discovered {discover_data.get('projectPath')}, expected {project_path}"
                )
            if discover_data.get("projectName") != meta["projectName"]:
                raise RuntimeError(
                    f"Project name mismatch: discovered {discover_data.get('projectName')}, expected {meta['projectName']}"
                )

            plc_read = call("plc_read", {"blockPath": "Main"})
            report["plc_read_before"] = plc_read
            if plc_read["isError"]:
                raise RuntimeError("plc_read(Main) failed before edits")
            before_source = plc_read["data"]["source"]
            before_declaration = plc_read["data"].get("declaration")
            proposed_source = with_revision(before_source, revision)

            plc_preview = call("plc_preview", {"blockPath": "Main", "source": proposed_source})
            report["plc_preview"] = plc_preview
            if plc_preview["isError"]:
                raise RuntimeError("plc_preview failed")

            hmi_preview = call(
                "hmi_preview",
                {
                    "screenPath": "MCP_Verification",
                    "itemName": "PLC_Output",
                    "property": "ProcessValue",
                    "tagName": tag_name,
                    "plcTag": "MCP_Verification.Output",
                    "connectionName": "HMI_Connection_1",
                    "tagTablePath": "MCP_Verification",
                },
            )
            report["hmi_preview"] = hmi_preview
            if hmi_preview["isError"]:
                raise RuntimeError("hmi_preview failed")

            plc_apply = operation("plc_apply", {"previewId": plc_preview["data"]["previewId"]})
            hmi_apply = operation("hmi_apply", {"previewId": hmi_preview["data"]["previewId"]})
            report["plc_apply"] = plc_apply
            report["hmi_apply"] = hmi_apply
            report["operationIds"]["plc_apply"] = plc_apply["requestId"]
            report["operationIds"]["hmi_apply"] = hmi_apply["requestId"]
            for label, op in (("plc_apply", plc_apply), ("hmi_apply", hmi_apply)):
                final = op["final"]["data"]
                if op["final"]["isError"] or final.get("status") != "succeeded":
                    raise RuntimeError(f"{label} failed: {json.dumps(final, ensure_ascii=False)}")

            plc_compile = operation("plc_compile", timeout_s=600)
            hmi_compile = operation("hmi_compile", timeout_s=600)
            report["plc_compile"] = plc_compile
            report["hmi_compile"] = hmi_compile
            report["operationIds"]["plc_compile"] = plc_compile["requestId"]
            report["operationIds"]["hmi_compile"] = hmi_compile["requestId"]
            plc_errors = compile_errors(plc_compile)
            hmi_errors = compile_errors(hmi_compile)
            report["compiler"] = {"plcErrors": plc_errors, "hmiErrors": hmi_errors}
            if plc_errors != 0 or hmi_errors != 0:
                raise RuntimeError(f"Compile reported errors (PLC={plc_errors}, HMI={hmi_errors}); save skipped.")

            save = operation("engineering_save", timeout_s=600)
            reopen = operation("engineering_reopen", timeout_s=600)
            report["engineering_save"] = save
            report["engineering_reopen"] = reopen
            report["operationIds"]["engineering_save"] = save["requestId"]
            report["operationIds"]["engineering_reopen"] = reopen["requestId"]
            for label, op in (("engineering_save", save), ("engineering_reopen", reopen)):
                final = op["final"]["data"]
                if op["final"]["isError"] or final.get("status") != "succeeded":
                    raise RuntimeError(f"{label} failed: {json.dumps(final, ensure_ascii=False)}")

            plc_after = call("plc_read", {"blockPath": "Main"})
            hmi_after = call(
                "hmi_read",
                {
                    "screenPath": "MCP_Verification",
                    "itemName": "PLC_Output",
                    "property": "ProcessValue",
                    "tagName": tag_name,
                },
            )
            report["plc_read_after"] = plc_after
            report["hmi_read_after"] = hmi_after
            if plc_after["isError"] or hmi_after["isError"]:
                raise RuntimeError("Readback after reopen failed")

            after_source = plc_after["data"]["source"]
            after_declaration = plc_after["data"].get("declaration")
            revision_match = REVISION_PATTERN.search(after_source)
            if not revision_match:
                raise RuntimeError("Persisted Main no longer contains MCP_Verification.Revision")
            persisted_revision = int(re.search(r"\d+", revision_match.group(0).split(":=", 1)[1]).group(0))
            if persisted_revision != revision:
                raise RuntimeError(f"Persisted revision was {persisted_revision}, expected {revision}")
            if not re.search(
                r'"MCP_Verification"\s*\.\s*Output\s*:=\s*"MCP_Verification"\s*\.\s*Input\s*;',
                after_source,
            ):
                raise RuntimeError("Persisted Main lost the Output := Input assignment")

            declaration_checks = assert_declaration_preserved(before_declaration or "", after_declaration or "")
            hmi_checks = assert_hmi(hmi_after, tag_name=tag_name)
            report["readbackChecks"] = {
                "revision": persisted_revision,
                "outputLogicPreserved": True,
                "declaration": declaration_checks,
                "hmi": hmi_checks,
            }
            report["passed"] = True
        except Exception as exc:
            report["failure"] = f"{type(exc).__name__}: {exc}"
        finally:
            try:
                client.stdin.close()
                client.wait(timeout=60)
            except Exception:
                client.kill()
            report["hostExitCode"] = client.returncode
            report["stderr"] = client.stderr.read()

    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({"reportPath": str(report_path), "passed": report.get("passed"), "failure": report.get("failure")}, indent=2))
    if not report.get("passed"):
        raise SystemExit(1)
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, help="Published SiemensEngineeringMcp.exe")
    parser.add_argument("--profile", required=True, help="Local inspection profile used as a template")
    parser.add_argument("--run-directory", required=True, help="Ignored local directory for copies and evidence")
    parser.add_argument("--version", required=True, choices=sorted(FIXTURES))
    parser.add_argument("--revision", type=int, help="Override the default revision for this version")
    parser.add_argument("--hmi-tag", help="Override the default HMI tag name for this version")
    args = parser.parse_args(argv)
    run_version(args)


if __name__ == "__main__":
    main()
