var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent)
    .WithPgAdmin();
var authDb = postgres.AddDatabase("authdb");

// The TestApi client secret comes from an Aspire secret parameter (user-secrets key
// "Parameters:test-api-client-secret"; the dashboard prompts for it when missing), not a literal in source.
var testApiClientSecret = builder.AddParameter("test-api-client-secret", secret: true);

var webAuth = builder.AddProject<Projects.MrWhoOidc_WebAuth>("mrwhooidc-webauth")
    .WithReference(authDb)
    .WithEnvironment("SEED_TEST_API_CLIENT_SECRET", testApiClientSecret)
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
