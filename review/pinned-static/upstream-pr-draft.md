# feat: pin static image translation results from Compact

Thank you for the guidance on interaction and architecture in #769. This revision follows the suggested flow: finish screenshot capture, OCR and translation in Compact, then use a **Pin** button in its toolbar to keep the completed result in an independent, always-on-top window.

Changes addressing the feedback:

1. **Use the existing Compact mode.** Standalone and Compact remain the two image-translation modes. The Pin button is available once a result is ready. A successful pin closes Compact so the existing entry points can start the next task. Pinned windows have no toolbar, and this change adds no hotkey configuration.
2. **Keep pinned results static.** Each pin shares the frozen source and annotated images and the completed vector translation overlay, and copies the text-selection data. It has no translation ViewModel, OCR/translation task, automatic recomputation, debounce, execution coordinator or deferred service disposal.
3. **Support text selection and copying.** Both layers support single-click/drag selection, double-click word selection, triple-click paragraph selection, and copying. Paragraph boundaries come from the existing layout analysis, so wrapped lines stay in their paragraph. Clipped translated text remains available for full-paragraph copying. Right-clicking text offers selection copying; the background menu provides Copy all, Original/Translation, Shadow and Close. Background dragging moves the pin; double-clicking the background closes it.
4. **Keep multiple pins independent.** Later changes to translation services, languages, layout options or display-layer settings leave existing snapshots intact. Menu labels use the existing dynamic language resources. Starting another capture temporarily cloaks pinned content and chrome windows; completing or cancelling the selection restores them, including pins created or closed during capture.
5. **Preserve the previous shadow and active glow.** The original colors, margins, blur radii, opacity and rendering biases remain unchanged. The chrome now clips the same WPF effects to four edge regions and fills the opaque center directly, reducing intermediate rendering surfaces. It remains one companion window with mouse passthrough. The shadow switch affects the current pin and sets the default for future pins; the active glow remains independent.

The main-window capture/translation dispatch, service management, OCR plugins and hotkey configuration retain the upstream implementation. The layout analyzer only carries forward paragraph membership it already determined. `PinnedWindowController` manages the pin collection and capture visibility; the content window handles static display and input using the existing `ImageZoom`. The rendering optimization stays inside the existing chrome file. Settings gains only the shadow default.

Validation:

- Debug and Release builds complete with no warnings or errors.
- 321 regression tests pass in the test branch, including 168 comparisons against the original chrome renderer across state changes, sizes, and 100%–300% DPI. The rendered BGRA bytes match exactly. A separate 56-case rendering comparison also passes, and the two appearances were checked side by side in a visible layered WPF window.
- The final upstream candidate runs the original 274 tests successfully.
- The screenshot/OCR/translation-to-pin flow and core interactions were manually tested by the submitter. The rendering follow-up is covered by the pixel comparisons above. Window-level checks cover capture hide/restore, creation and closure during capture, and release after closing; all closed-window weak references are collected.

See [Test code](https://github.com/Lne27/STranslate/tree/feature/pinned-static-review/src/Tests/STranslate.Tests) and [Performance tests and raw results](https://github.com/Lne27/STranslate/tree/feature/pinned-static-review/review/pinned-static).

Performance and memory measurements:

Measured on Windows 11 build 26200, x64, .NET 10.0.11, 96 DPI, WPF Tier 2, using 30 independent 800×400 BGRA source/annotated image pairs and 73 characters per text layer. Windows are shown off-screen. Each configuration runs in three fresh processes after a one-window warm-up.

| Measurement, median of three runs | Previous renderer | Optimized renderer | Reduction |
| --- | ---: | ---: | ---: |
| Chrome-stage private-memory increment | 176.23 MiB | 51.33 MiB | 70.9% |
| Total increment with components added in stages | 459.79 MiB | 355.79 MiB | 22.6% |
| Total increment after 30 sequential pins, each activated and then deactivated as the next opens | 371.77 MiB | 354.36 MiB | 4.7% |

The staged measurement adds images, text/snapshots, content windows, and chrome in that order; it waits for Dispatcher idle, 500 ms, and a full GC at each stage. The sequence measurement uses the production pin controller, allows each new pin to render while active, and waits two seconds before the final GC/sample. These two orders exercise different allocations and reuse of WPF rendering resources.

For the optimized staged run with the median total increment:

| Component stage | Private-memory increment | Share of total |
| --- | ---: | ---: |
| Source images | 26.77 MiB | 7.52% |
| Annotated images, translated text and snapshots | 33.68 MiB | 9.47% |
| Content windows, ImageZoom and WPF rendering | 244.02 MiB | 68.59% |
| Shadow and active-glow companion windows | 51.33 MiB | 14.43% |
| Total | **355.79 MiB** | **100%** |

Percentages are rounded.

Snapshot copying averages **8.58 μs** over 1,000 iterations and allocates **12,032 bytes** per snapshot. The 30-window cloak/restore median is **33.09 ms**, including two DWM synchronizations. Chrome drawing is rebuilt on size, DPI or active-state changes; moving an unchanged pin reuses its drawing.

The window-level creation sample takes **152 ms** for one pin (previously **139 ms**), and **2.85 s** for a rapid batch of 30 (previously **2.16 s**). The additional clipped drawing nodes trade some initialization time for smaller rendering surfaces.

The memory figures are process-private increments recorded as each component is added, including rendering resources and runtime caches. Actual pinning shares the images already produced by Compact. Content rendering retains the existing hardware-accelerated ImageZoom and transparent-image behavior. Static pins have no polling or OCR/translation recomputation, and their zoom animations are disabled.

Below is a screenshot from testing in a real usage scenario:

![Screenshot from testing Compact pinning in a real usage scenario](https://raw.githubusercontent.com/Lne27/STranslate/feature/pinned-static-review/review/pinned-static/test-image-1.png)
