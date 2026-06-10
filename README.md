# NaiwaProxy

Windows desktop proxy client MVP inspired by v2rayN.

Current scope:
- Import `vmess://` sharing links.
- Persist VMess profiles locally.
- Generate Xray/V2Ray-compatible VMess outbound config.
- Start and stop a bundled core process.
- Enable or disable Windows system HTTP proxy.
- Publish as a self-contained Windows executable.
- Build an installer with Inno Setup.

## Development

```powershell
dotnet build
dotnet run
```

## Core runtime

Put `xray.exe` in:

```text
NaiwaProxy\cores\xray.exe
```

The generated config is written to:

```text
%APPDATA%\NaiwaProxy\config.json
```

## Publish

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\publish\win-x64
```

Install Inno Setup, then compile:

```powershell
iscc .\installer\NaiwaProxy.iss
```
