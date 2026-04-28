# Graph Report - MrWhoOidc.Auth/Services  (2026-04-28)

## Corpus Check
- Corpus is ~43,136 words - fits in a single context window. You may not need a graph.

## Summary
- 712 nodes · 875 edges · 60 communities detected
- Extraction: 89% EXTRACTED · 11% INFERRED · 0% AMBIGUOUS · INFERRED: 96 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- [[_COMMUNITY_Community 0|Community 0]]
- [[_COMMUNITY_Community 1|Community 1]]
- [[_COMMUNITY_Community 2|Community 2]]
- [[_COMMUNITY_Community 3|Community 3]]
- [[_COMMUNITY_Community 4|Community 4]]
- [[_COMMUNITY_Community 5|Community 5]]
- [[_COMMUNITY_Community 6|Community 6]]
- [[_COMMUNITY_Community 7|Community 7]]
- [[_COMMUNITY_Community 8|Community 8]]
- [[_COMMUNITY_Community 9|Community 9]]
- [[_COMMUNITY_Community 10|Community 10]]
- [[_COMMUNITY_Community 11|Community 11]]
- [[_COMMUNITY_Community 12|Community 12]]
- [[_COMMUNITY_Community 13|Community 13]]
- [[_COMMUNITY_Community 14|Community 14]]
- [[_COMMUNITY_Community 15|Community 15]]
- [[_COMMUNITY_Community 16|Community 16]]
- [[_COMMUNITY_Community 17|Community 17]]
- [[_COMMUNITY_Community 18|Community 18]]
- [[_COMMUNITY_Community 19|Community 19]]
- [[_COMMUNITY_Community 20|Community 20]]
- [[_COMMUNITY_Community 21|Community 21]]
- [[_COMMUNITY_Community 22|Community 22]]
- [[_COMMUNITY_Community 23|Community 23]]
- [[_COMMUNITY_Community 24|Community 24]]
- [[_COMMUNITY_Community 25|Community 25]]
- [[_COMMUNITY_Community 26|Community 26]]
- [[_COMMUNITY_Community 27|Community 27]]
- [[_COMMUNITY_Community 28|Community 28]]
- [[_COMMUNITY_Community 29|Community 29]]
- [[_COMMUNITY_Community 30|Community 30]]
- [[_COMMUNITY_Community 31|Community 31]]
- [[_COMMUNITY_Community 32|Community 32]]
- [[_COMMUNITY_Community 33|Community 33]]
- [[_COMMUNITY_Community 34|Community 34]]
- [[_COMMUNITY_Community 35|Community 35]]
- [[_COMMUNITY_Community 36|Community 36]]
- [[_COMMUNITY_Community 37|Community 37]]
- [[_COMMUNITY_Community 38|Community 38]]
- [[_COMMUNITY_Community 39|Community 39]]
- [[_COMMUNITY_Community 40|Community 40]]
- [[_COMMUNITY_Community 41|Community 41]]
- [[_COMMUNITY_Community 42|Community 42]]
- [[_COMMUNITY_Community 43|Community 43]]
- [[_COMMUNITY_Community 44|Community 44]]
- [[_COMMUNITY_Community 45|Community 45]]
- [[_COMMUNITY_Community 46|Community 46]]
- [[_COMMUNITY_Community 47|Community 47]]
- [[_COMMUNITY_Community 48|Community 48]]
- [[_COMMUNITY_Community 49|Community 49]]
- [[_COMMUNITY_Community 50|Community 50]]
- [[_COMMUNITY_Community 51|Community 51]]
- [[_COMMUNITY_Community 52|Community 52]]
- [[_COMMUNITY_Community 53|Community 53]]
- [[_COMMUNITY_Community 54|Community 54]]
- [[_COMMUNITY_Community 55|Community 55]]
- [[_COMMUNITY_Community 56|Community 56]]
- [[_COMMUNITY_Community 57|Community 57]]
- [[_COMMUNITY_Community 58|Community 58]]
- [[_COMMUNITY_Community 59|Community 59]]

