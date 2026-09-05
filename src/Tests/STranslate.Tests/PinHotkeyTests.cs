using STranslate.Core;
using System.IO;

namespace STranslate.Tests;

public class PinHotkeyTests
{
    [Fact]
    public void ExistingSettingsGetPinDefaultWithoutChangingOtherShortcuts()
    {
        using var file = new SettingsFile();
        File.WriteAllText(file.Path, """{"SwitchImageHotkey":{"Key":"Ctrl + F9"}}""");
        var settings = new StorageBase<HotkeySettings>(file.Path).Load();
        settings.Initialize();

        Assert.Equal(Constant.EmptyHotkey, settings.PinImageTranslateHotkey.Key);
        Assert.Equal(Constant.EmptyHotkey, settings.PinImageTranslateHotkey.Default);
        Assert.Equal("Ctrl + F9", settings.SwitchImageHotkey.Key);
    }

    [Theory]
    [InlineData("Ctrl + Alt + F8")]
    [InlineData("F8")]
    [InlineData(Constant.EmptyHotkey)]
    public void PinEditPersistsAndResetKeepsTheDeclaredDefault(string key)
    {
        using var file = new SettingsFile();
        var storage = new TestStorage(file.Path);
        var settings = storage.Load();
        settings.Initialize();
        settings.SetStorage(storage);
        settings.PinImageTranslateHotkey.Key = key;

        var reloaded = new StorageBase<HotkeySettings>(file.Path).Load();
        reloaded.Initialize();
        Assert.Equal(key, reloaded.PinImageTranslateHotkey.Key);
        Assert.Equal(Constant.EmptyHotkey, reloaded.PinImageTranslateHotkey.Default);

        settings.PinImageTranslateHotkey.Key = settings.PinImageTranslateHotkey.Default;
        Assert.Equal(Constant.EmptyHotkey,
            new StorageBase<HotkeySettings>(file.Path).Load().PinImageTranslateHotkey.Key);
    }

    [Fact]
    public void PinParticipatesInImageWindowAndGlobalConflictChecks()
    {
        var settings = new HotkeySettings();
        settings.PinImageTranslateHotkey.Key = "Ctrl + Alt + F8";
        var pin = Assert.Single(settings.RegisteredHotkeys, item => item.ResourceKey == "Hotkey_PinImageTranslate");
        Assert.IsNotType<GlobalHotkey>(settings.PinImageTranslateHotkey);
        Assert.Equal(HotkeyType.ImageTransWindow, pin.Type);

        // 与现有设置对话框使用同一个作用域匹配规则。
        Assert.True(Overlaps(pin.Type, HotkeyType.ImageTransWindow));
        Assert.True(Overlaps(pin.Type, HotkeyType.OcrWindow | HotkeyType.ImageTransWindow));
        Assert.False(Overlaps(pin.Type, HotkeyType.MainWindow));
        Assert.False(Overlaps(pin.Type, HotkeyType.OcrWindow));
        var global = Assert.Single(settings.RegisteredHotkeys, item => item.ResourceKey == "Hotkey_ImageTranslate");
        Assert.True(Overlaps(pin.Type, global.Type));
        Assert.DoesNotContain(settings.RegisteredHotkeys,
            item => item.ResourceKey != pin.ResourceKey && item.Hotkey == pin.Hotkey && Overlaps(pin.Type, item.Type));

        pin.OnRemovedHotkey!();
        Assert.Equal(Constant.EmptyHotkey, settings.PinImageTranslateHotkey.Key);
        Assert.Equal(Constant.EmptyHotkey,
            Assert.Single(settings.RegisteredHotkeys, item => item.ResourceKey == pin.ResourceKey).Hotkey);
    }

    private static bool Overlaps(HotkeyType first, HotkeyType second) =>
        first.HasFlag(second) || second.HasFlag(first);

    private sealed class TestStorage : AppStorage<HotkeySettings>
    {
        internal TestStorage(string path)
        {
            FilePath = path;
            DirectoryPath = System.IO.Path.GetDirectoryName(path)!;
        }
    }

    private sealed class SettingsFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "STranslate-PinHotkey-" + Guid.NewGuid());
        internal string Path => System.IO.Path.Combine(_directory, "HotkeySettings.json");
        internal SettingsFile() => Directory.CreateDirectory(_directory);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
