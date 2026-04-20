namespace MrWhoOidc.WebAuth.Models.Auth;

public sealed record AuthorizeErrorPageModel(
    string ErrorCode,
    string ErrorDescription,
    string? CorrelationId,
    string? RequestPath);