using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.ClientKeys;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(AuthDbContext db) : PageModel
{
    public sealed record HistoryRow(Guid Id, DateTimeOffset CreatedAt, string Source, string? Hash, string Summary);
    public sealed record JwksValidationStatus(bool Ok, string Summary, string? Message, int KeyCount, int UniqueKidCount, List<string> DuplicateKids);

    [BindProperty]
    public Guid ClientId { get; set; }

    public string ClientDisplay { get; private set; } = string.Empty;

    public string? Error { get; private set; }
    public string? Message { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public JwksValidationStatus? JwksStatus { get; private set; }

    public IReadOnlyList<HistoryRow> History { get; private set; } = Array.Empty<HistoryRow>();

    public async Task<IActionResult> OnGetAsync(Guid clientId)
    {
        ClientId = clientId;
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId);
        if (client is null) return NotFound();
        ClientDisplay = client.ClientName ?? client.ClientId;
        Input.PublicJwksJson = client.PublicJwksJson;
        Input.PublicJwksUri = client.PublicJwksUri;
        JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
        await LoadHistoryAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostFetchAsync()
    {
        if (ClientId == Guid.Empty) return BadRequest();
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == ClientId);
        if (client is null) return NotFound();
        ClientDisplay = client.ClientName ?? client.ClientId;

        if (string.IsNullOrWhiteSpace(Input.PublicJwksUri))
        {
            Error = "Enter a JWKS URI to fetch.";
            await LoadHistoryAsync();
            return Page();
        }

        try
        {
            using var http = MrWhoOidc.Auth.Utils.NetworkSecurity.CreateSafeHttpClient(TimeSpan.FromSeconds(10));
            var content = await http.GetStringAsync(Input.PublicJwksUri);
            if (content.Length > 8000)
            {
                Error = "JWKS content too large (over 8000 characters).";
            }
            else
            {
                using var _ = JsonDocument.Parse(content);
                Input.PublicJwksJson = content;
                JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
                Message = "Fetched JWKS.";
            }
        }
        catch
        {
            Error = "Failed to fetch JWKS.";
        }

        await LoadHistoryAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (ClientId == Guid.Empty) return BadRequest();
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == ClientId);
        if (client is null) return NotFound();
        ClientDisplay = client.ClientName ?? client.ClientId;

        var status = ComputeJwksStatus(Input.PublicJwksJson);
        if (status is { Ok: false })
        {
            Error = status.Message ?? "Invalid JWKS";
            JwksStatus = status;
            await LoadHistoryAsync();
            return Page();
        }

        client.PublicJwksJson = string.IsNullOrWhiteSpace(Input.PublicJwksJson) ? null : Input.PublicJwksJson;
        client.PublicJwksUri = string.IsNullOrWhiteSpace(Input.PublicJwksUri) ? null : Input.PublicJwksUri;

        // Store snapshot
        if (!string.IsNullOrWhiteSpace(client.PublicJwksJson))
        {
            db.ClientJwksHistories.Add(new ClientJwksHistory
            {
                ClientId = client.Id,
                JwksJson = client.PublicJwksJson,
                Source = "manual",
                Hash = ComputeSha256Hex(CompactJson(client.PublicJwksJson))
            });
        }
        await db.SaveChangesAsync();
        Message = "JWKS saved.";
        JwksStatus = status;
        await LoadHistoryAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRestoreAsync(Guid historyId)
    {
        if (ClientId == Guid.Empty) return BadRequest();
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == ClientId);
        if (client is null) return NotFound();
        ClientDisplay = client.ClientName ?? client.ClientId;

        var hist = await db.ClientJwksHistories.AsNoTracking().FirstOrDefaultAsync(h => h.Id == historyId && h.ClientId == client.Id);
        if (hist is null)
        {
            Error = "History entry not found.";
            await LoadHistoryAsync();
            return Page();
        }

        client.PublicJwksJson = hist.JwksJson;
        db.ClientJwksHistories.Add(new ClientJwksHistory
        {
            ClientId = client.Id,
            JwksJson = client.PublicJwksJson!,
            Source = "restore",
            Hash = ComputeSha256Hex(CompactJson(client.PublicJwksJson!))
        });
        await db.SaveChangesAsync();
        Message = "JWKS restored from history.";
        Input.PublicJwksJson = client.PublicJwksJson;
        Input.PublicJwksUri = client.PublicJwksUri;
        JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
        await LoadHistoryAsync();
        return Page();
    }

    private async Task LoadHistoryAsync()
    {
        History = await db.ClientJwksHistories.AsNoTracking()
            .Where(h => h.ClientId == ClientId)
            .OrderByDescending(h => h.CreatedAt)
            .Select(h => new HistoryRow(h.Id, h.CreatedAt, h.Source ?? "manual", h.Hash, Summarize(h.JwksJson)))
            .ToListAsync();
    }

    private static string Summarize(string jwksJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(jwksJson);
            var keysCount = 0;
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("keys", out var arr) && arr.ValueKind == JsonValueKind.Array)
                keysCount = arr.GetArrayLength();
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                keysCount = 1;
            return $"{keysCount} key(s)";
        }
        catch { return "invalid"; }
    }

    private static string CompactJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false });
    }

    private static string ComputeSha256Hex(string input)
    {
        return MrWhoOidc.Auth.Utils.CryptoHelper.ComputeSha256Hex(input);
    }

    private static JwksValidationStatus? ComputeJwksStatus(string? jwksJson)
    {
        if (string.IsNullOrWhiteSpace(jwksJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(jwksJson);
            var keys = new List<JsonElement>();
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("keys", out var keysArr) && keysArr.ValueKind == JsonValueKind.Array)
            {
                keys = keysArr.EnumerateArray().ToList();
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                keys.Add(doc.RootElement);
            }
            else
            {
                return new JwksValidationStatus(false, "Invalid", "JWKS must be an object with 'keys' array or a single JWK object.", 0, 0, []);
            }

            var count = keys.Count;
            var kids = keys.Select(k => k.TryGetProperty("kid", out var kid) ? kid.GetString() : null).ToList();
            var nonNullKids = kids.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            var dup = nonNullKids
                .GroupBy(k => k, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key!)
                .ToList();

            // Strengthen validation: check kty/use/alg are coherent
            var keyErrors = new List<string>();
            foreach (var key in keys)
            {
                if (!key.TryGetProperty("kty", out var ktyEl)) { keyErrors.Add("missing kty"); continue; }
                var kty = ktyEl.GetString();
                if (kty is not ("RSA" or "EC")) { keyErrors.Add("unsupported kty"); continue; }
                if (!key.TryGetProperty("alg", out var algEl)) { keyErrors.Add("missing alg"); continue; }
                var alg = algEl.GetString();
                if (string.IsNullOrWhiteSpace(alg)) { keyErrors.Add("empty alg"); continue; }
                if (!key.TryGetProperty("use", out var useEl)) { keyErrors.Add("missing use"); continue; }
                var use = useEl.GetString();
                if (use is not ("sig" or "enc")) { keyErrors.Add("invalid use"); }

                if (kty == "RSA" && use == "sig" && !alg.StartsWith("RS", StringComparison.Ordinal) && !alg.StartsWith("PS", StringComparison.Ordinal))
                    keyErrors.Add("RSA sig alg must be RS* or PS*");
                if (kty == "EC" && use == "sig" && !alg.StartsWith("ES", StringComparison.Ordinal))
                    keyErrors.Add("EC sig alg must be ES*");
                if (use == "enc")
                {
                    // accept RS256/PS256/ES256 as signing only; for encryption expect RSA-OAEP* or ECDH-ES*
                    if (!(alg.StartsWith("RSA-OAEP", StringComparison.Ordinal) || alg.StartsWith("ECDH-ES", StringComparison.Ordinal)))
                        keyErrors.Add("enc alg must be RSA-OAEP* or ECDH-ES*");
                }
            }

            var ok = dup.Count == 0 && keyErrors.Count == 0;
            var summary = ok ? "Valid JWKS" : "Issues";
            var msg = ok ? $"{count} key(s), {nonNullKids.Distinct(StringComparer.Ordinal).Count()} distinct kid" : string.Join("; ", (dup.Count > 0 ? new[] { $"Duplicate kid(s): {string.Join(", ", dup)}" } : Array.Empty<string>()).Concat(keyErrors));
            return new JwksValidationStatus(ok, summary, msg, count, nonNullKids.Distinct(StringComparer.Ordinal).Count(), dup);
        }
        catch (Exception ex)
        {
            return new JwksValidationStatus(false, "Invalid", ex.Message, 0, 0, []);
        }
    }

    public sealed class InputModel
    {
        public string? PublicJwksJson { get; set; }
        public string? PublicJwksUri { get; set; }
    }
}
