using Microsoft.EntityFrameworkCore;
using SolGemFinder.Domain.Models;

namespace SolGemFinder.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();
}
