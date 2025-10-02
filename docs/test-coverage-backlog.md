# Test Coverage Backlog for MrWhoOidc.WebAuth, MrWhoOidc.Auth, and MrWhoOidc.Security

Status legend
- [ ] Not started
- [~] In progress
- [x] Done

## Overview

This backlog outlines comprehensive test coverage for the three core projects of MrWhoOidc to ensure protocol compliance, security, reliability, and maintainability. Current test coverage (as of 2024-12-29) is strong for protocol handlers, token services, security primitives, and integration scenarios.

**Current test count:** 284 tests passing (100% success rate)
**Recent completions:**
- ✅ Story 5.1: Security Boundary Tests (9 tests)
- ✅ Story 1.7: Introspection Service Tests (12 tests)
- ✅ Story 3.1: DPoP Validator Tests (15 tests)
- ✅ Story 1.5: UserInfo Handler Tests (16 tests)
- ✅ Story 1.2: Authorize Handler Tests (16 tests)
- ✅ Story 1.3: Token Handler Tests (13 tests)
- ✅ Story 1.4: PAR Handler Tests (14 tests)
- ✅ Story 1.6: Revocation Handler Tests (13 tests)
- ✅ Story 1.8: Logout Handler Tests (10 tests) — 2024-12-29

**Target coverage areas:** Protocol handlers, core services, security primitives, integration scenarios, error paths, and resilience patterns.

Test framework: **MSTest** (matching existing `MrWhoOidc.UnitTests`)

---

## Epic 1: MrWhoOidc.WebAuth — HTTP Handlers & Endpoints

### Story 1.1: Discovery Handler Tests
**Priority:** High | **Effort:** Small | **Status:** [ ]

Coverage for `DiscoveryHandler.cs` — OIDC discovery document generation and metadata advertisement.

**Test cases:**
- [ ] Discovery_Returns_200_With_Valid_JSON_Structure
  - Verify required OIDC fields: `issuer`, `authorization_endpoint`, `token_endpoint`, `jwks_uri`, `userinfo_endpoint`
  - Verify optional fields: `introspection_endpoint`, `revocation_endpoint`, `end_session_endpoint`
- [ ] Discovery_Advertises_BackChannel_Logout_Flags_When_Enabled
  - Assert `backchannel_logout_supported: true` and `backchannel_logout_session_supported: true`
- [ ] Discovery_Advertises_JAR_JARM_Support
  - Verify `request_parameter_supported`, `request_object_signing_alg_values_supported`
  - Verify `response_modes_supported` includes `query.jwt`, `form_post.jwt`
- [ ] Discovery_Advertises_Token_Exchange_Grant
  - Assert `grant_types_supported` contains `urn:ietf:params:oauth:grant-type:token-exchange`
- [ ] Discovery_Does_Not_Leak_Internal_Endpoints
  - Ensure admin endpoints not exposed in discovery
- [ ] Discovery_Cache_Control_Headers_Set_Correctly
  - Verify caching directives for public metadata

**Existing coverage:** `ExternalOidcIntegrationTests.Discovery_Document_Verification_Includes_JAR_JARM_And_TokenExchange` (partial)

---

### Story 1.2: Authorize Handler Tests
**Priority:** High | **Effort:** Medium | **Status:** [x] ✅

Coverage for `AuthorizeHandler.cs` — Authorization endpoint orchestration.

**Test cases:**
- [x] Authorize_MissingClientId_Returns_Error (completed 2025-10-02)
- [x] Authorize_MissingRedirectUri_Returns_Error (completed 2025-10-02)
- [x] Authorize_UnknownClient_Returns_Error (completed 2025-10-02)
- [x] Authorize_RedirectUri_Mismatch_Returns_Error (completed 2025-10-02)
- [x] Authorize_InvalidScope_Returns_Error (completed 2025-10-02)
- [x] Authorize_UnsupportedResponseType_Returns_Error (completed 2025-10-02)
- [x] Authorize_ValidRequest_RedirectsToLogin (completed 2025-10-02)
- [x] Authorize_PKCE_Required_When_Public_Client (completed 2025-10-02)
- [x] Authorize_PKCE_CodeChallengeMethod_S256_Supported (completed 2025-10-02)
- [x] Authorize_PKCE_CodeChallengeMethod_Plain_Rejected_When_Policy_Enforced (completed 2025-10-02)
- [x] Authorize_RequestObject_Via_PAR_Uri_Resolves_Correctly (completed 2025-10-02)
- [x] Authorize_RequestObject_Via_Inline_JWT_Validated_And_Parsed (completed 2025-10-02)
- [x] Authorize_State_Parameter_Echoed_In_Callback (completed 2025-10-02)
- [x] Authorize_Nonce_Parameter_Stored_For_IdToken (completed 2025-10-02)
- [x] Authorize_Response_Mode_Form_Post_Supported (completed 2025-10-02)
- [x] Authorize_Response_Mode_Query_JWT_Supported (JARM) (completed 2025-10-02)
- [ ] Authorize_Prompt_None_With_No_Session_Returns_Login_Required (future)
- [ ] Authorize_Prompt_Login_Forces_Reauthentication (future)
- [ ] Authorize_Max_Age_Parameter_Enforced (future)

**Existing coverage:** All 16 test cases implemented in `AuthorizeHandlerTests.cs` ✅

---

### Story 1.3: Token Handler Tests ✅
**Priority:** High | **Effort:** Large | **Status:** [x] **Completed: 2025-01-XX**

Coverage for `TokenHandler.cs` — Token endpoint orchestration, client authentication, and DPoP validation.

**Test cases:**
- [x] Token_Invalid_Grant_Type_Returns_Unsupported_Grant_Type (2025-01-XX)
- [x] Token_Invalid_Client_Credentials_Returns_Invalid_Client (2025-01-XX)
- [x] Token_Missing_Client_Id_Returns_Error (2025-01-XX)
- [x] Token_ClientCredentials_Invalid_Client_Assertion_Returns_Error (2025-01-XX)
- [x] Token_DPoP_Proof_Missing_Jti_Returns_Error (2025-01-XX)
- [x] Token_DPoP_Proof_Invalid_Signature_Returns_Error (2025-01-XX)
- [x] Token_DPoP_Proof_Htm_Mismatch_Returns_Error (2025-01-XX)
- [x] Token_DPoP_Proof_Htu_Mismatch_Returns_Error (2025-01-XX)
- [x] Token_Basic_Authentication_Works (2025-01-XX)
- [x] Token_Client_Secret_Post_Works (2025-01-XX)
- [x] Token_Private_Key_JWT_Authentication_Works (2025-01-XX)
- [x] Token_Metrics_Recorded_For_Each_Grant_Type (2025-01-XX)
- [x] Token_Missing_Form_Content_Type_Returns_Error (2025-01-XX)
- [ ] Token_AuthorizationCode_HappyPath_Returns_Access_And_Id_Tokens (deferred: covered in `AuthorizationCodeGrantStrategyTests`)
- [ ] Token_AuthorizationCode_InvalidCode_Returns_Error (deferred: covered in grant strategy tests)
- [ ] Token_AuthorizationCode_ExpiredCode_Returns_Error (deferred: covered in grant strategy tests)
- [ ] Token_AuthorizationCode_PKCE_Verifier_Mismatch_Returns_Error (deferred: covered in grant strategy tests)
- [ ] Token_AuthorizationCode_PKCE_Verifier_Missing_Returns_Error (deferred: covered in grant strategy tests)
- [ ] Token_ClientCredentials_HappyPath_Returns_Access_Token (deferred: covered in `ClientCredentialsGrantStrategyTests`)
- [ ] Token_ClientCredentials_Scope_Exceeds_Allowed_Returns_Error (deferred: grant strategy level)
- [ ] Token_RefreshToken_HappyPath_Returns_New_Tokens (deferred: covered in `TokenEndpointGrantDispatchStrategyTests`)
- [ ] Token_RefreshToken_Revoked_Returns_Error (deferred: grant strategy level)
- [ ] Token_RefreshToken_Scope_Downgrade_Allowed (deferred: grant strategy level)
- [ ] Token_RefreshToken_Scope_Upgrade_Rejected (deferred: grant strategy level)
- [ ] Token_TokenExchange_Happy_Path_With_Policy (deferred: see existing `TokenExchangeIntegrationTests`)
- [ ] Token_TokenExchange_With_DPoP_Ath_Binding_Succeeds (deferred: see existing tests)
- [ ] Token_DPoP_Proof_Replayed_Jti_Returns_Error (deferred: future DPoP replay cache)
- [ ] Token_DPoP_Nonce_Enforced_When_Required (deferred: future nonce enforcement)
- [ ] Token_Rate_Limiting_Applied_For_Token_Exchange (deferred: see `TokenExchangeRateLimiterTests`)