## God Nodes (most connected - your core abstractions)
1. `WebAuthnService` - 19 edges
2. `IUserAccountService` - 14 edges
3. `UserAccountService` - 14 edges
4. `IClientStore` - 13 edges
5. `ClientStore` - 13 edges
6. `TenantSettingsService` - 13 edges
7. `IAuthorizationCodeMetadataStore` - 13 edges
8. `InMemoryAuthorizationCodeMetadataStore` - 13 edges
9. `KeyStore` - 11 edges
10. `EmailConfirmationService` - 9 edges

## Surprising Connections (you probably didn't know these)
- None detected - all connections are within the same source files.

## Communities

### Community 0 - "Community 0"
Cohesion: 0.0
Nodes (9): IAuthorizationCodeExchanger, IAuthorizationCodeMetadataStore, InMemoryAuthorizationCodeMetadataStore, AuthorizationCodeService, IAuthorizationCodeService, ITenantsClaimService, TenantsClaimService, IPairwiseSubjectService (+1 more)

### Community 1 - "Community 1"
Cohesion: 0.0
Nodes (13): BackgroundService, IPlatformSettingsService, ITenantIconService, BackgroundServiceTenantHelper, KeyRotationHostedService, ClientViewModelForSelection, IOboSetupOrchestrator, OboExistingClientRequest (+5 more)

### Community 2 - "Community 2"
Cohesion: 0.0
Nodes (14): ClientAuthenticationService, AuthorizeRequestValidator, ConsentProcessor, ProviderSelectionService, UserClientAssignmentService, IAuthorizeRequestValidator, IClientAuthenticationService, IConsentProcessor (+6 more)

### Community 3 - "Community 3"
Cohesion: 0.0
Nodes (6): IGlobalAuthenticationService, Failure(), Success(), GlobalAuthenticationService, IUserAccountService, UserAccountService

### Community 4 - "Community 4"
Cohesion: 0.0
Nodes (6): IKeyRotationService, KeyRotationService, IKeyStore, KeyStore, ITenantService, TenantService

### Community 5 - "Community 5"
Cohesion: 0.0
Nodes (4): CliClientService, ICliClientService, ClientStore, IClientStore

### Community 6 - "Community 6"
Cohesion: 0.0
Nodes (10): IMtlsThumbprintResolver, ITenantDiscoveryService, JsonConverter, MtlsThumbprintResolver, TenantDiscoveryService, AssertionResult, RegistrationResult, WebAuthnClientData (+2 more)

### Community 7 - "Community 7"
Cohesion: 0.0
Nodes (6): AuthorizeRequestResolver, IAuthorizeRequestResolver, EfPushedAuthorizationRequestStore, InMemoryPushedAuthorizationRequestStore, IPushedAuthorizationRequestStore, PushedAuthorizationRequestEntry

### Community 8 - "Community 8"
Cohesion: 0.0
Nodes (6): ICachedKeyProvider, CachedKeyProvider, IJarmService, JarmService, IJwtService, JwtService

### Community 9 - "Community 9"
Cohesion: 0.0
Nodes (7): ClientAssertionValidator, IClientAssertionValidator, ClientJwksResolver, IJarReplayCache, IRequestObjectValidator, RequestObjectValidationResult, RequestObjectValidator

### Community 10 - "Community 10"
Cohesion: 0.0
Nodes (3): IWebAuthnService, WebAuthnChallengeSession, WebAuthnService

### Community 11 - "Community 11"
Cohesion: 0.0
Nodes (5): IRequestObjectDecryptor, RequestObjectDecryptor, IUserAgentParser, UserAgentInfo, UserAgentParser

### Community 12 - "Community 12"
Cohesion: 0.0
Nodes (6): IRegistrationService, ISeeder, Seeder, IUserAccountProvisioner, UserAccountProvisioner, RegistrationService

### Community 13 - "Community 13"
Cohesion: 0.0
Nodes (2): IQrLoginService, QrLoginService

### Community 14 - "Community 14"
Cohesion: 0.0
Nodes (5): IdentityProviderValidator, IIdentityProviderValidator, IJwksCache, JwksCache, SectorIdentifierUriValidator

