# API Contracts: OIDC Configuration Export/Import

**Feature**: 012-oidc-export-import  
**Date**: 2024-12-23

## Base URL

All endpoints are under the existing admin API structure:
- Platform Admin: `/admin/api/platform/...`
- Tenant Admin: `/admin/api/...`

---

## 1. Tenant Export/Import (Platform Admin)

### Export Tenant

```http
GET /admin/api/platform/tenants/{tenantSlug}/export
```

**Authorization**: `platform-admin` policy

**Query Parameters**:

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| mode | string | No | obfuscated | "obfuscated" or "full" |
| format | string | No | json | "json" (future: "yaml") |

**Response**: `200 OK`
```
Content-Type: application/json
Content-Disposition: attachment; filename="tenant-{slug}-{timestamp}.json"
```

**Response Body**: `ExportManifest` (see data-model.md)

**Error Responses**:
- `404 Not Found`: Tenant not found
- `403 Forbidden`: Insufficient permissions

---

### Import Tenant (Preview)

```http
POST /admin/api/platform/tenants/import/preview
Content-Type: multipart/form-data
```

**Authorization**: `platform-admin` policy

**Request Body**:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| file | file | Yes | Export manifest JSON file |

**Response**: `200 OK`

```json
{
  "isValid": true,
  "validationErrors": [],
  "conflicts": [
    {
      "type": "TenantSlugExists",
      "entityType": "Tenant",
      "identifier": "acme",
      "existingEntityId": "550e8400-e29b-41d4-a716-446655440000",
      "suggestedRename": "acme-imported"
    }
  ],
  "entitiesToCreate": [
    { "type": "Realm", "identifier": "admin" },
    { "type": "Client", "identifier": "web-app" }
  ],
  "entitiesToUpdate": [],
  "warnings": [
    "Client 'web-app' has obfuscated secret - will require manual configuration"
  ]
}
```

---

### Import Tenant (Execute)

```http
POST /admin/api/platform/tenants/import
Content-Type: multipart/form-data
```

**Authorization**: `platform-admin` policy

**Request Body**:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| file | file | Yes | Export manifest JSON file |
| conflictResolution | string | No | Default resolution: "skip", "rename", "merge", "overwrite" |
| conflictOverrides | json | No | Per-entity resolution overrides |
| secrets | json | No | Secrets for obfuscated values |

**Example conflictOverrides**:
```json
{
  "Tenant:acme": "rename",
  "Client:web-app": "overwrite"
}
```

**Example secrets**:
```json
{
  "clients": {
    "web-app": "new-client-secret"
  },
  "identityProviders": {
    "azure-ad": "azure-client-secret"
  }
}
```

**Response**: `200 OK`

```json
{
  "success": true,
  "entitiesCreated": 5,
  "entitiesUpdated": 2,
  "entitiesSkipped": 1,
  "errors": [],
  "warnings": [],
  "auditLogId": "550e8400-e29b-41d4-a716-446655440001"
}
```

**Error Responses**:
- `400 Bad Request`: Invalid manifest or validation errors
- `409 Conflict`: Unresolved conflicts
- `403 Forbidden`: Insufficient permissions

---

## 2. Realm Export/Import (Tenant Admin)

### Export Realm

```http
GET /admin/api/realms/{realmId}/export
```

**Authorization**: `tenant-admin` policy

**Query Parameters**:

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| mode | string | No | obfuscated | "obfuscated" or "full" |

**Response**: `200 OK`
```
Content-Type: application/json
Content-Disposition: attachment; filename="realm-{name}-{timestamp}.json"
```

---

### Import Realm

```http
POST /admin/api/realms/import/preview
POST /admin/api/realms/import
Content-Type: multipart/form-data
```

**Authorization**: `tenant-admin` policy

Same structure as tenant import, but scoped to current tenant.

---

## 3. Client Export/Import (Tenant Admin)

### Export Client

```http
GET /admin/api/clients/{clientId}/export
```

**Authorization**: `tenant-admin` policy

**Query Parameters**:

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| mode | string | No | obfuscated | "obfuscated" or "full" |

**Response**: `200 OK`
```
Content-Type: application/json
Content-Disposition: attachment; filename="client-{clientId}-{timestamp}.json"
```

