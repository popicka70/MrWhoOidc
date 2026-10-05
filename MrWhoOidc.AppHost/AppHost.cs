var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent)
    .WithPgAdmin();
var authDb = postgres.AddDatabase("authdb");

// Dev client secrets are generated per developer (persisted in this AppHost's user-secrets) and
// shared between the server seeder and the example apps, so no secret is committed to the repo.
// Note: the seeder only sets a secret when it creates the client; with an existing database volume,
// set Parameters:*-client-secret in the AppHost user-secrets to the already-seeded values.
var testApiClientSecret = builder.AddParameter(
    "test-api-client-secret", new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true, persist: true);
var blazorWebClientSecret = builder.AddParameter(
    "blazor-web-client-secret", new GenerateParameterDefault { MinLength = 48, Special = false }, secret: true, persist: true);

var webAuth = builder.AddProject<Projects.MrWhoOidc_WebAuth>("mrwhooidc-webauth")
    .WithReference(authDb)
    .WithEnvironment("SEED_TEST_API_CLIENT_SECRET", testApiClientSecret)
    .WithEnvironment("SEED_BLAZOR_WEB_CLIENT_SECRET", blazorWebClientSecret)
    .WaitFor(authDb);

var examplesApi = builder.AddProject<Projects.MrWhoOidc_TestApi>("examples-testapi")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithEnvironment("MrWhoOidc__ClientSecret", testApiClientSecret)
    .WaitFor(webAuth);

builder.AddProject<Projects.MrWhoOidc_RazorClient>("razorclient")
    .WithEnvironment("MrWhoOidc__ClientSecret", blazorWebClientSecret)
    .WithReference(examplesApi)
    .WaitFor(examplesApi)
    .WaitFor(webAuth);

builder.Build().Run();
