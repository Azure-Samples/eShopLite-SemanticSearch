using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.InMemory;
using OpenAI;
using OpenAI.VectorStores;
using Products.Endpoints;
using Products.Memory;
using Products.Models;
using VectorEntities;

var builder = WebApplication.CreateBuilder(args);

// Disable Globalization Invariant Mode
Environment.SetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "false");

// add aspire service defaults
builder.AddServiceDefaults();

builder.Services.AddProblemDetails();

// Add DbContext service
builder.AddSqlServerDbContext<Context>("sqldb");

if (builder.Environment.IsDevelopment())
{
    builder.AddOllamaSharpChatClient("chat");
    builder.AddOllamaSharpEmbeddingGenerator("embeddings");

    builder.Services.AddSingleton(new ScoreThreshold(0.4));
}
else
{
    // in dev scenarios rename this to "openaidev", and check the documentation to reuse existing AOAI resources
    var azureOpenAiClientName = "openai";
    builder.AddAzureOpenAIClient(azureOpenAiClientName);

    // get azure openai client and create Chat client from aspire hosting configuration
    builder.Services.AddSingleton(serviceProvider =>
    {
        var chatDeploymentName = "gpt-4o-mini";
        var logger = serviceProvider.GetService<ILogger<Program>>()!;
        logger.LogInformation("Chat client configuration, modelId: {chatDeploymentName}", chatDeploymentName);
        try
        {
            OpenAIClient client = serviceProvider.GetRequiredService<OpenAIClient>();
            return client.GetChatClient(chatDeploymentName).AsChatClient();
        }
        catch (Exception exc)
        {
            logger.LogError(exc, "Error creating embeddings client");
            throw;
        }
    });

    // get azure openai client and create embedding client from aspire hosting configuration
    builder.Services.AddSingleton(serviceProvider =>
    {
        var embeddingsDeploymentName = "text-embedding-ada-002";
        var logger = serviceProvider.GetService<ILogger<Program>>()!;
        logger.LogInformation("Embeddings client configuration, modelId: {embeddingsDeploymentName}", embeddingsDeploymentName);
        try
        {
            OpenAIClient client = serviceProvider.GetRequiredService<OpenAIClient>();
            return client.GetEmbeddingClient(embeddingsDeploymentName).AsEmbeddingGenerator();
        }
        catch (Exception exc)
        {
            logger.LogError(exc, "Error creating embeddings client");
            throw;
        }
    });

    AppContext.SetSwitch("OpenAI.Experimental.EnableOpenTelemetry", true);
    builder.Services.AddSingleton(new ScoreThreshold(0.8));
}

// add memory context
builder.Services.AddScoped<MemoryContext>();

builder.Services.AddSingleton<IVectorStore, InMemoryVectorStore>();

builder.Services.AddSingleton(sp =>
{
    var vectorStore = sp.GetRequiredService<IVectorStore>();
    return vectorStore.GetCollection<int, ProductVector>("products");
});

// Add services to the container.
var app = builder.Build();

// aspire map default endpoints
app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

app.MapProductEndpoints();

app.UseStaticFiles();

// manage db
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<Context>();
    try
    {
        app.Logger.LogInformation("Ensure database created");
        context.Database.EnsureCreated();
    }
    catch (Exception exc)
    {
        app.Logger.LogError(exc, "Error creating database");
    }
    DbInitializer.Initialize(context);

    var memoryContext = scope.ServiceProvider.GetRequiredService<MemoryContext>();
    await memoryContext.InitMemoryContextAsync();
}

app.Run();