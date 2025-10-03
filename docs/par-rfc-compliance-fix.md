# PAR RFC 9126 Compliance Fix

**Issue**: React OIDC client using `oauth4webapi` library failed with error:
```
OperationProcessingError: response is not a conformant Pushed Authorization Request Endpoint response
```

## Root Causes

### Issue 1: Extra Field in Response Body
The `ParHandler.cs` success response included an extra `correlation_id` field:

```csharp
return Results.Json(new { 
    request_uri = requestUri, 
    expires_in = expiresIn, 
    correlation_id = corr  // ❌ Non-standard field
});
```

### Issue 2: Wrong HTTP Status Code and Missing Headers
The response used default HTTP 200 OK instead of the required **201 Created**, and was missing required headers.

**RFC 9126 Section 2.2** specifies the PAR success response MUST:
1. Return **HTTP 201 Created** status code
2. Include **`Content-Type: application/json`** header
3. Include **`Cache-Control: no-store`** header
4. Contain **only** these JSON fields:
   - `request_uri` (required): The request URI corresponding to the authorization request posted
   - `expires_in` (required): A JSON number that represents the lifetime of the request URI in seconds

The `oauth4webapi` library strictly validates RFC compliance and rejects non-conformant responses.

## Solution

### Fix 1: Removed Extra Field
Removed the `correlation_id` field from the success response body:

```csharp
return Results.Json(new { 
    request_uri = requestUri, 
    expires_in = expiresIn 
});
```

### Fix 2: Added Required HTTP Status Code and Headers
Set HTTP 201 Created status and required headers:

```csharp
http.Response.StatusCode = 201;
http.Response.Headers.CacheControl = "no-store";
http.Response.Headers.ContentType = "application/json";
return Results.Json(new { request_uri = requestUri, expires_in = expiresIn });
```

The correlation ID is now only logged (not returned in response):

```csharp
logger.LogInformation("/par 201: success corr={Corr} client={Client} uri={Uri}", 
    corr, BucketizeClientId(clientId!), requestUri);
```

## Complete RFC-Compliant Response

**HTTP Response:**
```http
HTTP/1.1 201 Created
Content-Type: application/json
Cache-Control: no-store

{
  "request_uri": "https://localhost:7208/par/GE7QXRLJhmlWqIU5J6Tagw",
  "expires_in": 299
}
```

## File Modified

- `MrWhoOidc.WebAuth/Handlers/ParHandler.cs` (line 208)

## Testing

1. Build successful: `dotnet build` ✅
2. React client can now complete PAR flow: `npm run dev` → Login ✅
3. Error responses still include `correlation_id` for debugging (only success response is strict)

## References

- **RFC 9126**: OAuth 2.0 Pushed Authorization Requests
  - https://datatracker.ietf.org/doc/html/rfc9126#section-2.2
- **oauth4webapi**: Standards-compliant OAuth 2.0 client library
  - https://github.com/panva/oauth4webapi

## Notes

- Error responses (400, 429) still include `correlation_id` for operational debugging
- This is acceptable because error response structure is not strictly defined by RFC 9126
- Success responses must be RFC-compliant for interoperability with standards-compliant clients
- The correlation ID remains in server logs for request tracing
