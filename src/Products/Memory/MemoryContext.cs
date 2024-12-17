using Microsoft.EntityFrameworkCore;
using SearchEntities;
using DataEntities;
using OpenAI.Chat;
using OpenAI.Embeddings;
using VectorEntities;
using Microsoft.Extensions.VectorData;
using Newtonsoft.Json;
using Products.Models;

namespace Products.Memory;

public class MemoryContext(
    ILogger logger,
    ChatClient chatClient,
    EmbeddingClient embeddingClient,
    IVectorStore vectorStore,
    Context db)
{
    public IVectorStoreRecordCollection<int, ProductVector>? _productsCollection;
    private string _systemPrompt = "You are a useful assistant. You always reply with a short and funny message. If you do not know an answer, you say 'I don't know that.' You only answer questions related to outdoor camping products. For any other type of questions, explain to the user that you only answer outdoor camping products questions. Do not store memory of the chat conversation.";

    public async Task<bool> InitMemoryContextAsync()
    {
        logger.LogInformation("Initializing memory context");
        _productsCollection = vectorStore.GetCollection<int, ProductVector>("products");
        await _productsCollection.CreateCollectionIfNotExistsAsync();

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

                productVector.Vector = result.Value.ToFloats();
                var recordId = await _productsCollection.UpsertAsync(productVector);
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
            var vectorSearchQuery = result.Value.ToFloats();

            var searchOptions = new VectorSearchOptions()
            {
                Top = 1,
                VectorPropertyName = "Vector"
            };

            // search the vector database for the most similar product        
            var searchResults = await _productsCollection.VectorizedSearchAsync(vectorSearchQuery, searchOptions);
            double searchScore = 0.0;
            string? responseText;
            await foreach (var searchItem in searchResults.Results)
            {
                if (searchItem is not null && searchItem.Score > 0.5)
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

                    searchScore = searchItem.Score.Value;
                    responseText = $"The product [{firstProduct.Name}] fits with the search criteria [{search}][{searchItem.Score.Value.ToString("0.00")}]";
                    logger.LogInformation("Search Response: {ResponseText}", responseText);
                }
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
                new SystemChatMessage(_systemPrompt),
                new UserChatMessage(prompt)
            };

            logger.LogInformation("{ChatHistory}", JsonConvert.SerializeObject(messages));

            var resultPrompt = await chatClient.CompleteChatAsync(messages);
            responseText = resultPrompt.Value.Content[0].Text!;

            // create a response object
            response = new SearchResponse
            {
                Products = firstProduct == null ? [new Product()] : [firstProduct],
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