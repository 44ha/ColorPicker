# Chroma — Color Picker

A fast, privacy-friendly **global color picker for Windows**. Press `Ctrl+Shift+C`
anywhere on screen (even with the app minimized), move the cursor over the color
you want, and left-click — the color is converted to every common format and the
HEX value lands in your clipboard.

The UI is intentionally minimal: **two colors only — white and blue (#1D4ED8).**

---

## Features

- **System-wide hotkey** (`Ctrl+Shift+C` by default, reconfigurable in Settings)
  built on the standard `RegisterHotKey` API.
- **Full-screen picking overlay** that exists *only while you are picking* —
  one click captures the pixel, one right-click (or `Esc`) cancels.
- **Live widget at the cursor** showing HEX / RGB / HSL while you move.
- **Every format at once**: `HEX`, raw HEX, `RGB`, `RGBA`, `HSL`, `CMYK`.
- **History** of picked colors with one-click copy, expandable format rows,
  manual HEX entry, and per-item removal.
- **Clipboard + toast feedback** on every successful pick.
- Multi-monitor and mixed-DPI aware (Per-Monitor-V2): samples true physical
  pixels on every monitor, including monitors left/above the primary.

## Requirements

- Windows 10 (1703+) or Windows 11
- .NET 8 SDK to build · .NET 8 Desktop Runtime to run the published build

## Build & run

```powershell
dotnet build ColorPicker.csproj     # debug build → bin\Debug\net8.0-windows\
dotnet run --project ColorPicker.csproj
```

Publish a self-contained release:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

## Usage

| Action | Result |
| --- | --- |
| `Ctrl+Shift+C` (default) | Start / stop picking mode |
| Move the cursor | Widget shows the color under the cursor live |
| Left-click | Copy HEX to clipboard, record it in history, show a toast |
| Right-click or `Esc` | Cancel without capturing |

You can also paste or type any HEX value (`#F0F`, `FF00FF`, `0xFF00FF`,
`#FF00FFCC`) into the history bar and press **Add**.

---

## Design notes

### Why there is no `SetWindowsHookEx`

Global low-level keyboard/mouse hooks are how keyloggers behave, so AV products
watch for them — an app that installs one 24/7 can trigger heuristics even when
it is completely benign. Chroma therefore uses only APIs designed for ordinary
applications:

- **`RegisterHotKey`** — the standard Windows API for application hotkeys.
- **A transient top-level overlay window** (`WS_POPUP | WS_EX_TOPMOST |
  WS_EX_NOACTIVATE | WS_EX_LAYERED`) created *only while picking is active*.
  It covers the virtual screen, swallows the single capture click locally, and
  is destroyed the moment picking ends. Between picks the app observes **no
  input at all**.

The overlay uses layered alpha **1/255, not 0**: Windows routes mouse messages
around layered areas whose alpha is *zero*, so an alpha-0 window never receives
input. Alpha 1 is invisible to the eye and — verified experimentally — leaves
the sampled pixel value unchanged.

### How sampling works

Pixels are read from the desktop with a **1×1 `BitBlt` (`Graphics.CopyFromScreen`)
into a reusable bitmap** — the same strategy Microsoft's PowerToys color picker
uses — with a classic `GetDC(NULL) → GetPixel` call as fallback. Coordinates
flow end-to-end in *physical* screen pixels (the process is Per-Monitor-V2
DPI aware), so the value returned is exactly the pixel under the cursor on any
monitor layout.

The final capture sample is taken **after** the picking widget and overlay are
torn down, and the widget is positioned so it never sits on the pixel being
sampled — otherwise you could pick up the widget's own color instead of the
content underneath.

### Color-accuracy expectations

The picker reports the **8-bit sRGB value Windows composites at that point** —
what a screenshot at that pixel would contain. Two situations where that
differs from what a page's CSS says:

- **Edges and text.** Anti-aliased pixels are blends of the foreground and
  background colors. Click well inside a flat area of the element instead of
  its border or text for the element's actual color.
- **Hover / animated elements.** You capture what is displayed *now*; if a
  hover effect changed the element before the click, that changed color is
  what you sampled.

System-level caveats to be aware of (not app bugs):

- **HDR mode**: with HDR enabled the desktop is composed in scRGB; any 8-bit
  eyedropper — this one included — can only report the clipped SDR value.
- **Display ICC profiles / wide-gamut monitors** can make the framebuffer
  value differ from the CSS hex a browser's devtools shows (this affects
  PowerToys' picker too — see
  [PowerToys#11585](https://github.com/microsoft/PowerToys/issues/11585)).
  Turn off HDR / use the sRGB profile if you need devtools-exact hexes.
- **Protected content** (DRM video) is unreadable by any screen sampler.

## Project structure

```
ColorPicker/
├── App.xaml(.cs)                 Application resources (theme) & service wiring
├── MainWindow.xaml(.cs)          Dashboard: current color, settings, history
├── FloatingWidgetWindow.xaml(.cs) Cursor-side live preview widget (picking mode)
├── ToastWindow.xaml(.cs)         "Copied!" confirmation
├── app.manifest                  Per-Monitor-V2 DPI awareness, OS compatibility
├── Services/
│   ├── HotkeyService.cs          RegisterHotKey wrapper (system-wide hotkey)
│   ├── PickingOverlayService.cs  Transient full-screen click-capture overlay
│   ├── PickingController.cs      Picking-mode orchestration
│   ├── PixelCaptureService.cs    1×1 BitBlt sampling (+ GetPixel fallback)
│   ├── ClipboardService.cs       Clipboard with busy-retry handling
│   ├── ColorConverterService.cs  HEX parsing + conversion entry point
│   ├── ScreenPlacement.cs        Edge-aware widget/toast placement
│   └── Win32Api.cs               All P/Invoke declarations
├── Models/ColorInfo.cs           Color math: HEX/RGB/RGBA/HSL/CMYK
└── ViewModels/                   MainViewModel + history items
```
