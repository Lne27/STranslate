using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern;
using Microsoft.Extensions.Logging;
using STranslate.Controls;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.Services;
using STranslate.Views.Pages;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace STranslate.ViewModels;

/// <summary>
/// Pinned 图片翻译窗口的独立会话 ViewModel。
/// </summary>
public partial class PinnedImageTranslateViewModel : ObservableObject, IDisposable
{
    private const int AutoApplyDebounceMs = 350;

    private readonly ILogger<PinnedImageTranslateViewModel> _logger;
    private readonly Settings _settings;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly OcrService _ocrService;
    private readonly TranslateService _translateService;
    private readonly ImageTranslateExecutionCoordinator _executionCoordinator;
    private readonly Internationalization _i18n;
    private readonly CollectionViewSource _transCollectionView;

    private PinnedImageTranslateSource? _source;
    private OcrResult? _rawOcrResult;
    private List<OcrLayoutBlock>? _layoutSourceBlocks;
    private List<OcrLayoutBlock>? _translatedBlocks;
    private BitmapSource? _annotatedImage;
    private ImageTranslateOverlayDocument? _resultOverlayDocument;
    private ObservableCollection<OcrWord> _originalSelectionWords = [];
    private ObservableCollection<OcrWord> _translatedSelectionWords = [];
    private PinnedImageTranslateOptionsSnapshot? _appliedSnapshot;
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _operationCts;
    private long _operationGeneration;
    private string _selectedOcrServiceId = string.Empty;
    private string _selectedTranslateServiceId = string.Empty;
    private bool _suppressComputeOptionChanged;
    private bool _initialized;
    private bool _disposed;