**Notes:**
- Focused on TokenHandler orchestration layer: client auth, DPoP integration, grant dispatch
- Grant-specific logic tested separately in strategy tests
- 13/13 handler-level tests implemented
- 11/24 grant-level tests deferred to existing coverage

**Existing coverage:** `TokenExchangeIntegrationTests`, `ClientCredentialsGrantStrategyTests`, `AuthorizationCodeGrantStrategyTests`, `TokenEndpointGrantDispatchStrategyTests`

---

### Story 1.4: PAR Handler Tests ✅
**Priority:** Medium | **Effort:** Medium | **Status:** [x] **Completed: 2025-10-02**

Coverage for `ParHandler.cs` — Pushed Authorization Request endpoint.

**Test cases:**
- [x] PAR_HappyPath_Returns_RequestUri_And_ExpiresIn (2025-10-02)
- [x] PAR_Request_Object_Signed_JWT_Validated (2025-10-02)
- [x] PAR_Request_Object_Invalid_Returns_Error (2025-10-02)
- [x] PAR_Request_Object_ClientId_Mismatch_Returns_Error (2025-10-02)
- [x] PAR_Client_Authentication_Required (2025-10-02)
- [x] PAR_Client_Authentication_Via_Basic_Auth (2025-10-02)
- [x] PAR_Client_Authentication_Via_ClientAssertion_JWT (2025-10-02)
- [x] PAR_Invalid_Client_Assertion_Returns_Error (2025-10-02)
- [x] PAR_Invalid_Client_Returns_Error (2025-10-02)
- [x] PAR_Missing_ClientId_Returns_Error (2025-10-02)
- [x] PAR_Request_Validation_Failed_Returns_Error (2025-10-02)
- [x] PAR_Request_Object_Too_Large_Returns_Error (2025-10-02)
- [x] PAR_Rate_Limiting_Applied_Per_Client (2025-10-02)
- [x] PAR_Missing_Form_Content_Type_Returns_Error (2025-10-02)
- [ ] PAR_Request_Object_Algorithm_Mismatch_Returns_Error (deferred: requires request object validation extension)
- [ ] PAR_Request_Object_Expired_Returns_Error (deferred: requires request object validation extension)
- [ ] PAR_RequestUri_Expires_After_Configured_TTL (deferred: covered in `ParStoreTests.EfParStore_Expiry_Cleans_Up`)
- [ ] PAR_RequestUri_Single_Use_Enforced (deferred: covered in `ParStoreTests.EfParStore_Create_Get_Consume_WithPendingLimit`)
- [ ] PAR_Replay_Cache_Prevents_Duplicate_Jti (deferred: requires JAR replay cache integration)
- [ ] PAR_Metrics_Recorded (deferred: requires metrics validation framework)

**Notes:**
- Focused on PAR Handler orchestration: client auth, request validation, rate limiting
- 14/14 handler-level tests implemented
- 6/20 deferred to existing coverage or future extensions

**Existing coverage:** `RequestObjectValidatorTests`, `ParStoreTests`, `JarReplayCache` (partial)

---

### Story 1.5: UserInfo Handler Tests
**Priority:** High | **Effort:** Medium | **Status:** [x] ✅

Coverage for `UserInfoHandler.cs` — UserInfo endpoint with DPoP.

**Test cases:**
- [x] UserInfo_Missing_Authorization_Returns_401 (completed 2025-10-02)
- [x] UserInfo_Invalid_Authorization_Header_Returns_401 (completed 2025-10-02)
- [x] UserInfo_Invalid_Token_Returns_401 (completed 2025-10-02)
- [x] UserInfo_Valid_Token_Returns_Claims (completed 2025-10-02)
- [x] UserInfo_Sub_Claim_Always_Present (completed 2025-10-02)
- [x] UserInfo_DPoP_Bound_Token_Requires_Valid_Proof (completed 2025-10-02)
- [x] UserInfo_Access_Token_Expired_Returns_401 (completed 2025-10-02)
- [x] UserInfo_Access_Token_Revoked_Returns_401 (completed 2025-10-02)
- [x] UserInfo_DPoP_Proof_Jkt_Mismatch_Returns_Error (completed 2025-10-02)
- [x] UserInfo_DPoP_Nonce_Enforced_After_Initial_Error (completed 2025-10-02)
- [x] UserInfo_Claims_Filtered_By_Scope (completed 2025-10-02)
- [x] UserInfo_Email_Claim_Returned_With_Email_Scope (completed 2025-10-02)
- [x] UserInfo_Profile_Claims_Returned_With_Profile_Scope (completed 2025-10-02)
- [x] UserInfo_Roles_Claim_Returned_With_Roles_Scope (completed 2025-10-02)
- [x] UserInfo_Address_Claim_Not_Leaked_Without_Scope (completed 2025-10-02)
- [x] UserInfo_Metrics_Recorded (completed 2025-10-02)

**Existing coverage:** All tests implemented (16/16 test cases) in `UserInfoHandlerTests.cs` ✅

---

### Story 1.6: Revocation Handler Tests ✅
**Priority:** Medium | **Effort:** Small | **Status:** [x] **Completed: 2025-10-02**

Coverage for `RevocationHandler.cs` — Token revocation endpoint.

**Test cases:**
- [x] Revocation_Access_Token_HappyPath_Returns_200 (2025-10-02)
- [x] Revocation_Refresh_Token_HappyPath_Returns_200 (2025-10-02)
- [x] Revocation_Unknown_Token_Returns_200 (2025-10-02) — RFC 7009 compliant
- [x] Revocation_Client_Authentication_Required (2025-10-02)
- [x] Revocation_Client_Authentication_Via_Basic_Auth (2025-10-02)
- [x] Revocation_Client_Authentication_Via_PrivateKeyJwt (2025-10-02)
- [x] Revocation_Invalid_Client_Assertion_Returns_Error (2025-10-02)
- [x] Revocation_Missing_Token_Returns_Error (2025-10-02)
- [x] Revocation_Missing_ClientId_Returns_Error (2025-10-02)
- [x] Revocation_Token_Type_Hint_Opaque_Access (2025-10-02)
- [x] Revocation_Token_Type_Hint_Refresh (2025-10-02)
- [x] Revocation_IP_Address_Captured_For_Audit (2025-10-02)
- [x] Revocation_Missing_Form_Content_Type_Returns_Error (2025-10-02)
- [ ] Revocation_Cross_Client_Revocation_Rejected (deferred: covered in `SecurityBoundaryTests.Security_Cross_Client_Token_Revocation_Blocked`)
- [ ] Revocation_Metrics_Recorded (deferred: requires metrics validation framework)

