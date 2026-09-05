using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using STranslate.Controls;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Services;
using STranslate.ViewModels;
using STranslate.Views;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Tests the compiled Compact window with a prepared result. OCR/network services are not run.
internal static class CompactHotkeys
{
    internal static async Task Run()
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new iNKORE.UI.WPF.Modern.ThemeResources());
        app.Resources.MergedDictionaries.Add(new iNKORE.UI.WPF.Modern.Controls.XamlControlsResources());
        foreach (var path in new[] { "Resources/CustomStyles.xaml", "Themes/Generic.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/STranslate;component/{path}") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Languages/en.xaml")) });

        var settings = new Settings();
        var hotkeys = new HotkeySettings();
        hotkeys.Initialize();
        using var plugins = new PluginManager(NullLogger<PluginManager>.Instance);
        var i18n = new Internationalization(NullLogger<Internationalization>.Instance, plugins);
        var serviceSettings = new ServiceSettings();
        var manager = new ServiceManager(plugins, serviceSettings, NullLogger<ServiceManager>.Instance);
        var pluginService = new PluginService(plugins);
        using var ocr = new OcrService(plugins, manager, pluginService, serviceSettings, i18n);
        using var translate = new TranslateService(plugins, manager, pluginService, serviceSettings, i18n);
        var controller = new PinnedWindowController(settings, i18n, null!);
        using var provider = new ServiceCollection()
            .AddSingleton(i18n)
            .AddSingleton(controller)
            .AddTransient(_ => new ImageTranslateWindowViewModel(
                NullLogger<ImageTranslateWindowViewModel>.Instance, settings, hotkeys,
                new DataProvider(i18n), null!, ocr, translate, null!, i18n, null!, null!))
            .BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        foreach (var shortcut in new[] { "F8", "Ctrl + Shift + P" })
        {
            var window = new ImageTranslateCompactWindow();
            var vm = (ImageTranslateWindowViewModel)window.DataContext;
            var button = (IconButton)window.FindName("PART_PinButton");
            var binding = (KeyBinding)window.InputBindings[0];
            await Idle();
            Check(ReferenceEquals(button.Command, binding.Command), "Compiled key binding shares Pin button command");
            Check(binding.Key == Key.P && binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift), "Default gesture");
            hotkeys.PinImageTranslateHotkey.Key = "F8";
            await Idle();
            Check(binding.Key == Key.F8 && binding.Modifiers == ModifierKeys.None, "Live custom gesture");
            hotkeys.PinImageTranslateHotkey.Key = Constant.EmptyHotkey;
            await Idle();
            Check(binding.Key == Key.None && binding.Modifiers == ModifierKeys.None, "Cleared gesture");
            hotkeys.PinImageTranslateHotkey.Key = hotkeys.PinImageTranslateHotkey.Default;
            await Idle();
            Check(binding.Key == Key.P && binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift), "Live reset");
            binding.Command!.Execute(null);
            Check(!vm.CanPin && !button.IsEnabled && Pins() == 0 && window.DataContext != null, "Incomplete result rejects Pin");

            // Only the result is supplied as a fixture; the window, bindings and Pin command are production code.
            var bitmap = BitmapSource.Create(600, 200, 96, 96, PixelFormats.Bgra32, null, new byte[600 * 200 * 4], 2400);
            bitmap.Freeze();
            var overlay = ImageTranslateRenderer.CreateTranslatedOverlay([new OcrLayoutBlock
            {
                Text = "A completed image translation result.",
                BoxPoints = [new(10, 10), new(590, 10), new(590, 90), new(10, 90)],
                LineBoxPoints = [[new(10, 10), new(590, 10), new(590, 90), new(10, 90)]]
            }], ImageTranslateOverlayTheme.Light);
            Set(vm, "_sourceImage", bitmap);
            Set(vm, "_annotatedImage", bitmap);
            Set(vm, "_resultOverlayDocument", overlay);
            vm.DisplayImage = bitmap;
            vm.DisplayOverlayDocument = overlay;
            vm.IsExecuting = true;
            binding.Command.Execute(null);
            Check(!vm.CanPin && Pins() == 0, "Executing result rejects Pin");
            vm.IsExecuting = false;
            hotkeys.PinImageTranslateHotkey.Key = shortcut;
            await Idle();
            Check(vm.CanPin && button.IsEnabled, "Completed result enables Pin");
            window.PlaceForCapture(new System.Drawing.Rectangle(-20000, -20000, 600, 200), new System.Drawing.Size(600, 200));
            binding.Command.Execute(null);
            await Idle();
            Check(Pins() == 1, $"Command bound to {shortcut} creates exactly one static pin");
            Check(window.InputBindings.Count == 0 && window.Content == null, "Compact releases bindings and content");
            controller.CloseAll();
            hotkeys.PinImageTranslateHotkey.Key = hotkeys.PinImageTranslateHotkey.Default;
        }
        Console.WriteLine("PASS: all Compact hotkey checks completed.");
    }

    private static int Pins() => Application.Current.Windows.OfType<PinnedImageTranslateWindow>().Count();
    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static async Task Idle() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException(name);
        Console.WriteLine($"PASS: {name}");
    }
}