    public PinnedImageTranslateViewModel(
        ILogger<PinnedImageTranslateViewModel> logger,
        Settings settings,
        HotkeySettings hotkeySettings,
        DataProvider dataProvider,
        MainWindowViewModel mainWindowViewModel,
        OcrService ocrService,
        TranslateService translateService,
        ImageTranslateExecutionCoordinator executionCoordinator,
        Internationalization i18n)
    {
        _logger = logger;
        _settings = settings;
        HotkeySettings = hotkeySettings;
        DataProvider = dataProvider;
        _mainWindowViewModel = mainWindowViewModel;
        _ocrService = ocrService;
        _translateService = translateService;
        _executionCoordinator = executionCoordinator;
        _i18n = i18n;

        OcrEngines = [];
        RefreshOcrEngines();
        SelectedOcrEngine = _ocrService.GetImageTranslateOcrServiceOrDefault();
        _selectedOcrServiceId = SelectedOcrEngine?.ServiceID ?? string.Empty;

        _transCollectionView = new CollectionViewSource { Source = _translateService.Services };
        _transCollectionView.Filter += OnTransFilter;
        SelectedTranslateEngine = _translateService.ImageTranslateService
            ?? _translateService.Services.FirstOrDefault(service => service.Plugin is ITranslatePlugin);
        _selectedTranslateServiceId = SelectedTranslateEngine?.ServiceID ?? string.Empty;

        OcrLanguage = _settings.ImageTranslateOcrLanguage;
        LayoutAnalysisMode = _settings.LayoutAnalysisMode;
        SourceLanguage = _settings.ImageTranslateSourceLang;
        TargetLanguage = _settings.ImageTranslateTargetLang;
        // Pinned 默认显示译文，显示状态不继承其他窗口模式。
        IsShowingAnnotated = false;
        IsToolbarVisible = _settings.PinnedImageTranslateShowToolbar;
        IsShadowEnabled = _settings.PinnedImageTranslateShowShadow;

        _ocrService.Services.CollectionChanged += OnOcrServicesCollectionChanged;
        _translateService.Services.CollectionChanged += OnTranslateServicesCollectionChanged;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    public HotkeySettings HotkeySettings { get; }
    public DataProvider DataProvider { get; }
    public ICollectionView TransCollectionView => _transCollectionView.View;
    public Task? CurrentOperationTask { get; private set; }

    [ObservableProperty]
    public partial BitmapSource? DisplayImage { get; set; }

    [ObservableProperty]
    public partial ImageTranslateOverlayDocument? DisplayOverlayDocument { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<OcrWord> OcrWords { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<Service> OcrEngines { get; set; }

    [ObservableProperty]
    public partial Service? SelectedOcrEngine { get; set; }

    [ObservableProperty]
    public partial Service? SelectedTranslateEngine { get; set; }

    [ObservableProperty]
    public partial LangEnum OcrLanguage { get; set; } = LangEnum.Auto;

    [ObservableProperty]
    public partial LayoutAnalysisMode LayoutAnalysisMode { get; set; } = LayoutAnalysisMode.Auto;

    [ObservableProperty]
    public partial LangEnum SourceLanguage { get; set; } = LangEnum.Auto;

    [ObservableProperty]
    public partial LangEnum TargetLanguage { get; set; } = LangEnum.Auto;

    [ObservableProperty]
    public partial bool IsShowingAnnotated { get; set; }

    [ObservableProperty]
    public partial bool IsToolbarVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsShadowEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsExecuting { get; set; }

    [ObservableProperty]
    public partial string ProcessRingText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Result { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsNoLocationInfoVisible { get; set; }

    [ObservableProperty]
    public partial bool HasPendingChanges { get; set; }

    [ObservableProperty]
    public partial bool IsStatusVisible { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Severity StatusSeverity { get; set; } = Severity.Informational;

    [ObservableProperty]
    public partial PinnedImageTranslateResultQuality AppliedResultQuality { get; set; }

    internal void Initialize(PinnedImageTranslateSource source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _source = source;
        DisplayImage = source.Image;
        _initialized = true;
        var generation = InvalidateCurrentMeaning();
        StartOperation(generation, forceFull: true);
    }

    internal void CancelCurrentOperation()
    {
        if (_disposed)
            return;

        Interlocked.Increment(ref _operationGeneration);
        _debounceCts?.Cancel();
        _operationCts?.Cancel();
        IsExecuting = false;
    }

    [RelayCommand]
    private async Task ReExecuteAsync()
    {
        if (_source == null || _disposed)
            return;

        var forceFull = _appliedSnapshot == null || CapturePendingOptions() == _appliedSnapshot;
        var generation = InvalidateCurrentMeaning();
        await StartOperation(generation, forceFull);
    }

    [RelayCommand]
    private void SwitchImage() => IsShowingAnnotated = !IsShowingAnnotated;

    [RelayCommand]
    private void HideToolbar() => IsToolbarVisible = false;

    [RelayCommand]
    private void Close(Window window) => window.Close();

    [RelayCommand]
    private void CopySelectedText(ImageZoom? imageZoom)
    {
        var text = imageZoom?.SelectedText;
        if (!string.IsNullOrWhiteSpace(text))
            Clipboard.SetText(text);
    }

    [RelayCommand]
    private void SelectAllText(ImageZoom? imageZoom) => imageZoom?.SelectAllText();

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var window = await _mainWindowViewModel.OpenSettingsInternalAsync(null);

        if (Keyboard.Modifiers == ModifierKeys.Control)
            window.Navigate(nameof(OcrPage), selectedService: SelectedOcrEngine);
        else if (Keyboard.Modifiers == ModifierKeys.Alt)
            window.Navigate(nameof(TranslatePage), selectedService: SelectedTranslateEngine);
        else
            window.Navigate(nameof(StandalonePage));
    }

    partial void OnSelectedOcrEngineChanged(Service? value)
    {
        if (value != null)
            _selectedOcrServiceId = value.ServiceID;
        if (!_suppressComputeOptionChanged)
            OnComputeOptionChanged();
    }

    partial void OnSelectedTranslateEngineChanged(Service? value)
    {
        if (value != null)
            _selectedTranslateServiceId = value.ServiceID;
        if (!_suppressComputeOptionChanged)
            OnComputeOptionChanged();
    }
    partial void OnOcrLanguageChanged(LangEnum value) => OnComputeOptionChanged();
    partial void OnLayoutAnalysisModeChanged(LayoutAnalysisMode value) => OnComputeOptionChanged();
    partial void OnSourceLanguageChanged(LangEnum value) => OnComputeOptionChanged();
    partial void OnTargetLanguageChanged(LangEnum value) => OnComputeOptionChanged();

    partial void OnIsShowingAnnotatedChanged(bool value)
    {
        if (_initialized)
            RefreshDisplayState();
    }

    partial void OnIsToolbarVisibleChanged(bool value)
    {
        if (!_initialized)
            return;

        _settings.PinnedImageTranslateShowToolbar = value;
    }

    partial void OnIsShadowEnabledChanged(bool value)
    {
        if (!_initialized)
            return;

        _settings.PinnedImageTranslateShowShadow = value;
    }

    private void OnComputeOptionChanged()
    {
        if (!_initialized || _disposed || _source == null)
            return;

        var generation = InvalidateCurrentMeaning();
        HasPendingChanges = _appliedSnapshot == null || CapturePendingOptions() != _appliedSnapshot;
        // 仅对实际选项变化做防抖重算。
        ScheduleDebouncedOperation(generation);
    }

    private long InvalidateCurrentMeaning()
    {
        var generation = Interlocked.Increment(ref _operationGeneration);
        _debounceCts?.Cancel();
        _operationCts?.Cancel();
        IsExecuting = false;
        return generation;
    }

    private void ScheduleDebouncedOperation(long generation)
    {
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        _ = DebounceAndRunAsync(generation, _debounceCts.Token);
    }

    private async Task DebounceAndRunAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AutoApplyDebounceMs, cancellationToken);
            if (generation != _operationGeneration || _disposed)
                return;

            await StartOperation(generation, forceFull: false);
        }
        catch (OperationCanceledException)
        {
            // Latest request wins。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pinned image translate debounce execution failed");
        }
    }

    private Task StartOperation(long generation, bool forceFull)
    {
        if (_source == null || _disposed || generation != _operationGeneration)
            return Task.CompletedTask;

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var snapshot = CaptureOperationSnapshot();
        var task = RunOperationAsync(generation, snapshot, forceFull, _operationCts.Token);
        CurrentOperationTask = task;
        return task;
    }

    private async Task RunOperationAsync(
        long generation,
        PinnedImageTranslateOperationSnapshot snapshot,
        bool forceFull,
        CancellationToken cancellationToken)
    {
        var source = _source;
        if (source == null || !CanCommit(generation))
            return;

        IsExecuting = true;
        ClearTransientStatus();

        try
        {
            var earliestStage = DetermineEarliestStage(snapshot.Options, forceFull);
            if (earliestStage == PinnedImageTranslateStage.None)
            {
                HasPendingChanges = false;
                return;
            }

            OcrResult rawCandidate;
            ObservableCollection<OcrWord> originalWordsCandidate;
            List<OcrLayoutBlock> layoutSourceCandidate;
            var invalidateAnnotatedImage = earliestStage <= PinnedImageTranslateStage.Layout;

            if (earliestStage <= PinnedImageTranslateStage.Ocr)
            {
                ProcessRingText = _i18n.GetTranslation("RecognizingImageText");
                var ocrService = ResolveOcrService(snapshot.Options.OcrServiceId)
                    ?? throw new InvalidOperationException(_i18n.GetTranslation("ImageTranslateOcrServiceNotFoundMessage"));

                await using var ocrLease = await _executionCoordinator.AcquireAsync(ocrService, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (ocrService.Plugin is not IOcrPlugin ocrPlugin)
                    throw new InvalidOperationException(_i18n.GetTranslation("ImageTranslateOcrServiceNotFoundMessage"));

                rawCandidate = await ocrPlugin.RecognizeAsync(
                    new OcrRequest(
                        source.OcrPayload,
                        snapshot.Options.OcrLanguage,
                        source.PixelWidth,
                        source.PixelHeight),
                    cancellationToken);
                Utilities.PrepareOcrResult(rawCandidate);

                if (!rawCandidate.IsSuccess || string.IsNullOrWhiteSpace(rawCandidate.Text))
                    throw new InvalidOperationException(_i18n.GetTranslation("OcrFailed"));

                originalWordsCandidate = OcrWordBuilder.CreateFromOcrContents(rawCandidate.OcrContents);
            }
            else
            {
                rawCandidate = _rawOcrResult
                    ?? throw new InvalidOperationException("Pinned OCR cache is unavailable.");
                originalWordsCandidate = _originalSelectionWords;
            }

            if (earliestStage <= PinnedImageTranslateStage.Layout)
            {
                layoutSourceCandidate = OcrLayoutAnalyzer.AnalyzeBlocks(rawCandidate, snapshot.Options.LayoutAnalysisMode);
                if (layoutSourceCandidate.Count == 0)
                    throw new InvalidOperationException(_i18n.GetTranslation("OcrFailed"));
            }
            else
            {
                layoutSourceCandidate = _layoutSourceBlocks != null
                    ? CloneLayoutBlocks(_layoutSourceBlocks)
                    : throw new InvalidOperationException("Pinned layout cache is unavailable.");
            }

            ProcessRingText = _i18n.GetTranslation("TranslatingText");
            var translatedCandidate = CloneLayoutBlocks(layoutSourceCandidate);
            var translationResult = await TranslateBlocksAsync(translatedCandidate, snapshot, cancellationToken);
            if (translationResult.TranslatableCount > 0 && translationResult.SuccessCount == 0)
                throw new InvalidOperationException(_i18n.GetTranslation("ImtransFailed"));

            cancellationToken.ThrowIfCancellationRequested();
            if (!CanCommit(generation))
                return;

            // 提交时使用当前主题，避免旧快照覆盖新的显示主题。
            var overlayTheme = _settings.ColorScheme == ElementTheme.Dark
                ? ImageTranslateOverlayTheme.Dark
                : ImageTranslateOverlayTheme.Light;
            var overlayCandidate = ImageTranslateRenderer.CreateTranslatedOverlay(translatedCandidate, overlayTheme);
            var translatedWordsCandidate = new ObservableCollection<OcrWord>(overlayCandidate.SelectableWords);

            CommitSuccessfulResult(
                generation,
                snapshot,
                rawCandidate,
                layoutSourceCandidate,
                translatedCandidate,
                invalidateAnnotatedImage,
                overlayCandidate,
                originalWordsCandidate,
                translatedWordsCandidate,
                translationResult.FailureCount);
        }
        catch (OperationCanceledException)
        {
            // 取消或过期结果不影响当前显示。
        }
        catch (Exception ex)
        {
            if (CanCommit(generation))
            {
                var message = _appliedSnapshot == null
                    ? ex.Message
                    : string.Format(
                        _i18n.GetTranslation("ImageTranslatePinnedUpdateFailedPreviousResult"),
                        ex.Message);
                ShowStatus(message, Severity.Error);
                HasPendingChanges = _appliedSnapshot == null || CapturePendingOptions() != _appliedSnapshot;
                _logger.LogError(ex, "Pinned image translate execution failed");
            }
            else
            {
                // 忽略第三方插件延迟返回的过期结果。
                _logger.LogDebug(ex, "Ignored stale pinned image translate completion");
            }
        }
        finally
        {
            if (CanCommit(generation))
                IsExecuting = false;
        }
    }

    private async Task<TranslationRunResult> TranslateBlocksAsync(
        List<OcrLayoutBlock> blocks,
        PinnedImageTranslateOperationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var translateService = ResolveTranslateService(snapshot.Options.TranslateServiceId)
            ?? throw new InvalidOperationException(_i18n.GetTranslation("ImageTranslateServiceNotFoundMessage"));

        await using var translateLease = await _executionCoordinator.AcquireAsync(translateService, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (translateService.Plugin is not ITranslatePlugin translatePlugin)
            throw new InvalidOperationException(_i18n.GetTranslation("NoTranslateService"));

        var detectOptions = new LangDetectOptions(
            snapshot.LanguageDetector,
            snapshot.LocalDetectorRate,
            snapshot.SourceLanguageIfAuto,
            snapshot.FirstLanguage,
            snapshot.SecondLanguage);

        var translatableCount = 0;
        var successCount = 0;
        var failureCount = 0;

        // 同一服务按 block 串行执行。
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(block.Text))
                continue;

            translatableCount++;
            var originalText = block.Text;
            var (isSuccess, source, target) = await LanguageDetector.GetLanguageAsync(
                originalText,
                snapshot.Options.SourceLanguage,
                snapshot.Options.TargetLanguage,
                cancellationToken,
                options: detectOptions);

            if (!isSuccess)
            {
                failureCount++;
                continue;
            }

            var result = new TranslateResult();
            await translatePlugin.TranslateAsync(
                new TranslateRequest(originalText, source, target),
                result,
                cancellationToken);

            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Text))
            {
                failureCount++;
                continue;
            }

            var normalizedText = ImageTranslateTextOverlayLayout.NormalizeOverlayText(result.Text);
            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                failureCount++;
                continue;
            }

