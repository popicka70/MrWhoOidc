var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);
var authDb = postgres.AddDatabase("authdb");

var apiService = builder.AddProject<Projects.MrWhoOidc_ApiService>("apiservice")
    .WithReference(authDb)
    .WithHttpHealthCheck("/health")
    .WaitFor(authDb);

builder.AddProject<Projects.MrWhoOidc_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

var webAuth = builder.AddProject<Projects.MrWhoOidc_WebAuth>("mrwhooidc-webauth")
    .WithReference(authDb)
    .WaitFor(authDb);

var examplesApi = builder.AddProject<Projects.MrWhoOidc_TestApi>("examples-testapi")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WaitFor(webAuth);

builder.AddProject<Projects.MrWhoOidc_RazorClient>("razorclient")
    .WithReference(examplesApi)
    .WaitFor(examplesApi)
    .WaitFor(webAuth);

builder.Build().Run();