### Community 15 - "Community 15"
Cohesion: 0.0
Nodes (2): ITenantSettingsService, TenantSettingsService

### Community 16 - "Community 16"
Cohesion: 0.0
Nodes (13): Exception, WebAuthnAssertionOptions, WebAuthnAssertionResponse, WebAuthnAssertionResponseData, WebAuthnAttestationResponse, WebAuthnAttestationResponseData, WebAuthnAuthenticatorSelection, WebAuthnCredentialDescriptor (+5 more)

### Community 17 - "Community 17"
Cohesion: 0.0
Nodes (2): EmailConfirmationService, IEmailConfirmationService

### Community 18 - "Community 18"
Cohesion: 0.0
Nodes (4): IPairwiseSubjectService, ISectorIdentifierResolver, PairwiseSubjectService, SectorIdentifierResolver

### Community 19 - "Community 19"
Cohesion: 0.0
Nodes (2): ITotpService, TotpService

### Community 20 - "Community 20"
Cohesion: 0.0
Nodes (2): IUserTenantMembershipService, UserTenantMembershipService

### Community 21 - "Community 21"
Cohesion: 0.0
Nodes (2): IUserService, UserService

### Community 22 - "Community 22"
Cohesion: 0.0
Nodes (3): IScopeNameValidator, ScopeNameValidationResult, ScopeNameValidator

### Community 23 - "Community 23"
Cohesion: 0.0
Nodes (1): IWebAuthnService

### Community 24 - "Community 24"
Cohesion: 0.0
Nodes (2): IRevocationService, RevocationService

### Community 25 - "Community 25"
Cohesion: 0.0
Nodes (2): CurrentUserAccountResolver, ICurrentUserAccountResolver

### Community 26 - "Community 26"
Cohesion: 0.0
Nodes (2): ITenantSettingsService, ResolvedSettings

### Community 27 - "Community 27"
Cohesion: 0.0
Nodes (2): IScopeResolver, ScopeResolver

### Community 28 - "Community 28"
Cohesion: 0.0
Nodes (2): IScopeResolver, ScopeValidationResult

### Community 29 - "Community 29"
Cohesion: 0.0
Nodes (2): ClaimMappingService, IClaimMappingService

### Community 30 - "Community 30"
Cohesion: 0.0
Nodes (2): ITenantIconService, TenantIconData

### Community 31 - "Community 31"
Cohesion: 0.0
Nodes (2): ITenantBrandingService, TenantBranding

### Community 32 - "Community 32"
Cohesion: 0.0
Nodes (1): IGlobalAuthenticationService

### Community 33 - "Community 33"
Cohesion: 0.0
Nodes (1): IConfigurationImportService

### Community 34 - "Community 34"
Cohesion: 0.0
Nodes (1): IConfigurationExportService

### Community 35 - "Community 35"
Cohesion: 0.0
Nodes (2): IOboPolicyService, OboPolicyService

### Community 36 - "Community 36"
Cohesion: 0.0
Nodes (2): ITenantDiscoveryService, TenantInfo

### Community 37 - "Community 37"
Cohesion: 0.0
Nodes (2): ITenantBrandingService, TenantBrandingService

### Community 38 - "Community 38"
Cohesion: 0.0
Nodes (1): IPlatformSettingsService

### Community 39 - "Community 39"
Cohesion: 0.0
Nodes (2): IEmailSender, NullEmailSender

### Community 40 - "Community 40"
Cohesion: 0.0
Nodes (2): IJarReplayCache, InMemoryJarReplayCache

### Community 41 - "Community 41"
Cohesion: 0.0
Nodes (2): ClientIdGenerator, IClientIdGenerator

### Community 42 - "Community 42"
Cohesion: 0.0
Nodes (1): ICachedKeyProvider

### Community 43 - "Community 43"
Cohesion: 0.0
Nodes (3): AuthOptions, ClaimMappingRule, OpaqueAccessTokenOptions

### Community 44 - "Community 44"
Cohesion: 0.0
Nodes (2): IRoleClaimBuilder, RoleClaimBuilder

