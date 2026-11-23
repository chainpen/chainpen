# Installing Chainpen

Chainpen is free and comes from [GitHub Releases](https://github.com/chainpen/chainpen/releases). Download the file for your computer from the latest release.

| Computer | File | |
|---|---|---|
| Windows 10 (22H2) or 11, most PCs | `Chainpen-win-x64-Setup.exe` | Installer |
| Windows on Arm (Surface Pro X, Copilot+ PCs) | `Chainpen-win-arm64-Setup.exe` | Installer |
| Mac with Apple silicon (M1 and later) | `Chainpen-osx-arm64-Setup.pkg` | Installer |
| Mac with an Intel processor | `Chainpen-osx-x64-Setup.pkg` | Installer |
| Linux (Ubuntu 22.04+, Fedora 39+), most PCs | `Chainpen-linux-x64.AppImage` | Runs without installing |
| Linux on Arm | `Chainpen-linux-arm64.AppImage` | Runs without installing |

Not sure which Mac you have? Choose  → About This Mac: "Chip: Apple M…" means Apple silicon; "Processor: Intel" means Intel.

## Why your computer warns you, and what to do

Chainpen isn't signed with a paid developer certificate yet, so Windows and macOS don't recognise who made it and ask you to confirm the first time. This is expected for apps from small developers. Only do this for files you downloaded from the Chainpen releases page above.

### Windows: "Windows protected your PC"

1. Open `Chainpen-win-x64-Setup.exe`.
2. Windows SmartScreen shows **Windows protected your PC**. Select **More info**.
3. Check that it says the app is `Chainpen-win-x64-Setup.exe`, then select **Run anyway**.

Chainpen installs for you only (no administrator password) and appears in the Start menu.

### macOS: "cannot be opened because the developer cannot be verified"

1. Open `Chainpen-osx-arm64-Setup.pkg` (or the Intel one).
2. If macOS says it can't be opened, select **Done** (not Move to Bin).
3. Open **System Settings → Privacy & Security**, scroll to **Security**, and select **Open Anyway** next to the message about the Chainpen installer. Enter your password if asked.
4. Follow the installer. Chainpen goes in your Applications folder.

The first time you open Chainpen itself you may need to do the same again: right-click Chainpen in Applications, choose **Open**, then **Open** in the message.

### Linux

1. Make the file runnable: right-click it → Properties → Permissions → **Allow executing file as program**, or run `chmod +x Chainpen-linux-x64.AppImage`.
2. Double-click it to start Chainpen.

Saved signatures are encrypted with your desktop's keyring (GNOME Keyring or KWallet) when `secret-tool` is installed (`sudo apt install libsecret-tools` on Ubuntu). Without it they're kept unencrypted, readable only by your user account.

## Updates

Installed copies check GitHub for a new version a few seconds after starting. When one has downloaded, **Update ready** appears in the status bar: choose **Restart and update**, or **When I close Chainpen**. Your unsaved changes come back after the restart. The only things fetched are the list of releases and, when there's a newer version, the update itself; nothing about you or your documents is sent.

## Uninstalling

- **Windows:** Settings → Apps → Installed apps → Chainpen → Uninstall.
- **macOS:** drag Chainpen from Applications to the Bin.
- **Linux:** delete the AppImage.

Your settings, saved signatures and recovery copies stay in the app data folder until you delete it: `%APPDATA%\Chainpen` on Windows, `~/Library/Application Support/Chainpen` on macOS, `~/.config/Chainpen` on Linux. On macOS, the saved-signatures key is in Keychain Access as "Chainpen".