**Notes:**
- Focused on RevocationHandler orchestration: client auth, token validation, audit logging
- 13/13 handler-level tests implemented
- 2/15 deferred to existing coverage

**Existing coverage:** `RevocationServiceTests` (service layer), `SecurityBoundaryTests` (cross-client isolation)

---

### Story 1.7: Introspection Service Tests
**Priority:** High | **Effort:** Medium | **Status:** [x] ✅

Coverage for token introspection service layer: JWT validation, opaque token lookups, refresh token handling.

**Test cases:**
- [x] JWT_Token_Validation_Returns_Claims — Active JWT with valid signature returns principal
- [x] JWT_Token_Expired_Returns_Invalid — Expired JWT fails validation
- [x] Opaque_Token_Active_Found_In_Database — Active opaque token found with correct metadata
- [x] Opaque_Token_Expired_Returns_Inactive — Expired opaque token marked inactive
- [x] Opaque_Token_Revoked_Returns_Inactive — Revoked opaque token marked inactive
- [x] Refresh_Token_Active_Found_In_Database — Active refresh token found with scopes
- [x] Unknown_Token_Returns_Inactive — Unknown token returns `active=false` (RFC 7662)
- [x] DPoP_Bound_Token_Has_Cnf_Claim — DPoP-bound token includes `cnf.jkt` thumbprint
- [x] Token_Scope_Claims_Deserialized_Correctly — Scopes JSON deserialized properly
- [x] Client_Authentication_With_Secret_Validated — Correct client secret validates
- [x] Public_Client_Has_No_Secret — Public client has no secret hash

**Existing coverage:** `IntrospectionServiceTests.cs` — 12 tests covering service layer (182 total tests passing)

---

### Story 1.8: Logout Handler Tests
**Priority:** High | **Effort:** Large | **Status:** [x] ✅ 2024-12-29

Coverage for `LogoutHandler.cs` and modular logout handlers in `Handlers/Logout/`.

**Test cases:**
- [x] LocalLogoutAsync_Delegates_To_LocalLogoutHandler — 2024-12-29
- [x] LocalLogoutAsync_Null_ReturnUrl_Redirects_To_Root — 2024-12-29
- [x] LogoutEntryAsync_Delegates_To_FederatedEntry — 2024-12-29
- [x] FederatedCallbackAsync_Delegates_To_FederatedCallback — 2024-12-29
- [x] EndSessionAsync_Delegates_To_EndSession — 2024-12-29
- [x] EndSessionAsync_Accepts_Id_Token_Hint — 2024-12-29
- [x] EndSessionAsync_Accepts_Post_Logout_Redirect_Uri — 2024-12-29
- [x] EndSessionAsync_Accepts_Sid — 2024-12-29
- [x] FinalRedirectAsync_Delegates_To_RedirectResolver — 2024-12-29
- [x] FinalRedirectAsync_Empty_Ref_Returns_Error — 2024-12-29

**Notes:**
- Tests focus on LogoutHandler orchestration patterns (pure delegator)
- 10 tests covering all 5 public methods (LocalLogoutAsync, LogoutEntryAsync, FederatedCallbackAsync, EndSessionAsync, FinalRedirectAsync)
- Specialized handler business logic tested separately (BCL/FCL/federated in existing tests)
- Integration tests needed for full end-to-end logout flows with DB state

**Existing coverage:** `LogoutPromptFlowTests`, `FederatedLogoutServiceTests`, `LogoutHandlerTests.cs` (284 total tests passing)

---

### Story 1.9: External OIDC Handler Tests
**Priority:** Medium | **Effort:** Medium | **Status:** [x] ✅ **Completed: 2024-12-29**

Coverage for `ExternalOidcHandler.cs` — IDP chaining/federation orchestration.

**Test cases:**
- [x] External_TwoProviders_HappyPath_Provider1 (existing integration test)
- [x] External_TwoProviders_HappyPath_Provider2 (existing integration test)
- [x] External_CancelFlow_Returns_Error (existing)
- [x] Start_Missing_Provider_Parameter_Returns_Error — 2024-12-29
- [x] Start_Missing_ReturnUrl_Parameter_Returns_Error — 2024-12-29
- [x] Start_Unknown_Provider_Returns_Error — 2024-12-29
- [x] Start_Disabled_Provider_Returns_Error — 2024-12-29
- [x] Start_Invalid_Provider_Config_Returns_Error — 2024-12-29
- [x] Callback_Missing_State_Parameter_Returns_Error — 2024-12-29
- [x] Callback_Invalid_State_Returns_Error — 2024-12-29
- [x] Callback_Error_Parameter_Propagated — 2024-12-29
- [ ] External_Upstream_Discovery_Fetch_Fails_Returns_Error (partially covered by Start_Unknown_Provider)
- [ ] External_Upstream_Token_Exchange_Fails_Returns_Error (needs deeper mocking)
- [ ] External_Upstream_UserInfo_Fetch_Fails_Gracefully (integration-level test)
- [ ] External_Nonce_Validation_In_IdToken (integration-level test)
- [ ] External_Claims_Mapping_Applied (integration-level test)
- [ ] External_Account_Linking_By_Email (integration-level test)

**Notes:**
- 8 new unit tests added in `ExternalOidcHandlerTests.cs` covering StartAsync and CallbackAsync error paths
- Focus on parameter validation, provider lookup, state validation, and error handling
- Integration tests (`ExternalOidcIntegrationTests`) cover happy paths with full TestServer
- Some complex scenarios (token exchange failures, UserInfo failures, claims mapping) better suited for specialized service tests or integration tests
- StartAsync: 5 error tests (missing params, unknown/disabled/invalid provider)
- CallbackAsync: 3 validation tests (missing/invalid state, error parameter handling)
- State validation enforced at entry: missing/invalid state returns BadRequest before other processing

**Existing coverage:** `ExternalOidcIntegrationTests`, `ExternalOidcErrorTests`, `ExternalOidcHandlerTests.cs` (292 total tests passing)

---

### Story 1.10: Admin Endpoint Tests
**Priority:** Medium | **Effort:** Large | **Status:** [x] ✅ **Completed: 2024-10-02**

Coverage for admin APIs and Razor Pages in `WebAuth/Admin/` and `WebAuth/Pages/Admin/`.

**Test cases:**
- [x] Admin_Providers_List_Returns_All — 2024-10-02
- [x] Admin_Providers_Create_Validation_Rejects_Empty_Name — 2024-10-02
- [x] Admin_Providers_Update_Modifies_Fields — 2024-10-02
- [x] Admin_Providers_Update_Returns_NotFound_For_Missing_Id — 2024-10-02
- [x] Admin_Providers_Delete_Removes_Provider — 2024-10-02
- [x] Admin_Providers_Delete_Returns_NotFound_For_Missing_Id — 2024-10-02
- [x] Admin_Authorization_Handler_Grants_With_Admin_Role (existing in AdminAuthorizationHandlerTests)
- [x] Admin_Authorization_Handler_Denies_Without_Assignment (existing in AdminAuthorizationHandlerTests)
- [x] Providers_Create_And_Get_ById_Works_With_Admin_Policy (existing in AdminProvidersApiTests)
- [ ] Admin_Clients_Create_HappyPath (future: needs client admin API implementation)
- [ ] Admin_Clients_Edit_Updates_Fields (future)
- [ ] Admin_Clients_Delete_Soft_Deletes_Or_Hard_Deletes (future)
- [ ] Admin_Clients_BackChannelUri_HTTPS_Validation (future)
- [ ] Admin_Clients_BackChannelUri_Dev_Override_Allows_HTTP (future)
- [ ] Admin_Users_List_Paginated (future)
- [ ] Admin_Users_Create_With_Realms_And_Roles (future)
- [ ] Admin_Providers_Keys_Rotation_Endpoint (partial: see ProviderKeysPageTests)
- [ ] Admin_Audit_Logs_Sensitive_Changes (future)

