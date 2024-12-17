using Microsoft.EntityFrameworkCore;
using SearchEntities;
using DataEntities;
using VectorEntities;
using Microsoft.Extensions.VectorData;
using Newtonsoft.Json;
using Products.Models;
using Microsoft.Extensions.AI;

namespace Products.Memory;

public record ScoreThreshold(double Value);

public class MemoryContext(
    ILogger<MemoryContext> logger,
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingClient,
    IVectorStoreRecordCollection<int, ProductVector> productsCollection,
    Context db,
    ScoreThreshold scoreThreshold)
{
    private string _systemPrompt = "You are a useful assistant. You always reply with a short and funny message. If you do not know an answer, you say 'I don't know that.' You only answer questions related to outdoor camping products. For any other type of questions, explain to the user that you only answer outdoor camping products questions. Do not store memory of the chat conversation.";

    public async Task<bool> InitMemoryContextAsync()
    {
        logger.LogInformation("Initializing memory context");
        await productsCollection.CreateCollectionIfNotExistsAsync();

        // define system prompt
        _systemPrompt = "You are a useful assistant. You always reply with a short and funny message. If you do not know an answer, you say 'I don't know that.' You only answer questions related to outdoor camping products. For any other type of questions, explain to the user that you only answer outdoor camping products questions. Do not store memory of the chat conversation.";

        logger.LogInformation("Get a copy of the list of products");
        // get a copy of the list of products
        var products = await db.Product.ToListAsync();

        logger.LogInformation("Filling products in memory");

        // iterate over the products and add them to the memory
        foreach (var product in products)
        {
            try
            {
                logger.LogInformation("Adding product to memory: {Product}", product.Name);
                var productInfo = $"[{product.Name}] is a product that costs [{product.Price}] and is described as [{product.Description}]";

                // new product vector
                var productVector = new ProductVector
                {
                    Id = product.Id,
                    Name = product.Name,
                    Description = product.Description,
                    Price = product.Price,
                    ImageUrl = product.ImageUrl,
                };
                var result = await embeddingClient.GenerateEmbeddingAsync(productInfo);

                productVector.Vector = result.Vector;
                var recordId = await productsCollection.UpsertAsync(productVector);
                logger.LogInformation("Product added to memory: {Product} with recordId: {RecordId}", product.Name, recordId);
            }
            catch (Exception exc)
            {
                logger.LogError(exc, "Error adding product to memory");
            }
        }

        logger.LogInformation("DONE! Filling products in memory");
        return true;
    }

    public async Task<SearchResponse> Search(string search)
    {
        var response = new SearchResponse
        {
            Response = $"I don't know the answer for your question. Your question is: [{search}]"
        };
        Product? firstProduct = null;
        try
        {
            var result = await embeddingClient.GenerateEmbeddingAsync(search);
            var vectorSearchQuery = result.Vector;

            var searchOptions = new VectorSearchOptions()
            {
                Top = 1,
                VectorPropertyName = "Vector"
            };

            // search the vector database for the most similar product
            var searchResults = await productsCollection.VectorizedSearchAsync(vectorSearchQuery, searchOptions);
            string? responseText = null;
            await foreach (var searchItem in searchResults.Results)
            {
                if (searchItem is not null && searchItem.Score > scoreThreshold.Value)
                {
                    // product found, search the db for the product details
                    firstProduct = new Product
                    {
                        Id = searchItem.Record.Id,
                        Name = searchItem.Record.Name,
                        Description = searchItem.Record.Description,
                        Price = searchItem.Record.Price,
                        ImageUrl = searchItem.Record.ImageUrl
                    };

                    responseText = $"The product [{firstProduct.Name}] fits with the search criteria [{search}][{searchItem.Score.Value.ToString("0.00")}]";
                    logger.LogInformation("Search Response: {ResponseText}", responseText);
                }


                // let's improve the response message
                var prompt = $"""
                    You are an intelligent assistant helping clients with their search about outdoor products. Generate a catchy and friendly message using the following information:
                        - User Question: {search}
                        - Found Product Name: {firstProduct.Name}
                        - Found Product Description: {firstProduct.Description}
                        - Found Product Price: {firstProduct.Price}
                    Include the found product information in the response to the user question.
                """;

                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, _systemPrompt),
                    new(ChatRole.User, prompt)
                };

                logger.LogInformation("{ChatHistory}", JsonConvert.SerializeObject(messages));

                var resultPrompt = await chatClient.CompleteAsync(messages);
                responseText = resultPrompt.Message.Text;
            }

            if (firstProduct is null)
            {
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, _systemPrompt),
                    new(ChatRole.User, $"The search term: {search} did not match any products.")
                };

                logger.LogInformation("{ChatHistory}", JsonConvert.SerializeObject(messages));

                var resultPrompt = await chatClient.CompleteAsync(messages);
                responseText = resultPrompt.Message.Text;
            }

            // create a response object
            response = new SearchResponse
            {
                Products = [firstProduct ?? new Product()],
                Response = responseText
            };

        }
        catch (Exception ex)
        {
            // Handle exceptions (log them, rethrow, etc.)
            response.Response = $"An error occurred: {ex.Message}";
        }
        return response;
    }
}
