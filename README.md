# Siemens Engineering MCP

A Windows x64 Model Context Protocol (MCP) server for Siemens TIA Portal V21 and V21.1, WinCC Unified and PLCSIM Advanced V9. It connects an MCP client to a project and devices selected in a local workstation profile. The server uses stdio and returns structured JSON.

| Area | Capabilities |
| --- | --- |
| TIA Portal Openness | Discover the configured project and PLC, read an SCL block, preview and apply a body-only change, compile, save and reopen for readback. |
| WinCC Unified engineering | Discover connections, tags, screens and items. Preview and apply an external PLC tag binding on the `ProcessValue` property of an existing I/O field. Compile and read back the saved binding. |
| PLCSIM Advanced V9 | Discover instances, use the exact configured instance name, refresh symbolic tags, read values, write allowlisted simulated inputs and wait for an expected value. |
| WinCC Unified PC Runtime | Read permitted tags through local Open Pipe with value, quality and timestamps. |

The workstation profile starts in inspection mode. Engineering writes and simulator writes are enabled separately. PLC and HMI downloads are performed manually.

## Requirements

- Windows x64, a .NET 10 SDK with the ASP.NET Core shared framework, and .NET Framework 4.8.
- TIA Portal V21 and V21.1 with the matching Openness PublicAPI assemblies for the full build. Give the Windows account used by the MCP host Siemens Openness access.
- PLCSIM Advanced V9 x64 API for the simulation worker. A compatible local Unified PC Runtime is required for live Open Pipe checks.
- Python 3 for the regression scripts. Siemens products and licences are installed separately.

## Build and configure

From the repository root in PowerShell, build the workstation bundle and copy the inspection profile:

```powershell
.\scripts\Build-Workstation.ps1
Copy-Item .\samples\workstation.example.json .\samples\workstation.local.json
```

The default output path is `artifacts\Release\win-x64`. Use that in-place path only when no client is using the bundle. Disconnect clients that load that executable before rebuilding it in place.

When a client is already running, or for acceptance, publish a separate Release bundle to a new directory. The destination must not exist; the script stages beside it, verifies all five executables, then moves the staged directory into place:

```powershell
.\scripts\Build-Workstation.ps1 -OutputDirectory .\artifacts\Release\acceptance-20261125-0004
```

If the SDKs are installed outside the default directories, pass `-Tia21ApiDirectory`, `-Tia211ApiDirectory` and, when needed, `-PlcsimApiPath`. Keep each published bundle’s `workers` directory next to its `SiemensEngineeringMcp.exe`.

Edit `samples\workstation.local.json`:

- Set `tiaVersion` to `21.0` or `21.1` and select the corresponding `tiaApiDirectory` (`PublicAPI\V21\net48` or `PublicAPI\V21.1\net48`).
- Set `projectPath`, `plcPath` and `hmiPath` to your lab project and devices. Use drive-qualified local paths and an existing `fileRoot` for engineering file exchange.
- Leave `portalProcessId` null to let the worker open the file privately. Set it only to inspect the matching project already open in the specified TIA Portal process.
- Set `simApiPath` to the V9 x64 API DLL. Set `simInstance` to the exact name of a dedicated virtual PLC when testing simulation.
- Keep `allowEngineeringWrite` and `allowSimulationWrite` false for inspection. For changes, use a separate profile and a closed disposable project copy. Populate `writableTags` with exact simulated input names and `runtimeTagPrefixes` with the approved HMI tag prefixes.

## Connect a client

Use the examples as templates. Replace both placeholder paths with the full local path to the built host and your local profile.

| Client | Configuration |
| --- | --- |
| Visual Studio 2026 GitHub Copilot | Merge [the Copilot sample](samples/copilot.json) into the workspace `.vs\mcp.json`. Use Copilot Chat in Agent mode. |
| Cursor | Merge [the Cursor sample](samples/cursor.json) into the workspace `.cursor\mcp.json`. |
| Codex CLI | Register the executable as a stdio MCP server with arguments `--profile` and the full path to the local profile. |
| ChatGPT | Use a private Secure MCP Tunnel. Run [the tunnel setup script](samples/chatgpt-tunnel.ps1) with `-TunnelId` and your own tunnel ID. Optionally pass `-ServerExecutable` and `-WorkstationProfile`. Run the printed diagnostic and startup commands, confirm the client is polling successfully, and select the tunnel when adding the custom MCP server in ChatGPT. [Secure MCP Tunnel documentation](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) |

The repository also has example registrations for [Claude Code](samples/claude-code.json) and [Gemini CLI](samples/gemini.json). These have not been qualified for this release.

Start with `doctor`, then `engineering_discover`. For a read-only HMI check, use `hmi_discover` and `hmi_preview` and verify that the preview reports `changesApplied=false`. Keep credentials in a local secret store or the tunnel client's process environment. Local profiles, IDE registration files, logs, fixtures and tunnel state should stay outside the public repository.

