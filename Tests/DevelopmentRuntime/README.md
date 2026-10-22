# Isolated development application

[New-DevelopmentRuntime.ps1](./New-DevelopmentRuntime.ps1) stages selected assets from an existing installation and publishes the current C# application into a **new directory outside both the repository and the installation**. It does not launch the application. Installed save data, battle state, logs, CK3 mod bundles and updater executables are not copied.

```powershell
./Tests/DevelopmentRuntime/New-DevelopmentRuntime.ps1 `
  -AssetRuntime 'C:/installed/crusader-conflicts' `
  -Destination 'C:/test-evidence/development-runtime-01' `
  -DotNet 'C:/dotnet9/dotnet.exe' `
  -AppVersion '1.4.2'
```

The explicit `development-runtime.json` marker disables upstream app/mapper/updater replacement and automatic CK3 mod copying. The normal version file still determines the displayed version; a development suffix and “automatic updates off” label make the mode visible. The marker records source revision, whether uncommitted changes were present, the source asset directory, and the built assembly hash. Without this marker, normal release behavior is unchanged.

Use the copied development playset's existing source descriptor. The application does not install or overwrite that CK3 component automatically in development mode. The marker is not a gameplay sandbox: clicking the normal battle workflow can still launch games and import results, so UI tests should use disposable test campaigns.

Startup checks that can open dialogs run from the main window's `Shown` event. When development guards complete synchronously, running those checks inside `Load` can block the first main-window display behind an owned release-note dialog with no taskbar entry. The corrected startup logs `HomePage shown: Visible=..., Handle=..., Bounds=...` before updater/notification checks. Native startup verification on 2026-10-21 confirmed a nonzero main-window handle and the `Crusader Conflicts` title without administrator privileges.

The same native UI check activated the app, dismissed its visible release-note dialog, opened Options, and found the **Preserve CK3 combat stats** checkbox under Unit Scale/Max. Selecting it and closing Options persisted `<Option name="StatFidelityMode">Enabled</Option>` in the isolated runtime's options file. The installed application's options were not touched. For automation, select the Options dialog's own returned window and use its coordinates/accessibility tree; the parent window's clipped view is not the dialog surface.

That initial UI check used runtime02. The current source label is **CK3 combat stats (experimental)**, and enabling it includes anchored knight budgets and strict native levy coverage by default. Runtime04 was staged after the exact combat-result-link fix, with assembly SHA-256 `F0804C6C3AF243B74E36CFF389B405344989E196225F65911B91B26E07D07CEE`. It preserves the original runtime and has no advanced fidelity override file. Its final native UI/gameplay test is separate from the earlier runtime02 checkbox proof.
