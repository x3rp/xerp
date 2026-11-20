using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Infrastructure.Persistence;

namespace Xerp.Infrastructure.Tenancy;

public sealed class TenantDirectory(XerpDbContext db) : ITenantDirectory
{
    public Task<Guid?> FindActiveByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        db.Tenants.AsNoTracking()
            .Where(t => t.IsActive && EF.Property<string>(t, DbNames.CodeLower) == DbText.Lower(code))
            .Select(t => (Guid?)t.Id)
            .SingleOrDefaultAsync(cancellationToken);
}
