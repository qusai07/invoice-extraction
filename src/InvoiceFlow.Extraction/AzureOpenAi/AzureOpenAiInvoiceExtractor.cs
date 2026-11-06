using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Extraction.Contract;
using InvoiceFlow.Extraction.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace InvoiceFlow.Extraction.AzureOpenAi;

/// <summary>
/// Extracts invoices with an Azure OpenAI vision model using strict structured outputs (JSON schema).
/// The model only transcribes; normalization happens in <see cref="RawInvoiceMapper"/>.
/// </summary>
public sealed partial class AzureOpenAiInvoiceExtractor : IInvoiceExtractor
{
    private readonly AzureOpenAiExtractorOptions _options;
    private readonly ChatClient _chat;
    private readonly ILogger<AzureOpenAiInvoiceExtractor> _logger;

    public AzureOpenAiInvoiceExtractor(IOptions<AzureOpenAiExtractorOptions> options, ILogger<AzureOpenAiInvoiceExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;

        if (_options.Endpoint is null || string.IsNullOrWhiteSpace(_options.Deployment))
        {
            throw new InvalidOperationException(
                $"Configure {AzureOpenAiExtractorOptions.SectionName}:Endpoint and :Deployment.");
        }

        var client = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? new AzureOpenAIClient(_options.Endpoint, new DefaultAzureCredential())
            : new AzureOpenAIClient(_options.Endpoint, new AzureKeyCredential(_options.ApiKey));

        _chat = client.GetChatClient(_options.Deployment);
    }

    public string Name => "azure-openai";

    public async Task<ExtractionResult> ExtractAsync(DocumentInput document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var stopwatch = Stopwatch.StartNew();

        var raster = DocumentRasterizer.Rasterize(document, _options.Dpi, _options.MaxPages);
        if (raster.Error is not null)
        {
            return ExtractionResult.Fail(raster.Error, Usage(stopwatch, raster.Pages.Count, null));
        }

        var messages = BuildMessages(raster);
        var chatOptions = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "invoice_extraction",
                jsonSchema: BinaryData.FromString(ExtractionContract.Schema),
                jsonSchemaIsStrict: true),
            MaxOutputTokenCount = _options.MaxOutputTokens,
        };
        if (_options.Temperature is { } temperature)
        {
            chatOptions.Temperature = temperature;
        }

        ChatCompletion completion;
        try
        {
            completion = (await _chat.CompleteChatAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (ClientResultException ex)
        {
            LogProviderError(_logger, ex, document.FileName, ex.Status);
            return ExtractionResult.Fail($"Provider error {ex.Status}: {ex.Message}", Usage(stopwatch, raster.Pages.Count, null));
        }

        var usage = Usage(stopwatch, raster.Pages.Count, completion);

        if (!string.IsNullOrEmpty(completion.Refusal))
        {
            return ExtractionResult.Fail($"Model refused: {completion.Refusal}", usage);
        }

        if (completion.FinishReason == ChatFinishReason.Length)
        {
            return ExtractionResult.Fail("Model output was cut off (MaxOutputTokens too low for this document).", usage);
        }

        var json = completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;

        RawExtraction raw;
        try
        {
            raw = ExtractionContract.Deserialize(json);
        }
        catch (JsonException ex)
        {
            return ExtractionResult.Fail($"Invalid JSON from model: {ex.Message}", usage);
        }

        var mapped = RawInvoiceMapper.Map(raw, _options.DayFirstDates);
        var issues = mapped.Issues.ToList();
        if (raster.Truncated)
        {
            issues.Add($"document has {raster.TotalPages} pages; only the first {raster.Pages.Count} were read");
        }

        return new ExtractionResult
        {
            Status = mapped.DocumentType is DocumentType.Other ? ExtractionStatus.NotAnInvoice : ExtractionStatus.Succeeded,
            Invoice = mapped.Invoice,
            DocumentType = mapped.DocumentType,
            Evidence = mapped.Evidence,
            Issues = issues,
            Usage = usage,
        };
    }

    private List<ChatMessage> BuildMessages(RasterizeResult raster)
    {
        var detail = string.Equals(_options.ImageDetail, "low", StringComparison.OrdinalIgnoreCase)
            ? ChatImageDetailLevel.Low
            : ChatImageDetailLevel.High;

        var parts = new List<ChatMessageContentPart>
        {
            ChatMessageContentPart.CreateTextPart($"Document with {raster.Pages.Count} page(s). Extract the invoice."),
        };
        foreach (var page in raster.Pages)
        {
            parts.Add(ChatMessageContentPart.CreateTextPart($"Page {page.PageNumber}:"));
            parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(page.Bytes), page.ContentType, detail));
        }

        return [new SystemChatMessage(ExtractionContract.Prompt), new UserChatMessage(parts)];
    }

    private ExtractionUsage Usage(Stopwatch stopwatch, int pages, ChatCompletion? completion)
    {
        long input = completion?.Usage?.InputTokenCount ?? 0;
        long output = completion?.Usage?.OutputTokenCount ?? 0;
        return new ExtractionUsage
        {
            Extractor = Name,
            Model = completion?.Model ?? _options.Deployment,
            PromptVersion = ExtractionContract.Version,
            Pages = pages,
            InputTokens = input,
            OutputTokens = output,
            CostUsd = (input * _options.InputCostPerMillionTokens + output * _options.OutputCostPerMillionTokens) / 1_000_000m,
            Duration = stopwatch.Elapsed,
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Azure OpenAI call failed for {FileName} (HTTP {Status})")]
    private static partial void LogProviderError(ILogger logger, Exception ex, string fileName, int status);
}