**Notes:**
- 6 new provider CRUD tests added in `AdminProvidersCrudTests.cs`
- Tests focus on validation, update, and delete operations for identity providers
- Authorization logic tested separately in `AdminAuthorizationHandlerTests.cs` (realm/role-based)
- Provider create/get tested in existing `AdminProvidersApiTests.cs`
- Client and user admin APIs are not yet implemented - marked for future
- Test pattern: TestServer with in-memory database, bypasses admin policy for CRUD testing

**Existing coverage:** `AdminAuthorizationHandlerTests`, `AdminProvidersApiTests`, `ProviderKeysPageTests`, `AdminProvidersCrudTests.cs` (298 total tests passing)

---

### Story 1.11: Background Worker Tests
**Priority:** High | **Effort:** Medium | **Status:** [ ]

Coverage for `BackchannelLogoutDispatcher.cs` and `BackchannelAlertSampler.cs`.

**Test cases:**
- [ ] Dispatcher_Polls_Outbox_On_Interval
- [ ] Dispatcher_Processes_Notifications_With_Bounded_Concurrency
- [ ] Dispatcher_Retries_Failed_Notifications_With_Exponential_Backoff
- [ ] Dispatcher_Marks_Notification_Succeeded_After_200_Response
- [ ] Dispatcher_Marks_Notification_Failed_After_Max_Retries
- [ ] Dispatcher_Circuit_Breaker_Opens_After_Failure_Threshold
- [ ] Dispatcher_Circuit_Breaker_Closes_After_Success_Window
- [ ] Dispatcher_HTTP_Timeout_Enforced
- [ ] Dispatcher_Metrics_Counters_Incremented
- [ ] Dispatcher_Alert_Sampler_Logs_High_Failure_Rate
- [ ] Dispatcher_Graceful_Shutdown_Completes_In_Flight_Requests

**Existing coverage:** `BackchannelAlertSamplerTests` (partial)

---

### Story 1.12: Rate Limiting Integration Tests
**Priority:** Medium | **Effort:** Medium | **Status:** [~]

Coverage for rate limiting policies applied to OIDC endpoints.

**Test cases:**
- [ ] RateLimit_Token_Exchange_Limit_Per_Client
- [ ] RateLimit_PAR_Limit_Per_Client
- [ ] RateLimit_Introspection_Limit_Per_Client
- [ ] RateLimit_Headers_Set_On_Response
- [ ] RateLimit_Retry_After_Header_On_429

**Existing coverage:** `RateLimitHeadersIntegrationTests`, `TokenExchangeRateLimiterTests` (partial)

---

### Story 1.13: JWKS Public Endpoint Tests
**Priority:** Medium | **Effort:** Small | **Status:** [x]

Coverage for public JWKS endpoint serving signing keys.

**Test cases:**
- [x] JWKS_Returns_Current_Keys (existing)
- [x] JWKS_Keys_Include_Kid (existing)
- [x] JWKS_Keys_Include_Kty_Alg (existing)
- [x] JWKS_Metrics_Recorded (existing)
- [ ] JWKS_Cache_Control_Headers_Set
- [ ] JWKS_Key_Rotation_Reflected_After_Period

**Existing coverage:** `PublicJwksEndpointsTests`, `PublicJwksMetricsTests` (good)

---

### Story 1.14: Correlation Pipeline Tests
**Priority:** Low | **Effort:** Small | **Status:** [x]

Coverage for correlation ID middleware.

**Test cases:**
- [x] Correlation_ID_Generated_Per_Request (existing)
- [x] Correlation_ID_Propagated_In_Logs (existing)

**Existing coverage:** `CorrelationPipelineTests` (complete)

---

### Story 1.15: Health & Observability Tests
**Priority:** Low | **Effort:** Small | **Status:** [ ]

Coverage for health endpoints and metrics.

**Test cases:**
- [ ] Health_Backchannel_Endpoint_Returns_Metrics
- [ ] Health_Backchannel_Endpoint_Returns_CircuitBreaker_State
- [ ] Health_Database_Connectivity_Check
- [ ] Metrics_OIDC_Counter_Labels_Correct

**Existing coverage:** None identified (gap)

---

## Epic 2: MrWhoOidc.Auth — Core Domain Services

### Story 2.1: AuthorizeService Tests
**Priority:** High | **Effort:** Medium | **Status:** [~]

Coverage for `AuthorizeService.cs` — Authorization request validation and scope enforcement.

**Test cases:**
- [x] AuthorizeService_ValidatesClientId (existing)
- [x] AuthorizeService_ValidatesRedirectUri (existing)
- [x] AuthorizeService_ValidatesScopes (existing)
- [ ] AuthorizeService_Scope_Enforcement_Rejects_Disallowed_Scope
- [ ] AuthorizeService_Scope_Default_Applied_When_None_Requested
- [ ] AuthorizeService_PKCE_Enforcement_For_Public_Clients
- [ ] AuthorizeService_RequestObject_Merged_With_Query_Params
- [ ] AuthorizeService_RequestObject_Overrides_Query_Params_When_Conflict
- [ ] AuthorizeService_Prompt_Parameter_Validation
- [ ] AuthorizeService_Max_Age_Exceeds_Session_Age_Requires_Reauth

**Existing coverage:** `AuthorizeServiceTests` (foundation exists)

---

### Story 2.2: TokenService Tests
**Priority:** High | **Effort:** Large | **Status:** [~]

Coverage for `TokenService.cs` — Token issuance, refresh, exchange, and validation.

**Test cases:**
- [x] TokenService_CreateAccessToken_JWT (existing)
- [x] TokenService_CreateAccessToken_Opaque (existing)
- [x] TokenService_CreateRefreshToken (existing)
- [x] TokenService_TokenExchange_HappyPath (existing in integration tests)
- [ ] TokenService_TokenExchange_Delegation_Depth_Limit_Enforced
- [ ] TokenService_TokenExchange_Lifetime_Cap_Applied
- [ ] TokenService_TokenExchange_Scope_Downgrade_Allowed
- [ ] TokenService_TokenExchange_Scope_Upgrade_Rejected
- [ ] TokenService_RefreshToken_Rotation_Creates_New_Family
- [ ] TokenService_RefreshToken_Reuse_Detection_Revokes_Family
- [ ] TokenService_Access_Token_Includes_Roles_When_Roles_Scope_Granted
- [ ] TokenService_Access_Token_Excludes_Roles_Without_Roles_Scope
- [ ] TokenService_DPoP_Binding_Cnf_Claim_Included_When_Bound
- [ ] TokenService_Audience_Claim_Set_Correctly
- [ ] TokenService_Token_Expiry_Respects_Client_Policy

**Existing coverage:** `TokenServiceTests`, `TokenExchangeIntegrationTests`, `TokenRoleEmissionTests` (good foundation)

---

### Story 2.3: JwtService Tests
**Priority:** High | **Effort:** Medium | **Status:** [~]

Coverage for `JwtService.cs` — JWT creation, signing, and parsing.

