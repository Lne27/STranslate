using STranslate.Core;
using STranslate.Plugin;

namespace STranslate.Tests;

public class PinnedImageTranslateTests
{
    [Fact]
    public void SettingsDefaults_PinnedLooksLikeAPlainPin()
    {
        var settings = new Settings();

        Assert.Equal(ImageTranslateWindowMode.Standalone, settings.ImageTranslateWindowMode);
        Assert.False(settings.PinnedImageTranslateShowToolbar);
        Assert.True(settings.PinnedImageTranslateShowShadow);
    }

    [Fact]
    public void ImageTranslateWindowMode_PreservesOriginalModesAndAddsPinned()
    {
        Assert.Equal(
            [
                ImageTranslateWindowMode.Standalone,
                ImageTranslateWindowMode.Compact,
                ImageTranslateWindowMode.Pinned,
            ],
            Enum.GetValues<ImageTranslateWindowMode>());
    }

    [Theory]
    [InlineData(ImageTranslateWindowMode.Standalone, true)]
    [InlineData(ImageTranslateWindowMode.Compact, false)]
    [InlineData(ImageTranslateWindowMode.Pinned, false)]
    public void ScreenshotPadding_MatchesWindowPlacementContract(
        ImageTranslateWindowMode mode,
        bool expected)
    {
        Assert.Equal(expected, Screenshot.ShouldPadImage(mode));
    }

    [Fact]
    public void Invalidation_OcrChange_RerunsWholePipeline()
    {
        var applied = CreateOptions();
        var pending = applied with { OcrServiceId = "ocr-b" };

        var stage = PinnedImageTranslateInvalidation.GetEarliestStage(
            pending,
            applied,
            hasRawOcr: true,
            hasLayout: true,
            hasOverlay: true,
            forceFull: false);

        Assert.Equal(PinnedImageTranslateStage.Ocr, stage);
    }

    [Fact]
    public void Invalidation_OcrLanguageChange_RerunsWholePipeline()
    {
        var applied = CreateOptions();
        var pending = applied with { OcrLanguage = LangEnum.Japanese };

        var stage = PinnedImageTranslateInvalidation.GetEarliestStage(
            pending,
            applied,
            hasRawOcr: true,
            hasLayout: true,
            hasOverlay: true,
            forceFull: false);

        Assert.Equal(PinnedImageTranslateStage.Ocr, stage);
    }

    [Fact]
    public void Invalidation_LayoutChange_ReusesRawOcr()
    {
        var applied = CreateOptions();
        var pending = applied with { LayoutAnalysisMode = LayoutAnalysisMode.NoMerge };

        var stage = PinnedImageTranslateInvalidation.GetEarliestStage(
            pending,
            applied,
            hasRawOcr: true,
            hasLayout: true,
            hasOverlay: true,
            forceFull: false);

        Assert.Equal(PinnedImageTranslateStage.Layout, stage);
    }

    [Theory]
    [InlineData("translator-b", LangEnum.English, LangEnum.ChineseSimplified)]
    [InlineData("translator-a", LangEnum.Japanese, LangEnum.ChineseSimplified)]
    [InlineData("translator-a", LangEnum.English, LangEnum.Japanese)]
    public void Invalidation_TranslationOptionChange_ReusesOcrAndLayout(
        string translatorId,
        LangEnum sourceLanguage,
        LangEnum targetLanguage)
    {
        var applied = CreateOptions();
        var pending = applied with
        {
            TranslateServiceId = translatorId,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
        };

        var stage = PinnedImageTranslateInvalidation.GetEarliestStage(
            pending,
            applied,
            hasRawOcr: true,
            hasLayout: true,
            hasOverlay: true,
            forceFull: false);

        Assert.Equal(PinnedImageTranslateStage.Translation, stage);
    }

    [Fact]
    public void Invalidation_NoChange_DoesNoWork()
    {
        var applied = CreateOptions();

        var stage = PinnedImageTranslateInvalidation.GetEarliestStage(
            applied,
            applied,
            hasRawOcr: true,
            hasLayout: true,
            hasOverlay: true,
            forceFull: false);

        Assert.Equal(PinnedImageTranslateStage.None, stage);
    }

    [Fact]
    public void Invalidation_MissingCache_RestartsAtEarliestMissingStage()
    {
        var applied = CreateOptions();

        Assert.Equal(
            PinnedImageTranslateStage.Ocr,
            PinnedImageTranslateInvalidation.GetEarliestStage(
                applied, applied, false, true, true, false));
        Assert.Equal(
            PinnedImageTranslateStage.Layout,
            PinnedImageTranslateInvalidation.GetEarliestStage(
                applied, applied, true, false, true, false));
        Assert.Equal(
            PinnedImageTranslateStage.Translation,
            PinnedImageTranslateInvalidation.GetEarliestStage(
                applied, applied, true, true, false, false));
    }

    [Fact]
    public async Task ExecutionCoordinator_SerializesSameService()
    {
        var coordinator = new ImageTranslateExecutionCoordinator();
        var service = CreateService("service-a");

        var first = await coordinator.AcquireAsync(service, CancellationToken.None);
        var secondTask = coordinator.AcquireAsync(service, CancellationToken.None).AsTask();

        Assert.False(secondTask.IsCompleted);

        await first.DisposeAsync();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
        await second.DisposeAsync();
    }

    [Fact]
    public async Task ExecutionCoordinator_RetireDefersDisposeAndRejectsStaleReference()
    {
        var coordinator = new ImageTranslateExecutionCoordinator();
        var service = CreateService("service-a");
        var disposeCount = 0;

        var lease = await coordinator.AcquireAsync(service, CancellationToken.None);
        coordinator.Retire(service, () => Interlocked.Increment(ref disposeCount));

        Assert.Equal(0, disposeCount);

        await lease.DisposeAsync();
        Assert.Equal(1, disposeCount);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var staleLease = await coordinator.AcquireAsync(service, CancellationToken.None);
            await staleLease.DisposeAsync();
        });
    }

    private static PinnedImageTranslateOptionsSnapshot CreateOptions() =>
        new(
            "ocr-a",
            "translator-a",
            LangEnum.English,
            LayoutAnalysisMode.Auto,
            LangEnum.English,
            LangEnum.ChineseSimplified);

    private static Service CreateService(string id) =>
        new()
        {
            ServiceID = id,
            MetaData = new PluginMetaData
            {
                PluginID = $"plugin-{id}",
                Name = id,
            },
        };
}
