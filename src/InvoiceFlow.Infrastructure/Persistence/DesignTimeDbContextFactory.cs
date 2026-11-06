using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace InvoiceFlow.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef` (migrations). The connection string is not used to connect when adding migrations.
/// <code>dotnet ef migrations add &lt;Name&gt; --project src/InvoiceFlow.Infrastructure --output-dir Persistence/Migrations</code>
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<InvoiceFlowDbContext>
{
    public InvoiceFlowDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<InvoiceFlowDbContext>()
            .UseSqlServer(Environment.GetEnvironmentVariable("INVOICEFLOW_DESIGN_CONNECTION")
                          ?? "Server=localhost,14330;Database=InvoiceFlow;Integrated Security=false;TrustServerCertificate=True")
            .Options);
}