**Test cases:**
- [x] JwtService_CreateJwt_Signed_With_Current_Key (existing)
- [x] JwtService_ParseJwt_Validates_Signature (existing)
- [ ] JwtService_CreateJwt_Includes_Kid_In_Header
- [ ] JwtService_CreateJwt_Sets_Typ_Header
- [ ] JwtService_CreateJwt_Iat_Nbf_Exp_Claims_Set
- [ ] JwtService_ParseJwt_Rejects_Expired_Token
- [ ] JwtService_ParseJwt_Rejects_Not_Yet_Valid_Token
- [ ] JwtService_ParseJwt_Rejects_Invalid_Signature
- [ ] JwtService_ParseJwt_Rejects_Unknown_Kid
- [ ] JwtService_Key_Rotation_Uses_New_Key_For_New_Tokens
- [ ] JwtService_Old_Keys_Still_Validate_Until_Expiry

**Existing coverage:** `JwtServiceTests` (partial)

---

### Story 2.4: TokenValidator Tests
**Priority:** High | **Effort:** Medium | **Status:** [~]

Coverage for `TokenValidator.cs` — Access token validation.

**Test cases:**
- [x] TokenValidator_ValidateAccessToken_JWT_HappyPath (existing)
- [x] TokenValidator_ValidateAccessToken_Opaque_HappyPath (existing)
- [ ] TokenValidator_Rejects_Expired_Token
- [ ] TokenValidator_Rejects_Revoked_Token
- [ ] TokenValidator_Rejects_Invalid_Audience
- [ ] TokenValidator_Rejects_Invalid_Issuer
- [ ] TokenValidator_Rejects_Invalid_Signature
- [ ] TokenValidator_DPoP_Bound_Token_Requires_Valid_Proof
- [ ] TokenValidator_Opaque_Token_Lookup_In_Database

**Existing coverage:** `TokenValidatorTests` (partial)

---

### Story 2.5: KeyStore and Key Rotation Tests
**Priority:** High | **Effort:** Medium | **Status:** [~]

Coverage for `KeyStore.cs`, `KeyRotationService.cs`, and `KeyRotationHostedService.cs`.

**Test cases:**
- [x] KeyStore_GetCurrentKey_Returns_Most_Recent (existing)
- [x] KeyStore_GetKeyById_Returns_Correct_Key (existing)
- [ ] KeyRotation_Scheduled_Rotation_Creates_New_Key
- [ ] KeyRotation_Old_Keys_Marked_Inactive_After_Grace_Period
- [ ] KeyRotation_Inactive_Keys_Removed_After_Expiry
- [ ] KeyRotation_Concurrent_Rotation_Does_Not_Duplicate_Keys
- [ ] KeyRotation_JWKS_Includes_Active_Keys_Only
- [ ] KeyRotation_Provider_Specific_Keys_Managed_Separately

**Existing coverage:** `KeyStoreTests`, `KeyRotationServiceTests` (partial), `JwksHistoryAndParStressTests` (stress test exists)

---

### Story 2.6: ClientStore Tests
**Priority:** Medium | **Effort:** Medium | **Status:** [~]

Coverage for `ClientStore.cs` — Client lookup and validation.

**Test cases:**
- [x] ClientStore_GetByClientId_Returns_Client (existing)
- [x] ClientStore_GetByClientId_Unknown_Returns_Null (existing)
- [ ] ClientStore_ValidateClientSecret_HappyPath
- [ ] ClientStore_ValidateClientSecret_Invalid_Returns_False
- [ ] ClientStore_IsRedirectUriAllowed_Exact_Match
- [ ] ClientStore_IsRedirectUriAllowed_Wildcard_Not_Allowed
- [ ] ClientStore_GetAllowedScopes_Filters_By_Client_Config
- [ ] ClientStore_GetBackChannelLogoutUri_Returns_Configured_Uri

**Existing coverage:** `ClientStoreTests` (partial)

---

### Story 2.7: ConsentService Tests
**Priority:** Medium | **Effort:** Medium | **Status:** [~]

Coverage for `ConsentService.cs` — User consent management.

**Test cases:**
- [x] ConsentService_StoreConsent_HappyPath (existing)
- [x] ConsentService_GetConsent_Returns_Stored_Consent (existing)
- [ ] ConsentService_RevokeConsent_Removes_Consent
- [ ] ConsentService_IsConsentRequired_Returns_True_When_No_Consent
- [ ] ConsentService_IsConsentRequired_Returns_False_When_Consent_Exists
- [ ] ConsentService_Consent_Scopes_Includes_Previously_Granted

**Existing coverage:** `ConsentServiceTests` (partial)

---

### Story 2.8: UserService Tests
**Priority:** Medium | **Effort:** Medium | **Status:** [~]

Coverage for `UserService.cs` — User authentication and management.

**Test cases:**
- [x] UserService_ValidateCredentials_HappyPath (existing)
- [x] UserService_ValidateCredentials_Invalid_Password_Returns_False (existing)
- [ ] UserService_CreateUser_Hashes_Password
- [ ] UserService_UpdateUser_Updates_Fields
- [ ] UserService_GetUserByEmail_Normalized_Lookup
- [ ] UserService_GetUserByAlternativeEmail_Returns_User
- [ ] UserService_Lockout_After_Failed_Attempts
- [ ] UserService_GetUserRoles_Returns_Realm_And_Client_Roles
- [ ] UserService_AssignUserToClient_Creates_Assignment

**Existing coverage:** `UserServiceTests` (partial)

---

### Story 2.9: RevocationService Tests
**Priority:** Medium | **Effort:** Medium | **Status:** [~]

Coverage for `RevocationService.cs` — Token revocation.

**Test cases:**
- [x] RevocationService_RevokeToken_Marks_Revoked (existing)
- [x] RevocationService_RevokeRefreshToken_Revokes_Family (existing)
- [ ] RevocationService_IsRevoked_Returns_True_After_Revocation
- [ ] RevocationService_RevokeAllTokensForUser_Revokes_All

**Existing coverage:** `RevocationServiceTests` (partial)

---

### Story 2.10: AuthorizationCodeService Tests
**Priority:** Medium | **Effort:** Medium | **Status:** [~]

Coverage for `AuthorizationCodeService.cs` — Authorization code lifecycle.

**Test cases:**
- [x] AuthCodeService_CreateCode_Returns_Opaque_Code (existing)
- [x] AuthCodeService_ConsumeCode_HappyPath (existing)
- [x] AuthCodeService_ConsumeCode_SingleUse_Enforced (existing)
- [ ] AuthCodeService_ConsumeCode_Expired_Returns_Null
- [ ] AuthCodeService_Metadata_Store_Preserves_Nonce_And_Verifier

**Existing coverage:** `AuthorizationCodeServiceTests`, `AuthorizationCodeMetadataStoreTests` (good)

---

### Story 2.11: RefreshTokenService Tests
**Priority:** Medium | **Effort:** Medium | **Status:** [ ]

Coverage for `RefreshTokenService.cs` — Refresh token management.

**Test cases:**
- [ ] RefreshTokenService_CreateRefreshToken_Returns_Opaque_Token
- [ ] RefreshTokenService_ValidateRefreshToken_HappyPath
- [ ] RefreshTokenService_ValidateRefreshToken_Expired_Returns_Null
- [ ] RefreshTokenService_ValidateRefreshToken_Revoked_Returns_Null
- [ ] RefreshTokenService_ReuseDetection_Revokes_Family

**Existing coverage:** None identified (gap)

---

### Story 2.12: OboPolicyService Tests
**Priority:** Medium | **Effort:** Medium | **Status:** [ ]

Coverage for `OboPolicyService.cs` — On-Behalf-Of token exchange policy.

