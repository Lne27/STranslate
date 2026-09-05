# feat: pin static image translation results from Compact

Thank you for your detailed feedback on the interaction and architecture. This implementation follows your suggested flow: complete screenshot capture, OCR and translation in Compact, then use **Pin** to keep the result in an independent, always-on-top window.

Changes addressing the feedback:

1. Keep the existing Standalone and Compact modes. Add a Pin button to the Compact toolbar and an optional Compact-only shortcut, **unassigned by default**, both using the same Pin command. Pin is available when a result is ready. The shortcut can be changed, cleared or reset under Settings → Hotkeys → OCR / image translation window, using the existing conflict checks and persistence. Pinning closes the current Compact window, and the existing entry points can start the next screenshot task. Pinned windows have no toolbar.
2. Store a static display snapshot in each pin. It shares the frozen images and completed translation overlay, and owns its text-selection data. OCR, translation and settings adjustments remain in Compact; pinned windows do not retain a translation ViewModel or rerun processing.
3. Support single-click/drag selection, double-click word selection, triple-click paragraph selection and copying in both text layers. Wrapped lines retain their paragraph membership, and clipped translations remain fully copyable. Right-clicking selected text provides Copy. The background menu contains Copy all, Original/Translation, Shadow and Close. Dragging the background moves the pin; double-clicking it closes the pin.
4. Allow multiple independent pins. Service or result-setting changes do not alter existing snapshots, while menu labels follow the application language. During screenshot selection, pinned content and shadows are temporarily hidden and restored on completion or cancellation, including pins created or closed during capture.
5. Display a black shadow and an active blue glow in a companion window with mouse passthrough. The shadow switch affects the current pin and saves the default for future pins; the active glow remains independent. Physical image bounds drive both windows, including movement across monitors with different DPI and recovery after a monitor is disconnected.

The implementation uses the existing WPF/MVVM and dependency-injection structure. The main-window dispatch, service manager and OCR plugin interfaces are unchanged. Pin extends the existing window-hotkey settings and binds to the toolbar command through WPF; it adds no global hotkey registration. `PinnedWindowController` manages pin lifetime and screenshot visibility; the existing screenshot methods acquire a hide/restore scope. The image-translation ViewModel exposes completed results and pin availability, with cancellation checks before publishing results. The content window reuses `ImageZoom`; selection helpers use paragraph membership from the existing layout analysis without changing segmentation rules, retain clipped text for copying, and clear stale selection when the text source changes. Settings gains one shadow-default value.

Static pins have no polling or background recomputation, and zoom animations are disabled. Shadow/glow effects are clipped to four edge regions, with the opaque center drawn directly. Drawing is rebuilt on size, DPI or activation changes; moving an unchanged pin reuses it.

Validation: Debug and Release builds succeed; Release completes with **zero warnings and errors**. The linked test code passes **326 regression tests**, and the submitted code passes all **274 existing upstream tests**. Rendering checks include **168 pixel comparisons** against the expected shadow/glow appearance across sizes, state changes and 100%–300% DPI. Basic functional checks and manual testing cover the screenshot/OCR/translation-to-pin flow, appearance, selection, copying and menus. Shortcut checks cover saved settings, defaults, clearing, resetting and conflicts. A compiled Compact-window test with a prepared result verifies live key bindings, rejection of unfinished results, and the shared Pin command for user-defined keys, including Ctrl+Alt+F8. Resetting leaves the shortcut unassigned, and the Pin button remains usable without a shortcut. Window-level checks cover screenshot hide/restore, creation and closure during capture, and release after closing; closed-window weak-reference counts are **0** for the 1-, 10- and 30-window groups.

See [Test code](https://github.com/Lne27/STranslate/tree/feature/pinned-static-review/src/Tests/STranslate.Tests) and [Performance tests and raw results](https://github.com/Lne27/STranslate/tree/feature/pinned-static-review/review/pinned-static).

Performance: snapshot creation averages **8.58 μs** over 1,000 iterations, with **12,032 bytes** of managed allocation per snapshot. The 30-window hide/restore median is **33.09 ms**, including two DWM synchronizations. Process CPU-time increments during three-second idle samples are **109.38 / 62.50 / 46.88 ms** for 1 / 10 / 30 windows. Creating and initializing the windows takes **152 ms** for one pin and **2.85 s** for a rapid batch of 30.

Measurements use Windows 11 build 26200, x64, .NET 10.0.11, 96 DPI and WPF Tier 2, with 800×400 BGRA source/annotated image pairs and 73 characters per text layer. Benchmark windows are shown off-screen. Component-memory measurements use 30 independent image pairs and pins, with three fresh processes per configuration after a one-window warm-up.

The component measurement adds images, text/snapshots, content windows and chrome in stages, ending with 29 shadows and one active glow. Each stage waits for Dispatcher idle, 500 ms and a full GC. The median total private-memory increment is **355.79 MiB**; its component breakdown is:

| Component stage | Private-memory increment | Share of total |
| --- | ---: | ---: |
| Source images | 26.77 MiB | 7.52% |
| Annotated images, translated text and snapshots | 33.68 MiB | 9.47% |
| Content windows, ImageZoom and WPF rendering | 244.02 MiB | 68.59% |
| Shadow and active-glow companion windows | 51.33 MiB | 14.43% |
| Total | **355.79 MiB** | **100%** |

Percentages are rounded. These process increments include rendering resources and runtime caches. The component measurement starts with image creation; actual pinning shares Compact's existing images. A separate measurement using the production controller to open 30 pins sequentially, allowing each to render while active, records a median total increment of **354.36 MiB** after a two-second wait and full GC. Content rendering retains ImageZoom's hardware rendering and transparent-image support.

Below is a screenshot from testing in a real usage scenario:

![Screenshot from testing Compact pinning in a real usage scenario](https://raw.githubusercontent.com/Lne27/STranslate/feature/pinned-static-review/review/pinned-static/test-image-1.png)
