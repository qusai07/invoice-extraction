namespace InvoiceFlow.Extraction.AzureOpenAi;

/// <summary>Config section: <c>Extraction:AzureOpenAI</c>.</summary>
public sealed class AzureOpenAiExtractorOptions
{
    public const string SectionName = "Extraction:AzureOpenAI";

    /// <summary>e.g. https://my-resource.openai.azure.com/ (use a UAE North resource for production).</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Deployment name of a vision-capable model that supports structured outputs.</summary>
    public string? Deployment { get; set; }

    /// <summary>Optional. When empty, Microsoft Entra ID (DefaultAzureCredential / managed identity) is used.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Leave null for models that don't accept temperature (reasoning models).</summary>
    public float? Temperature { get; set; }

    public int MaxOutputTokens { get; set; } = 8000;

    public int Dpi { get; set; } = 200;

    public int MaxPages { get; set; } = 10;

    /// <summary>"high" (default, best for small print) or "low".</summary>
    public string ImageDetail { get; set; } = "high";

    /// <summary>Numeric dates like 03/04/2026 read as day-first (UAE/GCC).</summary>
    public bool DayFirstDates { get; set; } = true;

    /// <summary>USD per 1M input tokens, for cost reporting. Set from your Azure price sheet.</summary>
    public decimal InputCostPerMillionTokens { get; set; }

    /// <summary>USD per 1M output tokens, for cost reporting.</summary>
    public decimal OutputCostPerMillionTokens { get; set; }
}
