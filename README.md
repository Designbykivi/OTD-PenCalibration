# Pen Calibration for OpenTabletDriver

Wacom-style pen calibration for pen displays. Tap a series of crosshairs with the pen, and
the cursor lands exactly under the pen tip. It corrects offset, scale, rotation and skew, plus
keystone (perspective) if the panel or the parallax is uneven.

Requires OpenTabletDriver **0.6.5 or newer** and **Absolute Mode**.

It has two parts:

| Part | What it does |
|---|---|
| **Pen Calibration** filter (`PenCalibration.dll`) | OTD plugin. Applies the saved correction to every pen report. |
| **Pen Calibration app** (`PenCalibrationApp`) | Fullscreen window with the targets. Measures the taps and sends the result to the filter. |

The filter runs inside the OTD daemon, which can't draw on screen, so the targets come from the separate app.

## Install

**From the Plugin Manager:** in OpenTabletDriver, open **Plugins → Plugin Manager**, select
**Pen Calibration** and click **Install**.

**Manually:** download `PenCalibration.zip` from the
[latest release](https://github.com/Designbykivi/OTD-PenCalibration/releases/latest) and drag it
into the Plugin Manager. You can also unzip it into its own folder under the plugin directory:

- macOS: `~/Library/Application Support/OpenTabletDriver/Plugins/Pen Calibration/`
- Windows: `%localappdata%\OpenTabletDriver\Plugins\Pen Calibration\`
- Linux: `~/.config/OpenTabletDriver/Plugins/Pen Calibration/`

Then restart OpenTabletDriver, open **Filters**, enable **Pen Calibration**, and click **Apply**.
If you use other filters (smoothing etc.), put Pen Calibration **last**.

The zip holds the plugin and the calibration app for macOS (Apple silicon and Intel), Windows x64 and
Linux x64, so it's about 60 MB. The app is self-contained: it doesn't need .NET installed.

On macOS, if you unzipped a browser download by hand and the calibration screen doesn't open, macOS
has quarantined the app. Clear it with:

```bash
xattr -dr com.apple.quarantine ~/Library/Application\ Support/OpenTabletDriver/Plugins/Pen\ Calibration
```

Installing through the Plugin Manager avoids this.

## Calibrate

1. In OTD's **Filters** tab, open **Pen Calibration**, tick **Calibrate** and click **Apply**.
   (OTD 0.6 can't show buttons for plugins, so a checkbox and Apply stand in for one.) Untick it once you're
   calibrated, or the screen opens again on every Apply.
   It opens fullscreen on the display your pen was last used on. On the start screen it follows the pen:
   hover over another display and it moves there. **N** also moves it to the next display.
2. Press **T** to choose the number of targets: 4, 5, 9 (default), 16 or 25. More targets average out
   hand jitter: with 25 the result is roughly twice as precise as with 9. **P** switches between
   *Perspective* (default) and *Affine* correction.
3. Tap anywhere to start. The cursor is hidden while you tap. Tap the centre of each crosshair, holding the pen the way you normally draw.
   **Backspace** undoes the last tap, **Esc** cancels.
4. The app shows the average and worst error before and after correction. Press **Enter** to save. The correction applies straight away.
5. Tap the crosshairs again to check the result. Your taps are drawn as dots.

**C** on the start screen removes the saved calibration.

To see the saved calibration, open the **Calibration** dropdown in the Pen Calibration filter. It lists when
you calibrated, the average and worst error before and after, the offset at the centre of the screen, and the
scale and rotation. Picking a line does nothing. OTD reads the list when you open the filter, so
click another filter and back to refresh it after calibrating.

The checkbox is ignored for the first few seconds after OTD starts, so the screen doesn't pop up at login, and while the calibration screen is already open.

You can also open the calibration app directly from the plugin's `App/<platform>` folder.
On macOS it's a regular `.app`, so Finder works.

Calibrations are saved per tablet model in:

- macOS: `~/Library/Application Support/OpenTabletDriver-PenCalibration/calibrations.json`
- Windows: `%localappdata%\OpenTabletDriver-PenCalibration\calibrations.json`
- Linux: `~/.local/share/OpenTabletDriver-PenCalibration/calibrations.json`

They are stored outside the plugin folder, so updating the plugin keeps them.

## How it works

- The filter runs at OTD's **PostTransform** stage, after the tablet area has been mapped to
  the screen, so the correction is a 3×3 transform in screen coordinates.
- The app and filter talk over `127.0.0.1:47853` (see [Shared/Protocol.cs](Shared/Protocol.cs)).
  While you tap, the filter passes positions through uncorrected. The filter turns correction back on
  when you finish, cancel, or close the app.
- For each tap the app records three points: the crosshair, the cursor position the OS reports,
  and the raw position the filter saw. Cursor and raw position are the same physical point, so the
  app measures how its window coordinates map to OTD's coordinates directly. That handles DPI
  scaling and multi-monitor offsets on every OS without guessing.
- The correction is a least-squares fit (normalised DLT for perspective). With more targets than
  the model needs, it averages out hand jitter. The app rejects a result when a target doesn't
  agree with the others, when the correction would move the cursor implausibly far, or when the cursor and the driver disagree.

## Build

Requires the .NET 8 SDK or newer.

```bash
dotnet build Plugin -c Release
```

```bash
dotnet build App -c Release
```

To build the release (the plugin folder with the app for every platform, plus `dist/PenCalibration.zip`):

```bash
./package.sh
```

Pass platforms such as `osx-arm64` to build only those. Add `--install` on macOS or Linux to copy the
folder straight into OTD's plugin directory. The macOS app is signed ad hoc when built on a Mac.

The tests use their own port and a temporary calibration file, so they never touch a running OTD.
Run them (fitting, the full calibration maths, and the plugin ↔ app protocol over a real socket):

```bash
./run-tests.sh
```

## Notes

- Relative Mode isn't supported, because there is no screen position to correct.
- Any local program can connect to the port. The protocol can only read the pen position and set or clear the calibration.
- One calibration per tablet model name. If you have two identical tablets, they share it.
- Opening on the right display and finding the plugin's folder use OTD internals that aren't part of
  the plugin API (checked against 0.6.x). A future major OTD version will probably need an update.
- Tested on macOS with OpenTabletDriver 0.6.8. The Windows and Linux apps are built but not yet
  tested on those systems. On Linux the daemon has to run inside your desktop session to open the app.

## License

[MIT](LICENSE)
