# build/

Packaging target for Skyline Cadenza. Produces a `SkylineCadenza.zip` that can be installed via Skyline's `Tools > External Tools > Add` dialog.

## Usage

From the repo root:

```bash
dotnet build SkylineCadenza.sln -c Release
dotnet msbuild build/package.proj
```

The resulting zip lands at `publish/SkylineCadenza.zip`. Contents mirror the layout other Skyline tools use:

```
SkylineCadenza.exe
SkylineCadenza.Core.dll
... runtime DLLs ...
tool-inf/
  info.properties
  SkylineCadenza.properties
```

The `info.properties` declares the .NET 8 Desktop Runtime as a prerequisite, so Skyline will surface a clear error if the user's machine is missing it.

## Local install (smoke testing)

Run `tools/install-dev.ps1` (Windows only) to extract the publish output directly into
`%LOCALAPPDATA%\Apps\SkylineDaily\Tools\SkylineCadenza\` so a Skyline-daily restart picks it up without going through the zip+install loop.
