using Microsoft.AspNetCore.Mvc;
using SolGemFinder.Domain.Interfaces;
using SolGemFinder.Domain.Models;
using SolGemFinder.Shared.Dtos;

namespace SolGemFinder.ApiService.Controllers;

[ApiController]
[Route("[controller]")]
public class ProductsController : ControllerBase
{
    private readonly IProductRepository _repo;

    public ProductsController(IProductRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<IEnumerable<ProductDto>> Get()
    {
        var products = await _repo.GetAllAsync();
        return products.Select(p => new ProductDto { Id = p.Id, Name = p.Name, Price = p.Price });
    }

    [HttpPost]
    public async Task<IActionResult> Post(ProductDto dto)
    {
        var p = new Product { Name = dto.Name, Price = dto.Price };
        await _repo.AddAsync(p);
        dto.Id = p.Id;
        return CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _repo.GetByIdAsync(id);
        if (p == null) return NotFound();
        return Ok(new ProductDto { Id = p.Id, Name = p.Name, Price = p.Price });
    }
}
