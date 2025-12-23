using Microsoft.EntityFrameworkCore;
using SolGemFinder.Domain.Interfaces;
using SolGemFinder.Domain.Models;
using SolGemFinder.Infrastructure.Data;

namespace SolGemFinder.Infrastructure.Repositories;

public class ProductRepository : IProductRepository
{
    private readonly AppDbContext _db;

    public ProductRepository(AppDbContext db) => _db = db;

    public async Task AddAsync(Product product)
    {
        _db.Products.Add(product);
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<Product>> GetAllAsync() => await _db.Products.ToListAsync();

    public async Task<Product?> GetByIdAsync(int id) => await _db.Products.FindAsync(id);
}
