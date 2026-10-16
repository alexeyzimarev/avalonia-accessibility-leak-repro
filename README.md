# Avalonia macOS accessibility leak repro

Reproduces [AvaloniaUI/Avalonia#8240](https://github.com/AvaloniaUI/Avalonia/issues/8240) on macOS without a screen reader: once an accessibility client has walked a control, the control, its subtree and its `DataContext` are never garbage-collected.

The app walks its own accessibility tree in-process: `objc_msgSend` to `accessibilityChildren`, `accessibilityParent`, `accessibilityTopLevelUIElement`, `accessibilityFrame` and `accessibilityHitTest:`, starting at the `AvnWindow`. That drives the same `automation.mm` code AppKit drives on behalf of VoiceOver, Accessibility Inspector or any menu-bar or window-management utility. After each walk it detaches the content, forces a GC and counts how many detached views are still alive. Each view holds a 10 MB payload so the growth is visible in the heap size.

## Run

```
dotnet run -c Release
```

Expected on Avalonia 12.1.3 (the native bridge is unchanged on `main` as of 2026-10-16):

```
phase1 iter=0 ax_nodes=97 detached=1 alive=1 heap_mb=14
phase1 iter=1 ax_nodes=97 detached=2 alive=2 heap_mb=25
phase1 iter=2 ax_nodes=97 detached=3 alive=3 heap_mb=37
phase1 iter=3 ax_nodes=97 detached=4 alive=4 heap_mb=48
phase1 iter=4 ax_nodes=97 detached=5 alive=5 heap_mb=57
phase2 ax_nodes_before=101 ax_nodes_after=104 (expected after > before)
RESULT phase1_alive=5/5 final_alive=6/6 heap_mb=72
```

Every detached view is still alive. Exit code 1.

## Test a fixed native library

```
dotnet run -c Release -- --native /path/to/libAvalonia.Native.OSX.dylib
```

The dylib must be built from the Avalonia version the app references (12.1.3): the IDL vtables have to match the managed `Avalonia.Native.dll`.

```
git clone --filter=blob:none https://github.com/AvaloniaUI/Avalonia.git
cd Avalonia && git checkout 12.1.3
dotnet build native/Avalonia.Native/Avalonia.Native.macOS.proj -t:GenerateMicroComItems
xcodebuild -project native/Avalonia.Native/src/OSX/Avalonia.Native.OSX.xcodeproj -configuration Release \
  CONFIGURATION_BUILD_DIR=$PWD/artifacts/native/Release ONLY_ACTIVE_ARCH=YES MACOSX_DEPLOYMENT_TARGET=12.0
```

`MACOSX_DEPLOYMENT_TARGET=12.0` is needed on recent Xcode versions, which reject the project's 10.13 target.

With the fix from [alexeyzimarev/Avalonia `fix/macos-automation-bridge-leak`](https://github.com/alexeyzimarev/Avalonia/tree/fix/macos-automation-bridge-leak) applied to those sources:

```
phase1 iter=4 ax_nodes=97 detached=5 alive=0 heap_mb=2
phase2 ax_nodes_before=101 ax_nodes_after=104 (expected after > before)
RESULT phase1_alive=0/5 final_alive=0/6 heap_mb=2
```

Exit code 0.

## Options

- `--iterations N`: number of attach, walk, detach cycles (default 5).
- `--native PATH`: load this `libAvalonia.Native.OSX.dylib` instead of the packaged one.

Phase 2 hides a child, adds to it while hidden, shows it again and walks once more. It exercises the paths where a control's native accessibility element is released while its automation peer stays alive. With the fix applied it must complete without a crash and report more nodes after than before.
