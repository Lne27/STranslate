# feat: pin static image translation results from Compact

Thank you for your detailed feedback on the interaction and architecture of my earlier implementation. This revision follows the suggested flow: finish screenshot capture, OCR and translation in Compact, then use a **Pin** button in its toolbar to keep the completed result in an independent, always-on-top window.

Changes addressing the feedback:

1. Keep the existing Standalone and Compact modes, adding a Pin button to the Compact toolbar when a result is ready. A successful pin closes Compact so the existing entry points can start the next task. Pinned windows have no toolbar. No new mode or hotkey configuration is introduced.
2. Keep pinned results static. Each pin shares the frozen source and annotated images and the completed vector translation overlay, and copies the text-selection data. It has no independent translation ViewModel, OCR/translation task, automatic recomputation, debounce, execution coordinator or deferred service disposal.
3. Support single-click/drag selection, double-click word selection, triple-click paragraph selection and copying in both layers. Paragraph membership comes from the existing layout analysis, so wrapped lines stay in their paragraph; clipped translations retain their complete copyable text. Right-clicking text offers selection copying. The background menu provides Copy all, Original/Translation, Shadow and Close; dragging the background moves the pin, and double-clicking it closes the pin.
4. Keep multiple pins independent. Later changes to services, languages, layout options or display-layer settings leave existing snapshots intact; menu labels use the existing dynamic language resources. Starting another capture temporarily hides pinned content and chrome windows. Completing or cancelling selection restores them, including pins created or closed during capture.
5. Preserve the black shadow and active blue glow from the first revision of this PR, with the same colors, margins, blur radii, opacity and rendering biases. The companion window keeps mouse passthrough and clips the original WPF effects to four edge regions, filling the opaque center directly to reduce rendering surfaces. The shadow switch affects the current pin and saves the default for future pins; the active glow remains independent.

This implementation uses the existing WPF/MVVM and dependency-injection structure. The main-window dispatch, service manager, OCR plugin interfaces and hotkey configuration are unchanged. Integration changes register `PinnedWindowController` for pin lifetime and capture visibility, add hide/restore scopes around the existing screenshot methods, and expose completed results and pin availability from the existing image-translation ViewModel. That ViewModel also checks cancellation before publishing results. `ImageZoom` gains selection helpers and resets stale selection when its text source changes; the existing layout results supply paragraph membership without changing segmentation rules. Selection data retains clipped translation text for copying. The new content and chrome windows handle static display/input and shadow/glow rendering. Settings gains only the shadow default. Static pins disable zoom animations, share the images already produced by Compact, and use no polling or OCR/translation recomputation. Chrome drawing is rebuilt on size, DPI or active-state changes; moving an unchanged pin reuses its drawing.

Validation: Debug and Release builds complete with **zero warnings and errors**. All **321 regression tests** pass, including **168 byte-for-byte BGRA comparisons** against a reference of the shadow/glow appearance from the first revision of this PR, across state changes, sizes and 100%–300% DPI; a separate **56-case** rendering comparison also passes. The final upstream candidate passes all **274 existing upstream tests**. Basic functional checks and manual testing of the final optimized build are complete: the submitter confirmed the screenshot/OCR/translation-to-pin flow, appearance and core interactions. Window-level checks cover capture hide/restore, pins created or closed during capture, and release after closing; closed-window weak-reference counts are **0** for the 1-, 10- and 30-window groups.

See [Test code](https://github.com/Lne27/STranslate/tree/feature/pinned-static-review/src/Tests/STranslate.Tests) and [Performance tests and raw results](https://github.com/Lne27/STranslate/tree/feature/pinned-static-review/review/pinned-static).

Performance: snapshot copying averages **8.58 μs** over 1,000 iterations, with **12,032 bytes** of managed allocation per snapshot. The 30-window hide/restore median is **33.09 ms**, including two DWM synchronizations. Process CPU-time increments during three-second idle samples are **109.38 / 62.50 / 46.88 ms** for 1 / 10 / 30 windows. Creating and initializing the windows takes **152 ms** for one pin and **2.85 s** for a rapid batch of 30.

Memory measurements use Windows 11 build 26200, x64, .NET 10.0.11, 96 DPI, WPF Tier 2, with 30 independent 800×400 BGRA source/annotated image pairs and 73 characters per text layer. Windows are shown off-screen. Each configuration runs in three fresh processes after a one-window warm-up:

The staged measurement adds images, text/snapshots, content windows and chrome in order, ending with 29 shadows and one active glow; each stage waits for Dispatcher idle, 500 ms and a full GC. The sequential measurement uses the production pin controller, lets each new pin render while active, then waits two seconds before the final GC/sample. The median total private-memory increments are **355.79 MiB** for staged creation and **354.36 MiB** for sequential pinning.

Component breakdown for the staged run with the median total increment:

| Component stage | Private-memory increment | Share of total |
| --- | ---: | ---: |
| Source images | 26.77 MiB | 7.52% |
| Annotated images, translated text and snapshots | 33.68 MiB | 9.47% |
| Content windows, ImageZoom and WPF rendering | 244.02 MiB | 68.59% |
| Shadow and active-glow companion windows | 51.33 MiB | 14.43% |
| Total | **355.79 MiB** | **100%** |

Percentages are rounded. These are process-private increments recorded as each component is added, including rendering resources and runtime caches. The complete component measurement starts with image creation; actual pinning shares Compact's existing images. Content rendering retains the existing hardware-accelerated ImageZoom and transparent-image behavior.

Below is a screenshot from testing in a real usage scenario:

![Screenshot from testing Compact pinning in a real usage scenario](https://raw.githubusercontent.com/Lne27/STranslate/feature/pinned-static-review/review/pinned-static/test-image-1.png)
