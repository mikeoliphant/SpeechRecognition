# Project guide for coding agents

<!-- Starter for a mixed C++/C# solution in Visual Studio 2026.
     Replace every TODO, and delete sections that don't apply. Shorter is better. -->

## Overview
TODO: One or two sentences on what this product does.

- `TODO/` - C++ projects (what they do)
- `TODO/` - C# projects (what they do)
- `TODO/` - tests
- Solution file: `TODO.slnx` (or `.sln`)

## Environment
- Windows, Visual Studio 2026. Start the agent from Visual Studio's Developer PowerShell terminal so `msbuild`, `cl` and `vstest.console` are on PATH.
- Target platform: TODO (x64 / ARM64). C++ standard: TODO. .NET version: TODO.

## Build
Use MSBuild for the whole solution. `dotnet build` cannot build C++ `.vcxproj` projects.

```
msbuild TODO.slnx -m -v:minimal -p:Configuration=Debug -p:Platform=x64
```

- C#-only change: `dotnet build path\to\Project.csproj`
- C++-only change: `msbuild path\to\Project.vcxproj -m -v:minimal -p:Configuration=Debug -p:Platform=x64`
- Write switches with a dash (`-p:`, `-m`), not a slash. Slash forms can be mangled as paths under Git Bash.
- Build the smallest project that covers the change, and finish with a full solution build if a shared header or interop boundary changed.
- If the build fails with a locked file (LNK1104, MSB3027), the app may be running under the debugger. Ask the developer instead of deleting output files or killing processes.

## Test
- C#: `dotnet test path\to\Tests.csproj --filter TODO`
- C++: TODO (for example `vstest.console.exe x64\Debug\Tests.dll`, or `ctest --test-dir build -C Debug`)
- Run the tests for the code you changed before calling work done. Report any failures you did not fix.

## Naming (C++ and C#)
Use the same rules in both languages:

- `PascalCase`: types, namespaces and public members
- `camelCase`: locals, parameters and private fields
- `I` + `PascalCase`: interfaces (in C++, abstract classes used as interfaces), for example `IWidgetStore`

## Braces (C++ and C#)
Put opening and closing braces on their own lines (Allman style):

```
if (isReady)
{
    Start();
}
```

## C++ conventions
- Style: TODO (link to `.clang-format`, or describe it).
- Prefer RAII and smart pointers. Do not add raw `new`/`delete` in new code.
- Adding a file to a `.vcxproj` project means updating both the `.vcxproj` and `.vcxproj.filters`; C++ projects do not pick up new files automatically.
- Keep changes to widely included headers to a minimum, since they trigger long rebuilds.

## C# conventions
- Follow `.editorconfig`. Nullable reference types: TODO (enabled / disabled).
- Use `async`/`await` through the call chain. Do not block with `.Result` or `.Wait()`.
- SDK-style projects include new `.cs` files automatically, so do not list them in the `.csproj`.

## C++/C# boundary
TODO: How the two sides talk (P/Invoke, C++/CLI, COM, or none).

- Any change to a native signature, struct layout or calling convention must be made on both sides in the same change.
- Check who owns and frees any memory that crosses the boundary.

## Do not touch without asking
- Generated files: `*.g.cs`, `*.Designer.cs`, TODO
- Solution and project settings, beyond adding the files a change needs
- Third-party code in `TODO/`
- NuGet or vcpkg dependency versions

## Working style
- Preserve each file's existing line endings and encoding.
- Keep changes focused. Do not reformat code you are not otherwise changing.