            block.Text = normalizedText;
            successCount++;
        }

        return new TranslationRunResult(translatableCount, successCount, failureCount);
    }

    private void CommitSuccessfulResult(
        long generation,
        PinnedImageTranslateOperationSnapshot snapshot,
        OcrResult rawOcrResult,
        List<OcrLayoutBlock> layoutSourceBlocks,
        List<OcrLayoutBlock> translatedBlocks,
        bool invalidateAnnotatedImage,
        ImageTranslateOverlayDocument overlayDocument,
        ObservableCollection<OcrWord> originalSelectionWords,
        ObservableCollection<OcrWord> translatedSelectionWords,
        int translationFailureCount)
    {
        if (!CanCommit(generation))
            return;

        _rawOcrResult = rawOcrResult;
        _layoutSourceBlocks = CloneLayoutBlocks(layoutSourceBlocks);
        _translatedBlocks = CloneLayoutBlocks(translatedBlocks);
        if (invalidateAnnotatedImage)
            _annotatedImage = null;
        _resultOverlayDocument = overlayDocument;
        _originalSelectionWords = originalSelectionWords;
        _translatedSelectionWords = translatedSelectionWords;
        _appliedSnapshot = snapshot.Options;

        Result = rawOcrResult.Text;
        IsNoLocationInfoVisible = !Utilities.HasBoxPoints(rawOcrResult);
        AppliedResultQuality = translationFailureCount > 0
            ? PinnedImageTranslateResultQuality.Partial
            : PinnedImageTranslateResultQuality.Full;
        HasPendingChanges = CapturePendingOptions() != _appliedSnapshot;
        RefreshDisplayState();

        if (translationFailureCount > 0)
            ShowStatus(
                string.Format(_i18n.GetTranslation("ImageTranslatePinnedPartialFailure"), translationFailureCount),
                Severity.Warning);
        else
            ClearTransientStatus();
    }

    private PinnedImageTranslateStage DetermineEarliestStage(
        PinnedImageTranslateOptionsSnapshot pending,
        bool forceFull) =>
        PinnedImageTranslateInvalidation.GetEarliestStage(
            pending,
            _appliedSnapshot,
            hasRawOcr: _rawOcrResult != null,
            hasLayout: _layoutSourceBlocks != null,
            hasOverlay: _resultOverlayDocument != null,
            forceFull);

    private PinnedImageTranslateOptionsSnapshot CapturePendingOptions() =>
        new(
            _selectedOcrServiceId,
            _selectedTranslateServiceId,
            OcrLanguage,
            LayoutAnalysisMode,
            SourceLanguage,
            TargetLanguage);

    private PinnedImageTranslateOperationSnapshot CaptureOperationSnapshot() =>
        new(
            CapturePendingOptions(),
            _settings.ImageTranslateLanguageDetector,
            _settings.ImageTranslateLocalDetectorRate,
            _settings.ImageTranslateSourceLangIfAuto,
            _settings.ImageTranslateFirstLanguage,
            _settings.ImageTranslateSecondLanguage);

    private Service? ResolveOcrService(string serviceId) =>
        _ocrService.Services.FirstOrDefault(service =>
            service.ServiceID == serviceId &&
            _ocrService.IsImageTranslateOcrService(service));

    private Service? ResolveTranslateService(string serviceId) =>
        _translateService.Services.FirstOrDefault(service =>
            service.ServiceID == serviceId && service.Plugin is ITranslatePlugin);

    private void RefreshOcrEngines()
    {
        OcrEngines.Clear();
        foreach (var service in _ocrService.GetImageTranslateOcrServices())
            OcrEngines.Add(service);
    }

    private void OnOcrServicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var selectedServiceId = _selectedOcrServiceId;
        Service? selectedService;
        _suppressComputeOptionChanged = true;
        try
        {
            // 刷新列表时抑制 ComboBox 的临时选中变化。
            RefreshOcrEngines();
            selectedService = OcrEngines.FirstOrDefault(service => service.ServiceID == selectedServiceId);
            SelectedOcrEngine = selectedService;
        }
        finally
        {
            _suppressComputeOptionChanged = false;
        }

        if (selectedService != null)
            return;

        if (string.IsNullOrEmpty(selectedServiceId))
            return;

        InvalidateCurrentMeaning();
        HasPendingChanges = true;
        ShowStatus(_i18n.GetTranslation("ImageTranslateOcrServiceNotFoundMessage"), Severity.Warning);
    }

    private void OnTranslateServicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _transCollectionView.View.Refresh();

        var selectedService = _translateService.Services.FirstOrDefault(service =>
            service.ServiceID == _selectedTranslateServiceId && service.Plugin is ITranslatePlugin);
        if (selectedService != null)
        {
            SetSelectedTranslateEngineWithoutApply(selectedService);
            return;
        }

        if (string.IsNullOrEmpty(_selectedTranslateServiceId))
            return;

        SetSelectedTranslateEngineWithoutApply(null);
        InvalidateCurrentMeaning();
        HasPendingChanges = true;
        ShowStatus(_i18n.GetTranslation("ImageTranslateServiceNotFoundMessage"), Severity.Warning);
    }

    private void SetSelectedOcrEngineWithoutApply(Service? service)
    {
        _suppressComputeOptionChanged = true;
        try
        {
            SelectedOcrEngine = service;
        }
        finally
        {
            _suppressComputeOptionChanged = false;
        }
    }

    private void SetSelectedTranslateEngineWithoutApply(Service? service)
    {
        _suppressComputeOptionChanged = true;
        try
        {
            SelectedTranslateEngine = service;
        }
        finally
        {
            _suppressComputeOptionChanged = false;
        }
    }

    private void OnTransFilter(object sender, FilterEventArgs e) =>
        e.Accepted = e.Item is Service service && service.Plugin is ITranslatePlugin;

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Settings.ColorScheme) || _translatedBlocks == null)
            return;

        var overlayTheme = _settings.ColorScheme == ElementTheme.Dark
            ? ImageTranslateOverlayTheme.Dark
            : ImageTranslateOverlayTheme.Light;
        _resultOverlayDocument = ImageTranslateRenderer.CreateTranslatedOverlay(_translatedBlocks, overlayTheme);
        _translatedSelectionWords = new ObservableCollection<OcrWord>(_resultOverlayDocument.SelectableWords);
        RefreshDisplayState();
    }

    private void RefreshDisplayState()
    {
        if (_source == null)
            return;

        if (IsShowingAnnotated)
        {
            EnsureAnnotatedImage();
            DisplayImage = _annotatedImage ?? _source.Image;
            DisplayOverlayDocument = null;
            OcrWords = _originalSelectionWords;
        }
        else
        {
            DisplayImage = _source.Image;
            DisplayOverlayDocument = _resultOverlayDocument;
            OcrWords = _resultOverlayDocument == null ? _originalSelectionWords : _translatedSelectionWords;
        }
    }

    private void EnsureAnnotatedImage()
    {
        if (_annotatedImage != null || _source == null || _rawOcrResult == null || _layoutSourceBlocks == null)
            return;

        // 检测图按需生成，避免长期保留第二张全尺寸位图。
        _annotatedImage = ImageTranslateRenderer.GenerateAnnotatedImage(
            CreateLayoutProjection(_rawOcrResult, _layoutSourceBlocks),
            _source.Image);
    }

    private bool CanCommit(long generation) => !_disposed && generation == _operationGeneration;

    private void ShowStatus(string message, Severity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusVisible = true;
    }

    private void ClearTransientStatus()
    {
        if (HasPendingChanges)
            return;

        IsStatusVisible = false;
        StatusMessage = string.Empty;
        StatusSeverity = Severity.Informational;
    }

    private static OcrResult CreateLayoutProjection(OcrResult raw, IReadOnlyList<OcrLayoutBlock> blocks) =>
        new()
        {
            OcrContents = blocks.Select(block => block.ToOcrContent()).ToList(),
            Language = raw.Language,
            Duration = raw.Duration,
            IsSuccess = raw.IsSuccess,
            ErrorMessage = raw.ErrorMessage,
        };

    private static List<OcrLayoutBlock> CloneLayoutBlocks(IEnumerable<OcrLayoutBlock> blocks) =>
        blocks.Select(block => new OcrLayoutBlock
        {
            Text = block.Text,
            BoxPoints = block.BoxPoints.Select(point => new BoxPoint(point.X, point.Y)).ToList(),
            LineBoxPoints = block.LineBoxPoints
                .Select(line => line.Select(point => new BoxPoint(point.X, point.Y)).ToList())
                .ToList(),
            Source = block.Source,
            Confidence = block.Confidence,
        }).ToList();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Interlocked.Increment(ref _operationGeneration);
        _debounceCts?.Cancel();
        _operationCts?.Cancel();
        _debounceCts?.Dispose();
        _operationCts?.Dispose();
        _transCollectionView.Filter -= OnTransFilter;
        _ocrService.Services.CollectionChanged -= OnOcrServicesCollectionChanged;
        _translateService.Services.CollectionChanged -= OnTranslateServicesCollectionChanged;
        _settings.PropertyChanged -= OnSettingsPropertyChanged;

        _source = null;
        _rawOcrResult = null;
        _layoutSourceBlocks = null;
        _translatedBlocks = null;
        _annotatedImage = null;
        _resultOverlayDocument = null;
        DisplayImage = null;
        DisplayOverlayDocument = null;
        OcrWords = [];
        GC.SuppressFinalize(this);
    }

    private readonly record struct TranslationRunResult(int TranslatableCount, int SuccessCount, int FailureCount);
}
