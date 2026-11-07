# Technical notes

How the Alien Shooter widescreen fix works, and what was learned about the game along the way.
Everything here refers to the Steam build (app 33100, "Full version 1.22", 32-bit,
`AlienShooter.exe` MD5 `641f33c3207d37a838d4f7f8f9c40e26`, image base `0x400000`).

## The game

- **Renderer.** The renderer is Direct3D 9 (`Direct3DCreate9`, plus `d3dx9_43` for surface loading). The game is 2D: sprites and maps are drawn at 1:1 pixel size and never scaled.
- **Configuration.** `AlienShooter.cfg` holds the defaults (`DefaultScreenX=1024`...). The game keeps its own copy in `saves/options.ini` (`[graph] ScreenX/ScreenY/FullScreen`) and rewrites it on every start.
- **Resolution cap.** The game ignores `ScreenX`/`ScreenY` in practice. A mode picker at `0x42F7F0`, called from `0x42DC48`, chooses the resolution:
  - it enumerates the adapter's display modes;
  - it keeps those whose width is ≤ a cap pushed at `0x42DC3B` (`push 0x500`, i.e. 1280);
  - among those, it chooses the one whose aspect ratio is closest to the desktop's.

  The result is stored at `this+0x244/0x248` and used for both fullscreen and windowed mode. On a 3440×1440 desktop the game therefore ends up at 1280×720.
- **Map sizes.** The `HEAD` section of `Maps/*.map` holds the map size as two floats. Levels 1 and 11 are 2000×2000 px, level 6 is 3000×3000, and the rest range from 3600 to 8000 px.

  When the screen is wider than the map, the camera clamp pins the view to the map's right edge, which leaves a black strip on the left. Any render wider than 2000 px shows this strip in levels 1 and 11. This is very likely why the cap is 1280.
- **Window procedure.** The window class is `AlienShooter` and the WndProc is at `0x433F50`. That WndProc forwards to a handler object; the handler's input routine is at `0x433250`.
- **Mouse.** The cursor position is **not** read from the mouse messages. On `WM_NCHITTEST` (`0x84`) the game subtracts `GetWindowRect` from the screen coordinates in `lParam`. It reads X with `movzx`, so it treats X as **unsigned 16-bit**: a window at negative screen coordinates breaks it. `WM_LBUTTONDOWN` only sets flags.
- **Keyboard.** The keyboard comes through window messages (there is no DirectInput, `GetAsyncKeyState` or `GetKeyState`).

## The proxy DLL (`upscaler/upscale.c`)

`d3d9.dll` sits next to the game. It loads the system `d3d9.dll` (from the system directory, which WOW64 redirects to SysWOW64). It exports `Direct3DCreate9` and forwards the remaining exports through `jmp` stubs.

### On load (`DllMain`)

1. **Read settings** from `d3d9_upscale.ini` (see below).
2. **Patch the mode picker in memory.** The `.exe` on disk is never changed. Three spots are written:
   - At `0x42DC3B` the cap becomes 0. Now no display mode passes the filter, so the picker keeps its starting values.
   - At `0x42F879` / `0x42F882` those starting values (`mov [eax], 999999.0f` and `mov [esi], 1.0f`) become `RenderWidth` / `RenderHeight`.

   The picker therefore returns exactly the configured render size. The opcode bytes are checked first, and the patch is skipped on any other build.
3. **Hook USER32 through the game's import table:**
   - `CreateWindowExA`: the main window becomes a borderless popup covering the target monitor.
   - `GetClientRect`, `GetWindowRect`, `ClientToScreen`, `SetCursorPos`: report or accept virtual coordinates (see the mouse section).
   - `PeekMessageA`: remaps the `lParam` and `pt` of mouse messages.
   - `ShowWindow` and `GetForegroundWindow`: used by test mode only.

### Device

`IDirect3D9::CreateDevice` is hooked through the `IDirect3D9` vtable. It:

- **Picks the adapter** that drives the chosen monitor (`GetAdapterMonitor`).
- **Creates the real device** in one of two ways:
  - as a **windowed** device the size of the monitor (borderless fullscreen), or
  - as an **exclusive fullscreen** device when `OutputWidth`/`OutputHeight` asks for another mode. If that mode does not exist (for example 4K on a 1440p monitor), creation is retried in borderless mode.
- **Creates the game's surfaces:** an offscreen render target and a depth surface, both at the game's resolution.
- **Returns a wrapper** (`WrapDevice`) instead of the real device. 110 of its 119 methods are generated from `d3d9.h` by `gen_wrap.py` into `devwrap.h` and simply forward to the real device.

  **Why a wrapper:** patching the real device's vtable does not last. Something in the process, most likely the Steam overlay, restores the original `Present` about 2 s after the device is created. The game holds the wrapper, so nothing else can change it.

Hand-written methods:

- **`Present`:** `StretchRect` from the game's render target to the real back buffer (point or linear filter, aspect preserved and centred), then `Present`.
- **`GetBackBuffer`:** returns the game's render target instead of the real back buffer.
- **`Reset`:**
  1. release the offscreen surfaces;
  2. reset the real device with the converted parameters;
  3. recreate the surfaces.
- **`GetDisplayMode`, `GetFrontBufferData`:** report the game-sized surface.
- **`QueryInterface`, `AddRef`, `Release`:** keep COM identity and lifetime working for the wrapper.

### Window and mouse

The game is given a **virtual screen**: a game-sized screen whose window sits at (0,0), whatever monitor it is really on. The conversion between real and virtual points is `real = target origin + image offset + virtual × scale` (`RealToVirtual` / `VirtualToReal`).