### Community 45 - "Community 45"
Cohesion: 0.0
Nodes (1): IRegistrationService

### Community 46 - "Community 46"
Cohesion: 0.0
Nodes (1): IEmailService

### Community 47 - "Community 47"
Cohesion: 0.0
Nodes (2): WebAuthnOptions, WebAuthnTenantOverrides

### Community 48 - "Community 48"
Cohesion: 0.0
Nodes (1): IMtlsThumbprintResolver

### Community 49 - "Community 49"
Cohesion: 0.0
Nodes (1): IAuthorizationCodeExchanger

### Community 50 - "Community 50"
Cohesion: 0.0
Nodes (1): IRoleClaimBuilder

### Community 51 - "Community 51"
Cohesion: 0.0
Nodes (1): ISectorIdentifierResolver

### Community 52 - "Community 52"
Cohesion: 0.0
Nodes (1): IClientAuthenticationService

### Community 53 - "Community 53"
Cohesion: 0.0
Nodes (1): IProviderSelectionService

### Community 54 - "Community 54"
Cohesion: 0.0
Nodes (1): IAuthorizeRequestValidator

### Community 55 - "Community 55"
Cohesion: 0.0
Nodes (1): IConsentProcessor

### Community 56 - "Community 56"
Cohesion: 0.0
Nodes (1): IUserClientAssignmentService

### Community 57 - "Community 57"
Cohesion: 0.0
Nodes (1): QrLoginOptions

### Community 58 - "Community 58"
Cohesion: 0.0
Nodes (1): EmailConfirmationOptions

### Community 59 - "Community 59"
Cohesion: 0.0
Nodes (1): KeyRotationOptions

