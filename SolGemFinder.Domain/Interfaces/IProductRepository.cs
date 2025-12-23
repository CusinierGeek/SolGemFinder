using System.Collections.Generic;
using System.Threading.Tasks;
using SolGemFinder.Domain.Models;

namespace SolGemFinder.Domain.Interfaces;

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(int id);
    Task<IEnumerable<Product>> GetAllAsync();
    Task AddAsync(Product product);
}