- **`WM_NCHITTEST`:** the subclassed window procedure passes virtual coordinates to the game. It answers the hit-test itself (`HTCLIENT`) from the real point.
- **Mouse messages:** `lParam` and `pt` are remapped in `PeekMessageA`.
- **Window rectangles:** `GetWindowRect` and `GetClientRect` return `(0,0,w,h)` at the game's size, and `ClientToScreen` is the identity.
- **Cursor warps:** `SetCursorPos` converts virtual coordinates back to real ones.
- **Window position:** `WM_WINDOWPOSCHANGING` keeps the window on the monitor rectangle.
- **`ClipCursor`:** keeps the mouse on the game's monitor while the game is active.

### Settings: `d3d9_upscale.ini`

| Key | Meaning |
|---|---|
| `Enabled` | 1 / 0 |
| `Monitor` | `\\.\DISPLAYn`; empty means the primary monitor |
| `OutputWidth`, `OutputHeight` | 0 = desktop size (borderless); anything else = exclusive fullscreen in that mode |
| `RenderWidth`, `RenderHeight` | The game's resolution. Keep the width ≤ 2000 (see the map sizes above) |
| `LinearFilter` | 0 = point, 1 = bilinear |
| `IntegerScale` | 1 = whole-number scale factors only |
| `ClipCursor` | 1 = keep the mouse on the game's monitor |
| `TestMode` | See below |
| `Log` | 0 = off, 1 = `d3d9_upscale.log`, 2 = also window messages |

The launcher computes `RenderWidth/Height` as output size ÷ zoom factor. The result is limited to a width of at most 2000 px and a height of at least 600 px, so the menus still fit.

### Test mode

`TestMode=1` lets the game be tested without disturbing the person using the PC:

- **Window:** placed off-screen at x = +20000 (a positive coordinate, because the game reads X as unsigned), and never takes focus.
- **Game state:** the game is told it is active, and `SetCursorPos` does nothing.
- **Captures:** creating `d3d9_upscale_capture.req` in the game folder saves the next presented frame as `d3d9_upscale_capture.bmp`.
- **Input:** clicks are simulated by posting messages to the window. Send `SendMessage(WM_NCHITTEST)` with screen coordinates first, then post the mouse messages with client coordinates.

Note that starting a new campaign overwrites `saves/save.ini`, so back it up before testing.

## The launcher (`launcher/`)

A WPF app on .NET Framework 4.8, which ships with Windows 10/11.

- **Embedded DLL:** it carries `upscaler/d3d9.dll` as an embedded resource and installs or removes it in the game folder.
- **Settings:** it writes `d3d9_upscale.ini` and keeps `FullScreen=1` in the game's `options.ini`.
- **Starting the game:** it starts the game through `steam://rungameid/33100`.
- **Finding the game:** it looks in the default Steam path, then in the Steam libraries listed in `libraryfolders.vdf`.
- **Game art at runtime:** everything game-looking is read from the player's installation when the launcher runs: the background and the logo (`Vid/957_0.BMP`, `Vid/game_logo.bmp`, with the logo's black made transparent), the loading screens shown on the "security camera", the cursor (`cursores/arrow.ani`) and the sounds (`Wav/`). Nothing from the game is embedded.
- **Snapshot:** `AlienShooterLauncher.exe --snapshot out.png [--fake-update X.Y.Z]` renders the window to a PNG without showing it. The optional flag also shows the update notice.

### Self-update (`launcher/Updater.cs`)

- **Check.** On start the launcher asks the GitHub API for this repo's latest release (`/releases/latest`). It offers an update only if all of these hold:
  - the tag (`vX.Y.Z`) is newer than the launcher's own version, which comes from the `VERSION` file at build time;
  - the release has the asset `AlienShooterWidescreenFix-windows.zip`;
  - GitHub publishes a `sha256:` digest for that asset.

  If the PC is offline or GitHub cannot be reached, nothing is shown.
- **Update** (the ACTUALIZAR button):
  1. download the zip to `%TEMP%`;
  2. compare its SHA-256 with GitHub's digest;
  3. unpack it to a separate folder;
  4. rename the running exe to `.old` (a running exe can be renamed but not overwritten);
  5. copy the new exe in its place. If the copy fails, the old exe is renamed back;
  6. copy the rest of the package (best effort) and start the new launcher with `--updated`.
- **First start after an update:** the new launcher deletes the `.old` file. If the fix is enabled and the game is closed, it also replaces `d3d9.dll` in the game folder with the one it carries.
- **Test:** `--update-now` runs a check and an update without a window and writes the result to `<exe>.update.log`. To exercise a real update, build with a lower version (`-p:Version=0.9.0`) and run that.

**Releasing:**
1. Bump `VERSION`.
2. Run `tools/package.ps1`.
3. Tag `vX.Y.Z`.
4. Create the GitHub release with the zip attached. GitHub computes the digest itself.

## Building

`tools/package.ps1` builds the DLL with `upscaler/build.bat` and the launcher with `dotnet build`, then creates `dist/AlienShooterWidescreenFix-windows.zip`. Requirements:

- **DLL:** Visual Studio 2022+ with the C++ x86 tools. `build.bat` finds them through `vswhere`.
- **Launcher:** the .NET 8+ SDK. The .NET Framework 4.8 reference assemblies come from NuGet.

`devwrap.h` is committed. To regenerate it:

```
python gen_wrap.py "<Windows Kits>\Include\<version>\shared\d3d9.h" QueryInterface,AddRef,Release,Reset,Present,GetBackBuffer,GetDisplayMode,GetFrontBufferData,GetSwapChain
```

The second argument lists the methods written by hand in `upscale.c`.
