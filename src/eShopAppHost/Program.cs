var builder = DistributedApplication.CreateBuilder(args);

var sqldb = builder.AddSqlServer("sql")
    .WithDataVolume()
    .AddDatabase("sqldb");

var products = builder.AddProject<Projects.Products>("products")
    .WithReference(sqldb)
    .WaitFor(sqldb);

var store = builder.AddProject<Projects.Store>("store")
        .WithReference(products)
        .WaitFor(products)
        .WithExternalHttpEndpoints();

if (builder.ExecutionContext.IsPublishMode)
{
    var appInsights = builder.AddAzureApplicationInsights("appInsights");


    var chatDeploymentName = "gpt-4o-mini";
    var embeddingsDeploymentName = "text-embedding-ada-002";
    var aoai = builder.AddAzureOpenAI("openai")
        .AddDeployment(new AzureOpenAIDeployment(chatDeploymentName,
            "gpt-4o-mini",
            "2024-07-18",
            "GlobalStandard",
            10))
        .AddDeployment(new AzureOpenAIDeployment(embeddingsDeploymentName,
            "text-embedding-ada-002",
            "2"));

    products
        .WithReference(aoai)
        .WithEnvironment("AI_ChatDeploymentName", chatDeploymentName)
        .WithEnvironment("AI_embeddingsDeploymentName", embeddingsDeploymentName)
        .WithReference(appInsights);

    store.WithReference(appInsights);

    products.WithReference(appInsights)
        .WithReference(aoai)
        .WithEnvironment("AI_ChatDeploymentName", chatDeploymentName)
        .WithEnvironment("AI_embeddingsDeploymentName", embeddingsDeploymentName);

    store.WithReference(appInsights)
        .WithExternalHttpEndpoints();
}
else
{
    var ollama = builder.AddOllama("ollama")
        .WithDataVolume()
        .WithGPUSupport()
        .WithOpenWebUI();

    var chat = ollama.AddModel("chat", "llama3.2");
    var embeddings = ollama.AddModel("embeddings", "all-minilm");

    products
        .WithReference(chat)
        .WithReference(embeddings)
        .WaitFor(chat)
        .WaitFor(embeddings);
}

builder.Build().Run();
