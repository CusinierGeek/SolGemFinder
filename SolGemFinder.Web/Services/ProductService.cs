using SolGemFinder.Shared.Dtos;
using System.Net.Http.Json;

namespace SolGemFinder.Web.Services;

public class ProductService
{
    private readonly HttpClient _http;

    public ProductService(HttpClient http) => _http = http;

    public async Task<IEnumerable<ProductDto>> GetAllAsync() => await _http.GetFromJsonAsync<IEnumerable<ProductDto>>("/products") ?? Enumerable.Empty<ProductDto>();
}