## Regression checks

Against the default in-place Release host:

```powershell
python .\tests\protocol_smoke.py
python .\tests\journal_concurrency.py
```

Against a newly published bundle, pass the host executable:

```powershell
python .\tests\protocol_smoke.py .\artifacts\Release\acceptance-20261125-0004\SiemensEngineeringMcp.exe
python .\tests\journal_concurrency.py .\artifacts\Release\acceptance-20261125-0004\SiemensEngineeringMcp.exe
```

The protocol test checks MCP discovery, input schemas and inspection-mode write denials without contacting Siemens SDKs. The journal test loads the installed PLCSIM V9 API and checks 40 denied requests across two hosts, including polling and replay. It uses an unregistered simulator name and a tag outside the write allowlist.

## Offline write persistence

Prove MCP-authored PLC and HMI changes survive save and reopen on disposable fixture copies. The runner copies the version fixture into an ignored run directory, generates a write-enabled profile with `portalProcessId` unset, then executes preview → apply → compile → save → reopen → readback. Source fixtures are never modified. Simulation and Runtime are not used.

```powershell
$bundle = '.\artifacts\Release\acceptance-20261125-0004\SiemensEngineeringMcp.exe'
$profile = '.\session-state\files\workstation-v21-inspection.json'
$run = '.\session-state\files\write-persistence-20261125-0004-r2'

.\scripts\Run-OfflineWritePersistence.ps1 `
  -HostExecutable $bundle `
  -Profile $profile `
  -RunDirectory $run `
  -Version All
```

Defaults: V21 uses revision `21025` and HMI tag `McpVerify_21025`; V21.1 uses revision `21125` and HMI tag `McpVerify_21125`. Each version writes `write-persistence-report.json` under the run directory. Stop on the first failing step for that version.

Observed on 2026-11-25: publish to `artifacts\Release\acceptance-20261125-0004` succeeded while Cursor held the in-place host (PID 51672); all 42 in-place file hashes stayed unchanged; reusing that output path was rejected before copying; protocol smoke and journal concurrency both exited 0 against the new host; V21 and V21.1 offline write persistence both passed (`21025` / `McpVerify_21025`, `21125` / `McpVerify_21125`) with zero PLC and HMI compile errors.

## Workstation acceptance

Run independently on V21 and V21.1 with separate disposable projects. The fixture needs a cyclic SCL block, a Boolean test input and derived output, a PLC revision marker, and a Unified PC Runtime device with an S7 connection and an I/O field.

1. Read the SCL block and HMI binding. Preview a body-only SCL change and a binding to a uniquely named external HMI tag.
2. Apply both previews, compile the PLC and HMI, and check both compiler results before saving. Reopen the project and compare the persisted source and binding with the previews.
3. Close the engineering session. After the operator manually downloads both targets to the confirmed virtual PLC and compatible local Runtime, refresh the simulator tags and read the deployed revision and HMI verification tag.
4. Drive the approved simulated input `false → true → false`. At each step verify the PLC-derived output and the HMI tag value, quality and timestamp. Stop on a revision mismatch, bad quality or timeout.

Give each write, compile, save and reopen a fresh `requestId` UUID and poll `operation_status` before continuing. An identical request with the same UUID returns its recorded state. After an interruption, read back the target before retrying. The host holds a write lease on a changed target until the MCP session closes.

## Verification status

- **Passed:** V21 and V21.1 offline inspection, saved fixture readback and compile checks. Codex CLI completed V21 inspection, including HMI preview. Engineering worker SDK discovery resolves non-default Program Files installs.
- **Passed (2026-11-25):** separate Release publish to `artifacts\Release\acceptance-20261125-0004` while the Cursor in-place host stayed running; in-place hashes unchanged; existing output path rejected; all five executables present; protocol smoke and journal concurrency exited 0 against that host; offline write persistence passed on V21 (`21025` / `McpVerify_21025`) and V21.1 (`21125` / `McpVerify_21125`) with compile errors 0/0 and save/reopen readback.
- **Cursor V21 read-only acceptance passed:** Agent Chat ran `doctor`, `engineering_discover` and `hmi_preview` against the qualified Release bundle. Both write flags were false and the preview returned `changesApplied=false`.
- **Visual Studio 2026 Copilot blocked:** Agent Chat usage was exhausted. The Siemens MCP registration is ready for the three inspection calls once usage is available.
- **ChatGPT blocked:** The private Secure MCP Tunnel is configured for the qualified Release bundle. The tunnel client was stopped at the last check. Successful polling and the three ChatGPT tool results are still pending.
- **Still pending:** PLCSIM and Unified Runtime deployment checks, successful simulator-write replay, stale deployed revision and bad HMI quality checks.
