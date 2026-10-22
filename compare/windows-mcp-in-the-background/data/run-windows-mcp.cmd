@echo off
rem Start pinned Windows-MCP 0.8.8 on 127.0.0.1:8765 with telemetry off. Meant to run inside the Anode seat (anode run / seat_run).
set ANONYMIZED_TELEMETRY=false
"%~dp0.venv\Scripts\windows-mcp.exe" serve --transport streamable-http --host 127.0.0.1 --port 8765 --tools ControlStatus,App,Type,Click,Screenshot,Snapshot --debug >> "%~dp0run\server.log" 2>&1
