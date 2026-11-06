using InvoiceFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlow.Infrastructure.Identity;

/// <summary>
/// Turns stored actor ids (user GUIDs) into display names for screens. Ids that aren't users ("system",
/// development header names) are simply not in the result; callers show them as they are.
/// </summary>
public static class UserNames
{
    public static async Task<Dictionary<string, string>> ResolveAsync(InvoiceFlowDbContext db, IEnumerable<string?> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var guids = ids.Where(i => i is not null).Distinct()
            .Select(i => Guid.TryParse(i, out var g) ? g : (Guid?)null).Where(g => g is not null).Select(g => g!.Value).ToList();
        if (guids.Count == 0)
        {
            return [];
        }

        var users = await db.Users.AsNoTracking().Where(u => guids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName }).ToListAsync(ct).ConfigureAwait(false);
        return users.ToDictionary(u => u.Id.ToString(), u => u.DisplayName, StringComparer.OrdinalIgnoreCase);
    }
}