**Test cases:**
- [ ] OboPolicy_ValidateExchange_Allowed_Target_Succeeds
- [ ] OboPolicy_ValidateExchange_Disallowed_Target_Fails
- [ ] OboPolicy_ValidateExchange_Scope_Enforcement
- [ ] OboPolicy_ValidateExchange_Lifetime_Cap_Applied
- [ ] OboPolicy_ValidateExchange_Delegation_Depth_Enforced
- [ ] OboPolicy_DPoP_RequireSameJkt_Enforced_When_Policy_Set

**Existing coverage:** `TokenExchangePolicyTests` (partial)

---

### Story 2.13: ClientAssertionValidator Tests
**Priority:** Medium | **Effort:** Medium | **Status:** [~]

Coverage for `ClientAssertionValidator.cs` — JWT client authentication.

**Test cases:**
- [x] ClientAssertion_Valid_JWT_Returns_ClientId (existing)
- [x] ClientAssertion_Invalid_Signature_Returns_Null (existing)
- [ ] ClientAssertion_Expired_Returns_Null
- [ ] ClientAssertion_Invalid_Audience_Returns_Null
- [ ] ClientAssertion_Jti_Replay_Detected_Returns_Null
- [ ] ClientAssertion_Missing_Claims_Returns_Null

**Existing coverage:** `ClientAssertionValidatorTests` (partial)

---

### Story 2.14: RequestObjectValidator Tests
**Priority:** Medium | **Effort:** Medium | **Status:** [~]

Coverage for `RequestObjectValidator.cs` — JAR validation.

**Test cases:**
- [x] RequestObject_Signed_JWT_Valid (existing)
- [x] RequestObject_Invalid_Signature_Fails (existing)
- [ ] RequestObject_Unsigned_Rejected_When_Policy_Requires_Signature
- [ ] RequestObject_Expired_Fails
- [ ] RequestObject_Algorithm_Mismatch_Fails
- [ ] RequestObject_Replay_Jti_Detected_Fails
- [ ] RequestObject_Client_Mismatch_Fails

**Existing coverage:** `RequestObjectValidatorTests` (partial)

---

### Story 2.15: PushedAuthorizationRequestStore Tests
**Priority:** Medium | **Effort:** Small | **Status:** [~]

Coverage for `PushedAuthorizationRequestStore.cs` — PAR storage.

**Test cases:**
- [x] PAR_Store_Save_Returns_Uri (existing)
- [x] PAR_Store_Consume_HappyPath (existing)
- [x] PAR_Store_Consume_SingleUse_Enforced (existing)
- [ ] PAR_Store_Consume_Expired_Returns_Null
- [ ] PAR_Store_Cleanup_Removes_Expired_Entries

**Existing coverage:** `ParStoreTests` (good)

---

### Story 2.16: ClaimMappingService Tests
**Priority:** Low | **Effort:** Small | **Status:** [~]

Coverage for `ClaimMappingService.cs` — External IdP claim mapping.

**Test cases:**
- [x] ClaimMapping_Maps_Upstream_Claims_To_Local (existing)
- [x] ClaimMapping_Default_Mappings_Applied (existing)
- [ ] ClaimMapping_Custom_Mappings_Override_Defaults
- [ ] ClaimMapping_Unmapped_Claims_Dropped

**Existing coverage:** `ClaimMappingServiceTests` (partial)

---

### Story 2.17: PasswordHasher Tests
**Priority:** Low | **Effort:** Small | **Status:** [ ]

Coverage for `PasswordHasher.cs` — Argon2id password hashing.

**Test cases:**
- [ ] PasswordHasher_Hash_Produces_Different_Hashes_For_Same_Input
- [ ] PasswordHasher_Verify_HappyPath
- [ ] PasswordHasher_Verify_Invalid_Password_Returns_False
- [ ] PasswordHasher_Verify_Malformed_Hash_Returns_False

**Existing coverage:** None identified (gap)

---

### Story 2.18: TotpService Tests
**Priority:** Low | **Effort:** Small | **Status:** [ ]

Coverage for `TotpService.cs` — TOTP MFA.

**Test cases:**
- [ ] Totp_GenerateSecret_Returns_Base32_String
- [ ] Totp_ValidateCode_HappyPath
- [ ] Totp_ValidateCode_Invalid_Code_Returns_False
- [ ] Totp_ValidateCode_Expired_Window_Returns_False
- [ ] Totp_ValidateCode_Allows_Skew_Window

**Existing coverage:** None identified (gap)

---

### Story 2.19: Seeder Tests
**Priority:** Low | **Effort:** Small | **Status:** [~]

Coverage for `Seeder.cs` and `TestDataSeeder.cs` — Database seeding.

**Test cases:**
- [x] Seeder_Creates_Default_Realm (existing in integration tests)
- [x] Seeder_Creates_Default_Scopes (existing in integration tests)
- [ ] Seeder_Idempotent_Does_Not_Duplicate_On_Rerun
- [ ] TestDataSeeder_Produces_Consistent_Test_Data

**Existing coverage:** `SeedUsageExamples` (partial)

---

### Story 2.20: Persistence & DbContext Tests
**Priority:** Medium | **Effort:** Medium | **Status:** [ ]

Coverage for `AuthDbContext.cs` and entity configurations.

**Test cases:**
- [ ] DbContext_Email_Normalization_On_Save
- [ ] DbContext_Unique_Constraints_Enforced
- [ ] DbContext_Navigation_Properties_Loaded_Correctly
- [ ] DbContext_Cascade_Delete_Configured
- [ ] DbContext_Indexes_Present_On_Frequently_Queried_Fields
- [ ] DbContext_Soft_Delete_Query_Filters_Applied

**Existing coverage:** `EmailNormalizationTests` (partial)

---

## Epic 3: MrWhoOidc.Security — Security Primitives

### Story 3.1: DPoP Validator Tests
**Priority:** High | **Effort:** Medium | **Status:** [x] ✅

Coverage for DPoP proof validation logic.

**Test cases:**
- [x] DPoP_Valid_Proof_Passes_Validation — Required claims and header structure
- [x] DPoP_Missing_Jti_Fails_Validation — jti claim required
- [x] DPoP_Missing_Htm_Fails_Validation — htm claim required
- [x] DPoP_Missing_Htu_Fails_Validation — htu claim required
- [x] DPoP_Htm_Mismatch_Fails_Validation — HTTP method must match
- [x] DPoP_Htu_Mismatch_Fails_Validation — HTTP URI must match
- [x] DPoP_JKT_Thumbprint_Calculation — Base64url JWK thumbprint
- [x] DPoP_JKT_Mismatch_Fails_Validation — Token binding enforcement
- [x] DPoP_Expired_Proof_Fails_Validation — Expired proofs rejected
- [x] DPoP_Invalid_Signature_Fails_Validation — Signature verification
- [x] DPoP_Nonce_Claim_Present_When_Required — Server nonce enforcement
- [x] DPoP_Jti_Replay_Prevention — Replay attack prevention
- [x] DPoP_Ath_Claim_For_Protected_Resource — Access token hash binding
- [x] DPoP_Proof_Type_Header_Required — typ="dpop+jwt"
- [x] DPoP_JWK_Header_Required — jwk header present

**Existing coverage:** `DPoPValidatorTests.cs` — 15 tests covering validation logic (208 total tests passing)

---

### Story 3.2: DPoP Proof Generator Tests
**Priority:** Medium | **Effort:** Small | **Status:** [ ]

Coverage for `DPoPProofGenerator.cs` — Client-side proof generation.

**Test cases:**
- [ ] DPoP_Generator_Creates_Valid_JWT_Proof
- [ ] DPoP_Generator_Includes_Jti_Htm_Htu
- [ ] DPoP_Generator_Includes_Iat_In_Proof
- [ ] DPoP_Generator_Signs_With_Private_Key
- [ ] DPoP_Generator_Jwk_Thumbprint_Matches_PublicKey
- [ ] DPoP_Generator_Ath_Included_When_AccessToken_Provided
- [ ] DPoP_Generator_Nonce_Included_When_Provided

