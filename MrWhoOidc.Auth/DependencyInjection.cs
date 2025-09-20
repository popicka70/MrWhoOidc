using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddMrWhoOidcAuthCore(this IServiceCollection services)
    {
        services.AddScoped<IKeyStore, KeyStore>();
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IClientStore, ClientStore>();
        services.AddScoped<IAuthorizeService, AuthorizeService>();
        services.AddScoped<IAuthorizationCodeService, AuthorizationCodeService>();
        services.AddSingleton<IAuthorizationCodeMetadataStore, InMemoryAuthorizationCodeMetadataStore>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IConsentService, ConsentService>();
        services.AddScoped<ITokenValidator, TokenValidator>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<IRevocationService, RevocationService>();

        // Key rotation options and services
        services.AddOptions<KeyRotationOptions>();
        services.AddScoped<IKeyRotationService, KeyRotationService>();
        services.AddHostedService<KeyRotationHostedService>();

        return services;
    }
}
