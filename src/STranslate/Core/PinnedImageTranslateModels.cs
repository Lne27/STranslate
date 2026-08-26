using STranslate.Plugin;
using System.Drawing;
using System.Windows.Media.Imaging;

namespace STranslate.Core;

/// <summary>
/// Pinned 会话持有的冻结图像、OCR 数据与物理坐标。
/// </summary>
internal sealed record PinnedImageTranslateSource(
    BitmapSource Image,
    byte[] OcrPayload,
    int PixelWidth,
    int PixelHeight,
    Rectangle PhysicalBounds);

/// <summary>
/// Pinned 会话级选项快照。
/// </summary>
internal sealed record PinnedImageTranslateOptionsSnapshot(
    string OcrServiceId,
    string TranslateServiceId,
    LangEnum OcrLanguage,
    LayoutAnalysisMode LayoutAnalysisMode,
    LangEnum SourceLanguage,
    LangEnum TargetLanguage);

/// <summary>
/// 单次 OCR/翻译操作使用的不可变参数快照。
/// </summary>
internal sealed record PinnedImageTranslateOperationSnapshot(
    PinnedImageTranslateOptionsSnapshot Options,
    LanguageDetectorType LanguageDetector,
    double LocalDetectorRate,
    LangEnum SourceLanguageIfAuto,
    LangEnum FirstLanguage,
    LangEnum SecondLanguage);

internal enum PinnedImageTranslateStage
{
    None,
    Ocr,
    Layout,
    Translation,
}

/// <summary>
/// 根据选项差异与缓存状态确定最早重算阶段。
/// </summary>
internal static class PinnedImageTranslateInvalidation
{
    internal static PinnedImageTranslateStage GetEarliestStage(
        PinnedImageTranslateOptionsSnapshot pending,
        PinnedImageTranslateOptionsSnapshot? applied,
        bool hasRawOcr,
        bool hasLayout,
        bool hasOverlay,
        bool forceFull)
    {
        if (forceFull || applied == null || !hasRawOcr)
            return PinnedImageTranslateStage.Ocr;

        if (pending.OcrServiceId != applied.OcrServiceId ||
            pending.OcrLanguage != applied.OcrLanguage)
            return PinnedImageTranslateStage.Ocr;

        if (!hasLayout || pending.LayoutAnalysisMode != applied.LayoutAnalysisMode)
            return PinnedImageTranslateStage.Layout;

        if (!hasOverlay ||
            pending.TranslateServiceId != applied.TranslateServiceId ||
            pending.SourceLanguage != applied.SourceLanguage ||
            pending.TargetLanguage != applied.TargetLanguage)
            return PinnedImageTranslateStage.Translation;

        return PinnedImageTranslateStage.None;
    }
}

public enum PinnedImageTranslateResultQuality
{
    None,
    Full,
    Partial,
}