**Existing coverage:** None identified (gap)

---

### Story 3.3: Crypto Primitives Tests
**Priority:** Low | **Effort:** Small | **Status:** [ ]

Coverage for `RsaJwk.cs` and `EcJwk.cs` — JWK serialization.

**Test cases:**
- [ ] RsaJwk_Serializes_Correctly_To_JSON
- [ ] RsaJwk_Deserializes_Correctly_From_JSON
- [ ] RsaJwk_Thumbprint_Matches_RFC7638
- [ ] EcJwk_Serializes_Correctly_To_JSON
- [ ] EcJwk_Deserializes_Correctly_From_JSON
- [ ] EcJwk_Thumbprint_Matches_RFC7638

**Existing coverage:** None identified (gap)

---

## Epic 4: Integration & E2E Scenarios

### Story 4.1: Authorization Code Flow E2E
**Priority:** High | **Effort:** Large | **Status:** [ ]

Full flow from authorize → token → userinfo.

**Test cases:**
- [ ] E2E_AuthCode_Flow_With_PKCE_S256
- [ ] E2E_AuthCode_Flow_With_Consent
- [ ] E2E_AuthCode_Flow_With_IdToken_And_AccessToken
- [ ] E2E_AuthCode_Flow_With_Refresh_Token
- [ ] E2E_AuthCode_Flow_UserInfo_Returns_Claims

**Existing coverage:** Partial in external OIDC tests

---

### Story 4.2: Client Credentials Flow E2E
**Priority:** Medium | **Effort:** Medium | **Status:** [~]

Full flow for client credentials grant.

**Test cases:**
- [x] E2E_ClientCredentials_With_ClientSecret (existing)
- [ ] E2E_ClientCredentials_With_ClientAssertion_JWT
- [ ] E2E_ClientCredentials_Introspection_Active

**Existing coverage:** `ClientCredentialsGrantStrategyTests` (partial)

---

### Story 4.3: Token Exchange Flow E2E
**Priority:** High | **Effort:** Large | **Status:** [~]

Full flow for RFC 8693 token exchange.

**Test cases:**
- [x] E2E_TokenExchange_JWT_Subject_To_JWT_Access (existing)
- [x] E2E_TokenExchange_With_DPoP_Ath_Binding (existing)
- [ ] E2E_TokenExchange_Opaque_Subject_To_JWT_Access
- [ ] E2E_TokenExchange_Multi_Hop_Delegation
- [ ] E2E_TokenExchange_Delegation_Depth_Cap_Reached
- [ ] E2E_TokenExchange_With_RequireSameJkt_Policy

**Existing coverage:** `TokenExchangeIntegrationTests` (good foundation)

---

### Story 4.4: Back-Channel Logout E2E
**Priority:** High | **Effort:** Large | **Status:** [ ]

Full flow for BCL from logout → outbox → dispatcher → RP receiver.

**Test cases:**
- [ ] E2E_BackChannel_Logout_RP_Initiated_Enqueues_Notifications
- [ ] E2E_BackChannel_Logout_Dispatcher_POSTs_To_RP
- [ ] E2E_BackChannel_Logout_RP_Validates_LogoutToken
- [ ] E2E_BackChannel_Logout_RP_Revokes_Session
- [ ] E2E_BackChannel_Logout_Retry_On_RP_Failure
- [ ] E2E_BackChannel_Logout_Circuit_Breaker_Opens

**Existing coverage:** None identified (critical gap)

---

### Story 4.5: Federated Logout E2E
**Priority:** Medium | **Effort:** Medium | **Status:** [ ]

Full flow for federated logout to upstream IdP.

**Test cases:**
- [ ] E2E_Federated_Logout_Invokes_Upstream_EndSession
- [ ] E2E_Federated_Logout_Upstream_Failure_Handled
- [ ] E2E_Federated_Logout_Multiple_Upstream_IdPs

**Existing coverage:** `FederatedLogoutServiceTests` (service layer only)

---

### Story 4.6: External IdP Chaining E2E
**Priority:** Medium | **Effort:** Medium | **Status:** [x]

Full flow for external OIDC provider chaining.

**Test cases:**
- [x] E2E_External_IdP_TwoProviders_HappyPath (existing)
- [x] E2E_External_IdP_Cancel_Flow (existing)

**Existing coverage:** `ExternalOidcIntegrationTests` (complete)

---

### Story 4.7: Refresh Token Rotation E2E
**Priority:** Medium | **Effort:** Medium | **Status:** [ ]

Full flow for refresh token rotation and reuse detection.

**Test cases:**
- [ ] E2E_RefreshToken_Rotation_Issues_New_Family
- [ ] E2E_RefreshToken_Reuse_Detection_Revokes_Family
- [ ] E2E_RefreshToken_Scope_Downgrade_Succeeds

**Existing coverage:** None identified (gap)

---

### Story 4.8: Multi-Realm Role Emission E2E
**Priority:** Medium | **Effort:** Medium | **Status:** [~]

Full flow for realm/role assignment and token emission.

**Test cases:**
- [x] E2E_Token_Includes_Realm_Roles_With_Roles_Scope (existing)
- [x] E2E_Token_Includes_Client_Roles_With_Roles_Scope (existing)
- [ ] E2E_UserInfo_Includes_Roles_With_Roles_Scope
- [ ] E2E_Token_Excludes_Roles_Without_Roles_Scope

**Existing coverage:** `MultiRealmRoleTests`, `TokenRoleEmissionTests` (partial)

---

## Epic 5: Security & Resilience Tests

### Story 5.1: Security Boundary Tests
**Priority:** Critical | **Effort:** Medium | **Status:** [x]

Tests ensuring security boundaries are not violated.

**Test cases:**
- [x] Security_Cross_Client_Token_Revocation_Blocked (completed 2025-10-02)
- [x] Security_Same_Client_Token_Revocation_Allowed (completed 2025-10-02)
- [x] Security_Cross_Realm_Role_Leakage_Prevented (completed 2025-10-02)
- [x] Security_Scope_Escalation_Prevented (completed 2025-10-02)
- [x] Security_Audience_Mismatch_Rejected (completed 2025-10-02)
- [x] Security_JWT_Algorithm_None_Rejected (completed 2025-10-02)
- [x] Security_PKCE_Downgrade_Attack_Prevented (completed 2025-10-02)
- [x] Security_Token_Audience_Isolation_Between_Clients (completed 2025-10-02)
- [x] Security_Client_Secret_Never_In_Logs (completed 2025-10-02)

**Existing coverage:** SecurityBoundaryTests.cs (9/9 test cases implemented) ✅

---

### Story 5.2: Input Validation & Error Handling
**Priority:** High | **Effort:** Medium | **Status:** [ ]

Tests for malformed inputs and error responses.

**Test cases:**
- [ ] Validation_Missing_Required_Params_Returns_Error
- [ ] Validation_Invalid_JSON_Returns_400
- [ ] Validation_SQL_Injection_Attempts_Rejected
- [ ] Validation_XSS_Payloads_Sanitized
- [ ] Validation_Excessively_Large_Payloads_Rejected
- [ ] Error_Responses_Do_Not_Leak_Internal_Details
- [ ] Error_Responses_RFC_Compliant_Format

**Existing coverage:** Partial in handler tests

---

### Story 5.3: Concurrency & Race Conditions
**Priority:** Medium | **Effort:** Medium | **Status:** [~]

