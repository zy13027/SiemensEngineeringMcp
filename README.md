# Siemens Engineering MCP

A standalone Windows x64 MCP server for TIA Portal V21 and V21.1, WinCC Unified engineering and PC Runtime, and PLCSIM Advanced V9. One workstation profile selects the engineering project, devices and disposable simulator. Tool calls return structured JSON and a text copy for clients that only display text.

## Build and configure

Use an installed .NET SDK capable of targeting .NET 10, the .NET 10 ASP.NET Core Runtime, .NET Framework 4.8, the two TIA PublicAPI SDKs and the PLCSIM Advanced V9 x64 SDK. Provision Siemens Openness access for the Windows user before attaching a project. The build uses local Siemens assemblies without packaging them. Licensed engineering and runtime checks require the relevant Siemens installations and licences.

```powershell
.\scripts\Build-Workstation.ps1
Copy-Item .\samples\workstation.example.json .\samples\workstation.local.json
```

Edit the local profile to point at the fixture project and a dedicated virtual PLC. Set both `tiaVersion` and `tiaApiDirectory` to their matching release. V21.1 uses its `PublicAPI\V21.1\net48` directory. An optional `--tia-version 21.0|21.1` overrides the profile version, and the directory must still match.

Use an explicitly selected Portal process ID for inspection of a project already open in the GUI. Leave that field null for a privately opened project. Use a separate write-enabled profile for an offline, disposable copy with the project closed in other applications. Enable `allowEngineeringWrite` and `allowSimulationWrite` independently. Place the permitted simulated input in `writableTags`. Give HMI verification tag names an explicit prefix allowed by `runtimeTagPrefixes`. Choose an existing local directory for `fileRoot`.

The build script emits `artifacts\Release\win-x64\SiemensEngineeringMcp.exe`. Keep its worker directories beside it.

## Client setup

| Client | Apply the sample configuration |
|---|---|
| Claude Code | Merge `samples/claude-code.json` into the workspace `.mcp.json`. |
| Cursor | Merge `samples/cursor.json` into `.cursor/mcp.json`. |
| GitHub Copilot in VS Code | Merge `samples/copilot.json` into `.vscode/mcp.json`. |
| Gemini CLI | Merge `samples/gemini.json` into `.gemini/settings.json`. |
| ChatGPT | Run `samples/chatgpt-tunnel.ps1 -TunnelId <id>` using an authorised Secure MCP Tunnel. Start the tunnel client on this workstation and select the tunnel when adding the custom MCP in ChatGPT. [OpenAI Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) |

Adjust the executable and profile paths in each sample. Run `doctor` first. It reports local prerequisites. Use `engineering_discover`, `sim_state` and `runtime_read` for live target checks. Keep the server session open during an engineering change so its private project and preview remain available.

## Workstation acceptance

Run this sequence on V21 and on V21.1 using separate project copies and a dedicated simulator. The fixture needs an existing SCL block called cyclically, a DB with a Boolean test input and a distinct derived output, a numeric revision value, a Unified PC device with an S7 connection and an existing screen with an I/O field. Seed a revision and known output rule before changes. Select a fresh revision number and HMI tag name for each run.

1. Use `engineering_discover`, `plc_blocks`, `plc_read` and `hmi_discover` to identify targets. For the PLC change, use the complete SIMATIC source document returned by `plc_read`. Change the implementation to compute a known output from the fixture input and publish the fresh revision value. Keep the block identity and declaration intact.
2. Call `plc_preview` and `hmi_preview`. For Unified, set the target property to `ProcessValue` on an existing I/O field and link an external HMI tag to the fixture output. Specify the HMI connection and, if required, its tag-table path. Inspect the previews before applying.
3. Submit `plc_apply`, `hmi_apply`, `plc_compile`, `hmi_compile` and `engineering_save` in sequence. Give each action a distinct UUID in `requestId` and poll `operation_status` until it completes before continuing. Check compiler error counts before saving. Reusing a UUID with the same operation and arguments retrieves that request. An interrupted operation requires target readback before a new request.
4. Call `engineering_reopen`, then `plc_read` and `hmi_read`. Compare the persisted source, the screen item's dynamic tag reference and the external tag's `PlcTag`, `Connection`, `PlcName` and `TagType` against the preview.
5. Close the MCP session to release the project, perform the PLC and HMI downloads manually, and reconnect the client. Call `sim_refresh`. Confirm the expected PLC revision with `sim_read`, and the uniquely named HMI tag with `runtime_read`.
6. Use `sim_write` with a fresh UUID per action to drive the fixture input false, true and false. After each action use `sim_wait` on the derived output and `runtime_read` on the corresponding HMI tag. Record both matching values and the HMI quality. Stop on a revision mismatch, bad quality or timeout. Screen rendering is outside this check.

Independently exercise wrong SDK selection, stale preview, invalid HMI binding, a competing writer, missing simulator instance, stale deployed revision and bad Runtime quality. From each client, discover the tools and configured target, preview a change and retrieve verification evidence. Run licensed bench checks after operator confirmation.

The MCP process holds a write lease for each project or simulator it has changed until that session closes. Operation results are stored under the current user's local app data. If the host exits during a write, a reconnect can retrieve the interrupted status and determine the outcome by reading the target.