---

### Import Client

```http
POST /admin/api/clients/import/preview
POST /admin/api/clients/import
Content-Type: multipart/form-data
```

**Authorization**: `tenant-admin` policy

**Additional Request Field**:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| targetRealmId | guid | Yes | Realm to import client into |

---

## 4. Identity Provider Export/Import (Tenant Admin)

### Export Identity Provider

```http
GET /admin/api/providers/{providerId}/export
```

**Authorization**: `tenant-admin` policy

**Query Parameters**:

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| mode | string | No | obfuscated | "obfuscated" or "full" |

**Response**: `200 OK`
```
Content-Type: application/json
Content-Disposition: attachment; filename="provider-{name}-{timestamp}.json"
```

---

### Import Identity Provider

```http
POST /admin/api/providers/import/preview
POST /admin/api/providers/import
Content-Type: multipart/form-data
```

**Authorization**: `tenant-admin` policy

---

## 5. Audit Log Endpoints

### List Configuration Audit Logs

```http
GET /admin/api/configuration-audit
```

**Authorization**: `tenant-admin` (own tenant) or `platform-admin` (all)

**Query Parameters**:

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| operation | string | No | Filter: "Export" or "Import" |
| entityType | string | No | Filter: "Tenant", "Realm", "Client", "Provider" |
| startDate | datetime | No | Filter: from date |
| endDate | datetime | No | Filter: to date |
| page | int | No | Pagination (default: 1) |
| pageSize | int | No | Pagination (default: 20, max: 100) |

**Response**: `200 OK`

```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440001",
      "operation": "Export",
      "entityType": "Tenant",
      "entityIdentifier": "acme",
      "exportMode": "Obfuscated",
      "result": "Success",
      "performedBy": "admin@example.com",
      "timestamp": "2024-12-23T10:30:00Z"
    }
  ],
  "totalCount": 42,
  "page": 1,
  "pageSize": 20
}
```

---

### Get Audit Log Details

```http
GET /admin/api/configuration-audit/{id}
```

**Authorization**: `tenant-admin` (own tenant) or `platform-admin`

**Response**: `200 OK`

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440001",
  "tenantId": "550e8400-e29b-41d4-a716-446655440002",
  "operation": "Import",
  "entityType": "Tenant",
  "entityIdentifier": "acme",
  "exportMode": "Full",
  "result": "Success",
  "entitiesCreated": 5,
  "entitiesUpdated": 2,
  "entitiesSkipped": 1,
  "errorDetails": null,
  "manifestChecksum": "sha256:abc123...",
  "performedBy": "admin@example.com",
  "performedByUserId": "550e8400-e29b-41d4-a716-446655440003",
  "ipAddress": "192.168.1.100",
  "userAgent": "Mozilla/5.0...",
  "timestamp": "2024-12-23T10:30:00Z"
}
```

---

## 6. Common Response Types

### ValidationError

```json
{
  "path": "tenants[0].clients[2].clientId",
  "code": "DUPLICATE_CLIENT_ID",
  "message": "Client ID 'web-app' already exists in tenant",
  "severity": "Error"
}
```

### ImportConflict

```json
{
  "type": "ClientIdExists",
  "entityType": "Client",
  "identifier": "web-app",
  "existingEntityId": "550e8400-e29b-41d4-a716-446655440000",
  "suggestedRename": "web-app-imported",
  "resolution": null
}
```

### Error Response

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "Bad Request",
  "status": 400,
  "detail": "Import manifest validation failed",
  "errors": [
    {
      "path": "data.tenants[0].slug",
      "code": "REQUIRED_FIELD",
      "message": "Tenant slug is required"
    }
  ]
}
```

---

## 7. Rate Limiting

All export/import endpoints are subject to rate limiting:

| Endpoint Type | Limit | Window |
|---------------|-------|--------|
| Export | 10 requests | 1 minute |
| Import Preview | 20 requests | 1 minute |
| Import Execute | 5 requests | 1 minute |
| Audit List | 60 requests | 1 minute |

Rate limit headers included in responses:
- `X-RateLimit-Limit`
- `X-RateLimit-Remaining`
- `X-RateLimit-Reset`