Tests for concurrent operations.

**Test cases:**
- [ ] Concurrency_Parallel_Code_Redemption_Enforces_SingleUse
- [ ] Concurrency_Parallel_RefreshToken_Use_Detects_Reuse
- [ ] Concurrency_Key_Rotation_Does_Not_Duplicate_Keys (existing in stress test)
- [ ] Concurrency_PAR_Jti_Replay_Detection_Under_Load

**Existing coverage:** `JwksHistoryAndParStressTests` (partial)

---

### Story 5.4: Rate Limiting & DoS Protection
**Priority:** Medium | **Effort:** Medium | **Status:** [~]

Tests for rate limiting effectiveness.

**Test cases:**
- [x] RateLimit_Token_Exchange_Returns_429_After_Limit (existing)
- [ ] RateLimit_PAR_Returns_429_After_Limit
- [ ] RateLimit_Introspection_Returns_429_After_Limit
- [ ] RateLimit_Brute_Force_Login_Attempts_Blocked
- [ ] RateLimit_Retry_After_Header_Present_On_429

**Existing coverage:** `TokenExchangeRateLimiterTests`, `RateLimitHeadersIntegrationTests` (partial)

---

### Story 5.5: Replay Attack Prevention
**Priority:** High | **Effort:** Medium | **Status:** [~]

Tests for replay protections.

**Test cases:**
- [x] Replay_Authorization_Code_SingleUse_Enforced (existing)
- [x] Replay_PAR_Jti_Detected (existing)
- [ ] Replay_DPoP_Jti_Detected
- [ ] Replay_Client_Assertion_Jti_Detected
- [ ] Replay_RequestObject_Jti_Detected

**Existing coverage:** `AuthorizationCodeServiceTests`, `ParStoreTests` (partial)

---

## Epic 6: Observability & Diagnostics

### Story 6.1: Metrics & Telemetry Tests
**Priority:** Low | **Effort:** Small | **Status:** [~]

Tests verifying metrics are recorded.

**Test cases:**
- [x] Metrics_OIDC_Token_Issued_Counter_Incremented (existing)
- [x] Metrics_JWKS_Endpoint_Hit_Counter_Incremented (existing)
- [ ] Metrics_BackChannel_Dispatcher_Success_Counter
- [ ] Metrics_BackChannel_Dispatcher_Failure_Counter
- [ ] Metrics_Introspection_Request_Counter
- [ ] Metrics_Rate_Limit_Hit_Counter

**Existing coverage:** `PublicJwksMetricsTests` (partial)

---

### Story 6.2: Audit Logging Tests
**Priority:** Medium | **Effort:** Small | **Status:** [ ]

Tests verifying audit events are logged.

**Test cases:**
- [ ] Audit_Admin_Client_Changes_Logged
- [ ] Audit_Sensitive_Fields_Hashed_In_Logs
- [ ] Audit_User_Login_Success_Logged
- [ ] Audit_User_Login_Failure_Logged
- [ ] Audit_Token_Revocation_Logged
- [ ] Audit_BackChannel_Notification_Logged

**Existing coverage:** None identified (gap)

---

## Epic 7: Snapshot & Regression Tests

### Story 7.1: Surface Snapshot Tests
**Priority:** Medium | **Effort:** Small | **Status:** [x]

Tests ensuring public API surface stability.

**Test cases:**
- [x] Snapshot_Program_Endpoints_Stable (existing)
- [x] Snapshot_Rate_Limiting_Policies_Stable (existing)
- [x] Snapshot_Admin_Authorization_Policy_Stable (existing)

**Existing coverage:** `ProgramSurfaceSnapshotTests`, `ProgramEndpointsSnapshotTests`, `Phase0AugmentedSafetyTests` (complete)

---

## Implementation Notes

### Test Infrastructure Needs
- **In-memory database fixtures:** Extend existing TestServer patterns for handler tests
- **HTTP mocking:** Use existing `TestHandler` patterns from `MrWhoDiscoveryClientTests`
- **Time abstraction:** Introduce `ISystemClock` for testing time-based logic (expiry, rotation)
- **Secret management mocking:** Test key rotation and secret hashing without real KMS
- **Outbox polling simulation:** Mock background worker timers for backchannel dispatcher tests

### Priority Guidelines
- **Critical:** Security boundaries, token validation, BCL E2E
- **High:** Protocol handlers (token, authorize, logout), core services (token, JWT, authorize)
- **Medium:** Admin endpoints, introspection, PAR, auxiliary services
- **Low:** Metrics, crypto primitives, snapshot tests (already stable)

### Effort Estimates
- **Small:** 1-2 days (< 10 test cases)
- **Medium:** 3-5 days (10-20 test cases)
- **Large:** 1-2 weeks (> 20 test cases or complex integration)

### Test Patterns to Follow
- Use MSTest `[TestClass]`, `[TestMethod]`, `[TestCategory]`
- Follow existing patterns: `TokenExchangeIntegrationTests`, `ExternalOidcIntegrationTests`
- Use `TestServer` and `WebApplicationFactory<Program>` for HTTP endpoint tests
- Use in-memory EF Core databases with unique names per test class
- Mock external dependencies (HTTP clients, time, secrets)
- Assert both happy path and error conditions
- Include RFC compliance checks for OIDC/OAuth error responses

---

## Progress Tracking

**Total epics:** 7
**Total stories:** ~60
**Estimated effort:** ~120-150 developer days

**Completed stories:** 8 (Security Boundary, Introspection Service, DPoP Validator, UserInfo Handler, Authorize Handler, Token Handler, PAR Handler, Revocation Handler)
**In-progress stories:** 16-20 (foundation exists, needs expansion)
**Not started stories:** 26-30

**Latest completion (2025-10-02):**
- Story 5.1: Security Boundary Tests - 9/9 test cases implemented ✅ (ALL COMPLETE)
- Story 1.7: Introspection Service Tests - 12/12 test cases implemented ✅ (ALL COMPLETE)
- Story 3.1: DPoP Validator Tests - 15/15 test cases implemented ✅ (ALL COMPLETE)
- Story 1.5: UserInfo Handler Tests - 16/16 test cases implemented ✅ (ALL COMPLETE)
- Story 1.2: Authorize Handler Tests - 16/16 test cases implemented ✅ (ALL COMPLETE, 234 total tests)
- Story 1.3: Token Handler Tests - 13/13 handler-level tests implemented ✅ (ALL COMPLETE, 247 total tests)
- Story 1.4: PAR Handler Tests - 14/14 handler-level tests implemented ✅ (ALL COMPLETE, 261 total tests)
- Story 1.6: Revocation Handler Tests - 13/13 handler-level tests implemented ✅ (ALL COMPLETE, 274 total tests passing)

**Currently implementing:**
- (None — ready for next priority story)

**Next priorities:**
1. Story 2.11 (RefreshTokenService Tests) — no existing coverage, high priority
2. Story 4.4 (Back-Channel Logout E2E) — critical BCL validation
3. Story 1.1 (Discovery Handler Tests) — metadata endpoint validation
4. Story 1.8 (Logout Handler Tests) — session termination and federated logout

---

## Notes
- Tests should verify RFC compliance (error codes, response formats, required fields)
- Security tests should attempt boundary violations and verify rejection
- Integration tests should cover multi-hop scenarios (delegation chains, federated flows)
- All async operations should have cancellation token tests
- Database tests should verify constraints, indexes, and normalization
- Metrics/logging tests should verify structured output and PII handling
- Background worker tests should verify retry/backoff/circuit breaker behavior
- Admin endpoint tests should verify authorization and audit trails

**Maintainer:** AI Assistant | **Last updated:** 2025-10-02
