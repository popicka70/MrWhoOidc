# Third-Party Dependency Reduction Plan

> **ARCHIVED DOCUMENT** - This is a historical planning document. Some changes may already be implemented.

This document outlines the plan to minimize third-party dependencies in `MrWhoOidc`, specifically targeting cryptographic and utility libraries that can be replaced with native .NET implementations or client-side logic.

## 1. Remove `BCrypt.Net-Next`

**Status:** High Priority / Low Risk  
**Description:** The Project references `BCrypt.Net-Next` but currently uses `Argon2` for password hashing. This dependency is unused and can be safely removed.

**Action Items:**
1.  **Remove Reference:** Delete `PackageReference Include="BCrypt.Net-Next"` from `MrWhoOidc.Auth/MrWhoOidc.Auth.csproj`.
2.  **Clean Code:**
    - Remove `public const string BCrypt = "bcrypt";` from `MrWhoOidc.Auth/Protocols/SecurityConstants.cs`.
    - Update comments in `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` regarding `SecretHash`.

---

## 2. Refactor QR Code Generation

**Status:** Medium Priority / Low Risk  
**Description:** Currently, `QRCoder` generates QR code images (for MFA/TOTP) on the server side. This adds a dependency and server load. We will move this rendering to the client side using JavaScript.

**Action Items:**
1.  **Remove Reference:** Delete `PackageReference Include="QRCoder"` from `MrWhoOidc.WebAuth/MrWhoOidc.WebAuth.csproj`.
2.  **JS Integration:**
    - Add a lightweight, dependency-free QR code library (e.g., `qrcode.js` or `kjua`) to `MrWhoOidc.WebAuth/wwwroot/lib/`.
3.  **Refactor Service:**
    - Modify `IQrCodeGenerator` to return the raw `otpauth://` URI string instead of a Base64 image data URI.
4.  **Update UI:**
    - Update `MrWhoOidc.WebAuth/Pages/Mfa/Index.cshtml` (and `Login.cshtml` for QR login) to receive the raw URI.
    - Add a `<div id="qr-container"></div>` placeholder.
    - Add a script block to render the QR code into the container using the raw URI.

---

## 3. Switch to Native PBKDF2

**Status:** High Priority / **High Impact**  
**Description:** Replace the third-party `Isopoh.Cryptography.Argon2` library with the native .NET `Microsoft.AspNetCore.Cryptography.KeyDerivation` (PBKDF2).
**Critical Warning:** **This change is breaking.** Existing password hashes stored in the database (hashed with Argon2) will no longer be verifiable. This requires a database reset or a mass password reset for all users.

**Action Items:**
1.  **Remove Reference:** Remove `PackageReference Include="Isopoh.Cryptography.Argon2"` from `MrWhoOidc.Auth/MrWhoOidc.Auth.csproj`.
2.  **Implement PBKDF2Hasher:**
    - Rewrite `MrWhoOidc.Auth/Services/PasswordHasher.cs`.
    - Use `System.Security.Cryptography.RandomNumberGenerator` for salts.
    - Use `KeyDerivation.Pbkdf2` with `KeyDerivationPrf.HMACSHA256`.
    - Recommended Settings:
        - Iterations: 600,000 (OWASP recommendation for HMAC-SHA256)
        - Salt Size: 128-bit (16 bytes)
        - Subkey Length: 256-bit (32 bytes)
3.  **Update Format Marker:**
    - Change the storage format to include the algorithm version if not already present, though for a hard switch, simply replacing the implementation is sufficient for new databases.

## Execution Order

1.  **Step 1:** Remove BCrypt (Immediate cleanup).
2.  **Step 2:** Refactor QR Code (requires UI work).
3.  **Step 3:** Switch Password Hashing (Coordinate with database reset).
