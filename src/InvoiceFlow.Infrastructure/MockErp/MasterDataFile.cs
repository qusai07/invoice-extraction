using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using InvoiceFlow.Domain.MasterData;

namespace InvoiceFlow.Infrastructure.MockErp;

/// <summary>JSON snapshot of ERP master data. Used by the mock ERP, the eval dataset and local development.</summary>
public sealed record MasterDataFile
{
    /// <summary>Snapshot date. Evals validate as of this date so results don't drift with the calendar.</summary>
    public DateOnly? AsOf { get; init; }

    public IReadOnlyList<string> CompanyTrns { get; init; } = [];
    public IReadOnlyList<Supplier> Suppliers { get; init; } = [];
    public IReadOnlyList<PurchaseOrder> PurchaseOrders { get; init; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MasterDataFile Load(string path) =>
        JsonSerializer.Deserialize<MasterDataFile>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"{path} is empty.");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions) + Environment.NewLine);
}
