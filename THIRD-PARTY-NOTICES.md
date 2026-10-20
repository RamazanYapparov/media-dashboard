# Third-Party Notices

Media Dashboard is licensed under the [MIT License](LICENSE).

The source code in this repository has no third-party runtime dependencies. The release
binary `MediaDashboard.exe` is a self-contained single-file build and incorporates the
components listed below. They are distributed under their own license terms.

## .NET 10 runtime, WPF and Windows Forms

**Source**: <https://github.com/dotnet/runtime>, <https://github.com/dotnet/wpf>, <https://github.com/dotnet/winforms>

As described in [License information for .NET on Windows](https://github.com/dotnet/core/blob/main/license-information-windows.md):

- The .NET runtime included in single-file binaries (including `coreclr.dll`) and the WPF
  native components `PresentationNative_cor3.dll`, `wpfgfx_cor3.dll` and
  `vcruntime140_cor3.dll` are licensed under the
  [.NET Library License](https://dotnet.microsoft.com/dotnet_library_license.htm).
- `D3DCompiler_47_cor3.dll` (used by WPF) is licensed under the
  [Windows SDK License](https://learn.microsoft.com/legal/windows-sdk/license).
- All other .NET, WPF and Windows Forms binaries are licensed under the
  [MIT License](https://github.com/dotnet/core/blob/main/LICENSE.TXT).

Notices for third-party code incorporated into .NET itself:

- <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>
- <https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT>
- <https://github.com/dotnet/winforms/blob/main/THIRD-PARTY-NOTICES.TXT>

## Not distributed

The *Segoe Fluent Icons* font used for icons is loaded from the Windows installation at
runtime and is not included in this repository or in the release binary.
