var builder = DistributedApplication.CreateBuilder(args);

var apiService = builder.AddProject<Projects.MrWhoOidc_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.MrWhoOidc_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

// Add a PostgreSQL server and a database for auth persistence
var postgres = builder.AddPostgres("postgres");
var authDb = postgres.AddDatabase("authdb");

builder.AddProject<Projects.MrWhoOidc_WebAuth>("mrwhooidc-webauth")
    .WithReference(authDb)
    .WaitFor(authDb);

builder.Build().Run();