## Knowledge Gaps
- **33 isolated node(s):** `WebAuthnOptions`, `WebAuthnTenantOverrides`, `ScopeValidationResult`, `QrLoginOptions`, `AuthOptions` (+28 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **Thin community `Community 13`** (17 nodes): `QrLoginService.cs`, `IQrLoginService`, `.CleanupExpiredSessionsAsync()`, `.CreateSessionAsync()`, `.ExpireSessionAsync()`, `.GetSessionAsync()`, `.GetSessionByHashAsync()`, `.MarkScannedAsync()`, `.UpdateStatusAsync()`, `QrLoginService`, `.CleanupExpiredSessionsAsync()`, `.CreateSessionAsync()`, `.ExpireSessionAsync()`, `.GetSessionAsync()`, `.GetSessionByHashAsync()`, `.MarkScannedAsync()`, `.UpdateStatusAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 15`** (14 nodes): `ITenantSettingsService`, `TenantSettingsService`, `.GetCurrentTenantSettingsAsync()`, `.GetPlatformDefaults()`, `.GetTenantSettingsAsync()`, `.LoadPlatformDefaults()`, `.MergeAuth()`, `.MergeOidc()`, `.MergePasswordPolicy()`, `.MergeQrLogin()`, `.MergeSettings()`, `.MergeTokens()`, `.UpdateTenantSettingsAsync()`, `TenantSettingsService.cs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 17`** (13 nodes): `EmailConfirmationService.cs`, `EmailConfirmationService`, `.ConfirmAlternativeAsync()`, `.ConfirmAsync()`, `.ConfirmPrimaryAsync()`, `.CreateAlternativeConfirmationAsync()`, `.CreateInternalAsync()`, `.CreatePrimaryConfirmationAsync()`, `.GenerateToken()`, `IEmailConfirmationService`, `.ConfirmAsync()`, `.CreateAlternativeConfirmationAsync()`, `.CreatePrimaryConfirmationAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 19`** (12 nodes): `ITotpService`, `.GenerateSecretBase32()`, `.GetProvisioningUri()`, `.VerifyCode()`, `TotpService`, `.Base32Decode()`, `.Base32Encode()`, `.ComputeHotp()`, `.GenerateSecretBase32()`, `.GetProvisioningUri()`, `.VerifyCode()`, `TotpService.cs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 20`** (11 nodes): `IUserTenantMembershipService`, `.CreateAsync()`, `.GetMembershipAsync()`, `.GetMembershipsAsync()`, `.GetMembershipsByUsernameAsync()`, `UserTenantMembershipService`, `.CreateAsync()`, `.GetMembershipAsync()`, `.GetMembershipsAsync()`, `.GetMembershipsByUsernameAsync()`, `UserTenantMembershipService.cs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 21`** (11 nodes): `IUserService`, `.FindByIdAcrossTenantsAsync()`, `.FindByUsernameAsync()`, `.FindByUsernameOrEmailAsync()`, `.InvalidateUserCacheAsync()`, `UserService`, `.FindByIdAcrossTenantsAsync()`, `.FindByUsernameAsync()`, `.FindByUsernameOrEmailAsync()`, `.InvalidateUserCacheAsync()`, `UserService.cs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 23`** (10 nodes): `IWebAuthnService.cs`, `IWebAuthnService`, `.CompleteAuthenticationAsync()`, `.CompleteRegistrationAsync()`, `.CreateAuthenticationChallengeAsync()`, `.CreateRegistrationChallengeAsync()`, `.GetUserCredentialsAsync()`, `.HasWebAuthnCredentialsAsync()`, `.RemoveCredentialAsync()`, `.UpdateCredentialNameAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 24`** (9 nodes): `RevocationService.cs`, `IRevocationService`, `.RevokeAllForUserAsync()`, `.RevokeAsync()`, `.RevokeRefreshTokenFamilyAsync()`, `RevocationService`, `.RevokeAllForUserAsync()`, `.RevokeAsync()`, `.RevokeRefreshTokenFamilyAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 25`** (8 nodes): `CurrentUserAccountResolver.cs`, `CurrentUserAccountResolver`, `.NormalizeEmail()`, `.ResolveAccountSnapshotAsync()`, `.ResolveAsync()`, `.TryGetGuid()`, `ICurrentUserAccountResolver`, `.ResolveAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 26`** (8 nodes): `ITenantSettingsService.cs`, `ITenantSettingsService`, `.GetCurrentTenantSettingsAsync()`, `.GetPlatformDefaults()`, `.GetTenantSettingsAsync()`, `.UpdateTenantSettingsAsync()`, `ResolvedSettings`, `.AddSource()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 27`** (7 nodes): `IScopeResolver`, `ScopeResolver.cs`, `ScopeResolver`, `.GetAvailableScopesAsync()`, `.IsScopeNameAvailableAsync()`, `.IsStandardScope()`, `.ValidateScopesAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 28`** (7 nodes): `IScopeResolver.cs`, `IScopeResolver`, `.GetAvailableScopesAsync()`, `.IsScopeNameAvailableAsync()`, `.IsStandardScope()`, `.ValidateScopesAsync()`, `ScopeValidationResult`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 29`** (7 nodes): `ClaimMappingService.cs`, `ClaimMappingService`, `.ApplyAsync()`, `.ResolveValue()`, `.TryGet()`, `IClaimMappingService`, `.ApplyAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 30`** (7 nodes): `ITenantIconService.cs`, `ITenantIconService`, `.DeleteTenantIconAsync()`, `.GetIconAsync()`, `.GetTenantIconAsync()`, `.UploadIconAsync()`, `TenantIconData`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 31`** (7 nodes): `ITenantBrandingService.cs`, `ITenantBrandingService`, `.GetBrandingAsync()`, `.GetCurrentTenantBrandingAsync()`, `TenantBranding`, `.GetAccentColorOrDefault()`, `.GetPrimaryColorOrDefault()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 32`** (7 nodes): `IGlobalAuthenticationService.cs`, `IGlobalAuthenticationService`, `.AuthenticateAsync()`, `.ClearFailedAttemptsAsync()`, `.FindAccountByEmailAsync()`, `.IsLockedOutAsync()`, `.RecordFailedAttemptAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 33`** (7 nodes): `IConfigurationImportService.cs`, `IConfigurationImportService`, `.ImportClientAsync()`, `.ImportIdentityProviderAsync()`, `.ImportRealmAsync()`, `.ImportTenantAsync()`, `.PreviewImportAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 34`** (6 nodes): `IConfigurationExportService.cs`, `IConfigurationExportService`, `.ExportClientAsync()`, `.ExportIdentityProviderAsync()`, `.ExportRealmAsync()`, `.ExportTenantAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 35`** (6 nodes): `OboPolicyService.cs`, `IOboPolicyService`, `.EvaluateAsync()`, `OboPolicyService`, `.EvaluateAsync()`, `.Parse()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 36`** (5 nodes): `ITenantDiscoveryService.cs`, `ITenantDiscoveryService`, `.FindTenantsByEmailAsync()`, `.GetPreferredTenantAsync()`, `TenantInfo`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 37`** (5 nodes): `ITenantBrandingService`, `TenantBrandingService`, `.GetBrandingAsync()`, `.GetCurrentTenantBrandingAsync()`, `TenantBrandingService.cs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 38`** (5 nodes): `IPlatformSettingsService.cs`, `IPlatformSettingsService`, `.GetSettingsAsync()`, `.IsQrLoginAtDiscoveryEnabledAsync()`, `.UpdateSettingsAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 39`** (5 nodes): `EmailSender.cs`, `IEmailSender`, `.SendAsync()`, `NullEmailSender`, `.SendAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 40`** (5 nodes): `IJarReplayCache`, `InMemoryJarReplayCache.cs`, `InMemoryJarReplayCache`, `.Cleanup()`, `.TryAdd()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 41`** (5 nodes): `ClientIdGenerator.cs`, `ClientIdGenerator`, `.Generate()`, `IClientIdGenerator`, `.Generate()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 42`** (5 nodes): `ICachedKeyProvider.cs`, `ICachedKeyProvider`, `.GetActiveSigningKeyAsync()`, `.GetPublicJwksAsync()`, `.InvalidateCache()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 44`** (4 nodes): `IRoleClaimBuilder`, `RoleClaimBuilder.cs`, `RoleClaimBuilder`, `.BuildRoleClaims()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 45`** (4 nodes): `IRegistrationService.cs`, `IRegistrationService`, `.ApproveRegistrationAsync()`, `.CreateRegistrationAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 46`** (3 nodes): `IEmailService.cs`, `IEmailService`, `.SendPasswordResetEmailAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 47`** (3 nodes): `WebAuthnOptions`, `WebAuthnTenantOverrides`, `WebAuthnOptions.cs`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 48`** (3 nodes): `IMtlsThumbprintResolver.cs`, `IMtlsThumbprintResolver`, `.ResolveThumbprint()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 49`** (3 nodes): `IAuthorizationCodeExchanger.cs`, `IAuthorizationCodeExchanger`, `.ExchangeAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 50`** (3 nodes): `IRoleClaimBuilder.cs`, `IRoleClaimBuilder`, `.BuildRoleClaims()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 51`** (3 nodes): `ISectorIdentifierResolver.cs`, `ISectorIdentifierResolver`, `.ResolveSectorIdentifierAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 52`** (3 nodes): `IClientAuthenticationService.cs`, `IClientAuthenticationService`, `.AuthenticateAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 53`** (3 nodes): `IProviderSelectionService.cs`, `IProviderSelectionService`, `.EvaluateAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 54`** (3 nodes): `IAuthorizeRequestValidator.cs`, `IAuthorizeRequestValidator`, `.ValidateAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 55`** (3 nodes): `IConsentProcessor.cs`, `IConsentProcessor`, `.EvaluateAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 56`** (3 nodes): `IUserClientAssignmentService.cs`, `IUserClientAssignmentService`, `.EnsureAssignedAsync()`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 57`** (2 nodes): `QrLoginOptions.cs`, `QrLoginOptions`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 58`** (2 nodes): `EmailConfirmationOptions.cs`, `EmailConfirmationOptions`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.
- **Thin community `Community 59`** (2 nodes): `KeyRotationOptions.cs`, `KeyRotationOptions`
  Too small to be a meaningful cluster - may be noise or needs more connections extracted.