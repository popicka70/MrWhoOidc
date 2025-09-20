var builder = DistributedApplication.CreateBuilder(args);

var apiService = builder.AddProject<Projects.MrWhoOidc_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.MrWhoOidc_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.AddProject<Projects.MrWhoOidc_WebAuth>("mrwhooidc-webauth");

builder.Build().Run();
