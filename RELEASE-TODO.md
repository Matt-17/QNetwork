# QNetwork Release TODO

Scope: publish QNetwork through winget only. The public installer should be a versioned MSI attached to a GitHub Release and referenced by the winget manifest through that GitHub Release URL.

## Release Decision

- [x] Keep QNetwork on .NET 10 LTS for the first public winget release. Yes
- [x] Publish as self-contained `win-x64`. Yes
- [x] Keep `PublishSingleFile=true`. Yes 
- [x] Do not declare a `.NET Desktop Runtime` dependency in winget. Yes
- [x] Keep installer scope per-machine unless release testing shows winget elevation behavior is unacceptable. Yes
- [x] Keep the app manifest requiring administrator rights because ETW kernel tracing needs elevation. yes

## Project Updates

- [x] Verify all projects are intentionally on .NET 10:
  - `QNetwork/QNetwork.csproj`: `net10.0-windows`.
  - `QNetwork.Core/QNetwork.Core.csproj`: `net10.0`.
  - `QNetwork.Cli/QNetwork.Cli.csproj`: `net10.0`.
  - `QNetwork.Tests/QNetwork.Tests.csproj`: `net10.0`.
- [x] Keep application metadata aligned:
  - Product: `QNetwork`.
  - Company/manufacturer/publisher: `Code-iX`.
  - License: MIT.
  - Version: supplied by the release tag.
- [x] Replace placeholder app metadata before release:
  - About window GitHub value.
  - README title-only content.
  - Any feature claims that are not currently shipped.

## Installer

- [x] Keep WiX project at `QNetwork.Installer/QNetwork.Installer.wixproj`.
- [x] Keep WiX package source at `QNetwork.Installer/Package.wxs`.
- [x] Do not change `UpgradeCode="{60C7E0F4-0162-43B9-A56C-8ED103F94A90}"`.
- [x] Ensure installer build consumes the self-contained publish output.
- [x] Ensure release asset name is exactly `QNetwork-X.Y.Z-x64.msi`.
- [x] Validate generated MSI values:
  - Product name `QNetwork`.
  - Manufacturer `Code-iX`.
  - Version `X.Y.Z`.
  - Per-machine install scope.
  - Start Menu shortcut.
  - App icon in Apps & Features.

## GitHub Actions

- [x] Split the current `.github/workflows/build.yml` into:
  - `.github/workflows/ci.yml` for branch and pull request validation.
  - `.github/workflows/release.yml` for `v*` tags.
- [x] CI workflow should:
  - Restore.
  - Build.
  - Test.
  - Publish self-contained `win-x64`.
  - Build the MSI as an installer-source validation step.
- [x] Release workflow should:
  - Derive `X.Y.Z` from tag `vX.Y.Z`.
  - Run tests in Release configuration.
  - Publish self-contained `win-x64`.
  - Build `QNetwork-X.Y.Z-x64.msi`.
  - Create a GitHub Release.
  - Upload only the winget-targeted MSI as the distribution asset.
  - Submit/update `Code-iX.QNetwork` in winget after the initial package exists.
- [ ] Add required repository secret:
  - `WINGET_TOKEN`, if using `winget-releaser`.

## Winget

- [ ] Initial submission:
  - Create first GitHub Release with the MSI.
  - Submit initial package manifest for `Code-iX.QNetwork`.
  - Use the GitHub Release MSI URL as `InstallerUrl`.
  - Use `Architecture: x64`.
  - Use `InstallerType: wix` or `msi`, whichever `wingetcreate`/validation confirms for the generated MSI.
  - Do not add .NET runtime dependency because the package is self-contained.
- [ ] Automated updates:
  - Confirm `winget-releaser` or an equivalent GitHub Actions path can submit version update PRs.
  - Confirm the release asset regex only matches the intended MSI.
- [ ] Validate locally before submission:
  - `winget validate <manifest-folder>`.
  - Sandbox install test from the manifest.
- [ ] Confirm winget handles install elevation cleanly:
  - Non-elevated shell.
  - Elevated shell.
  - Uninstall path.

## Docs

- [ ] Expand `README.md`:
  - What QNetwork does.
  - Why administrator rights are required.
  - Install command: `winget install Code-iX.QNetwork`.
  - Manual fallback: latest GitHub Release MSI.
  - Build/test commands using `QNetwork.slnx`.
  - Release/tag process.
- [ ] Add `docs/RELEASING.md`:
  - Tag format: `vX.Y.Z`.
  - Self-contained .NET 10 release model.
  - First winget submission steps.
  - `WINGET_TOKEN` setup.
  - Stable `UpgradeCode` warning.
- [ ] Add `docs/DESIGN.md` if QConvert gets the same file:
  - Shared Code-iX WPF palette.
  - Shared button/card/text style rules.
  - App-specific notes for dense monitoring tables and operator-facing diagnostics.

## Smoke Test

- [ ] Install MSI from a non-elevated shell and confirm expected elevation behavior.
- [ ] Install MSI from an elevated shell.
- [ ] Launch QNetwork from Start Menu.
- [ ] Verify the WPF app starts monitoring when elevated.
- [ ] Verify the app fails clearly or relaunches correctly when not elevated.
- [ ] Run CLI without elevation and confirm the expected admin-rights message.
- [ ] Run CLI elevated and confirm monitoring output.
- [ ] Uninstall and verify app files and Start Menu entry are removed.
- [ ] Verify winget commands after package merge:
  - `winget show Code-iX.QNetwork`.
  - `winget install Code-iX.QNetwork`.
  - `winget upgrade Code-iX.QNetwork`.
  - `winget uninstall Code-iX.QNetwork`.

## Done

- [ ] Default branch CI is green.
- [ ] A clean `vX.Y.Z` tag creates a GitHub Release with `QNetwork-X.Y.Z-x64.msi`.
- [ ] `Code-iX.QNetwork` exists in `microsoft/winget-pkgs`.
- [ ] A fresh Windows machine can install QNetwork through winget without manually installing .NET.
