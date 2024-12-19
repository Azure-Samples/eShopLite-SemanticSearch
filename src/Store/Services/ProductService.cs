using SearchEntities;
using DataEntities;
using System.Text.Json;

namespace Store.Services;

public class ProductService(HttpClient httpClient, ILogger<ProductService> logger)
{
    public async Task<List<Product>> GetProducts()
    {
        List<Product>? products = null;
        try
        {
            var response = await httpClient.GetAsync("/api/product");
            var responseText = await response.Content.ReadAsStringAsync();

            logger.LogInformation("Http status code: {StatusCode}", response.StatusCode);
            logger.LogInformation("Http response content: {responseText}", responseText);

            if (response.IsSuccessStatusCode)
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                products = await response.Content.ReadFromJsonAsync(ProductSerializerContext.Default.ListProduct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during GetProducts.");
        }

        return products ?? new List<Product>();
    }

    public async Task<SearchResponse> Search(string searchTerm, bool semanticSearch = false)
    {
        try
        {
            // call the desired Endpoint
            HttpResponseMessage response = semanticSearch switch
            {
                true => await httpClient.GetAsync($"/api/aisearch/{searchTerm}"),
                false => await httpClient.GetAsync($"/api/product/search/{searchTerm}")
            };

            var responseText = await response.Content.ReadAsStringAsync();

            logger.LogInformation("Http status code: {StatusCode}", response.StatusCode);
            logger.LogInformation("Http response content: {responseText}", responseText);

            if (response.IsSuccessStatusCode)
            {
                var searchResponse = await response.Content.ReadFromJsonAsync<SearchResponse>();
                if (searchResponse is not null)
                {
                    return searchResponse;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during Search.");
        }

        return new SearchResponse { Response = "No response" };
    }
}
