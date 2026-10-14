using ComplianceMonitor.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ComplianceMonitor.Api.Data;

public class ComplianceDbContext : DbContext
{
    public ComplianceDbContext(
        DbContextOptions<ComplianceDbContext> options)
        : base(options)
    {
    }

    public DbSet<AnalysisRecord> Analyses => Set<AnalysisRecord>();
}
