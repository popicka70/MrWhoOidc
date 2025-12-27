using System;

namespace MrWhoOidc.Auth.Services.Authorization;

public record AuthorizeRequest(
    string? response_type = null,
    string? client_id = null,
    string? redirect_uri = null,
    string? scope = null,
    string? state = null,
    string? nonce = null,
    string? code_challenge = null,
    string? code_challenge_method = null,
    string? resource = null,
    string? response_mode = null,
    string? prompt = null,
    string? max_age = null,
    string? id_token_hint = null,
    string? login_hint = null,
    string? acr_values = null,
    string? display = null,
    string? ui_locales = null,
    string? claims = null
);
