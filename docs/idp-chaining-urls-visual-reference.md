# IdP Chaining URLs - Visual Reference

## What the Admin Sees

### Before Clicking Copy Button

```
╔══════════════════════════════════════════════════════════════════════╗
║ 🔗 IdP Chaining Configuration URLs                                   ║
╠══════════════════════════════════════════════════════════════════════╣
║                                                                      ║
║ ℹ️ Use these tenant-aware URLs when configuring this instance as a   ║
║   downstream IdP in an identity provider chaining scenario.         ║
║                                                                      ║
║ ➡️ Authorization Endpoint (Login URL)                                ║
║ ┌────────────────────────────────────────────────┬─────────────────┐║
║ │ https://auth.example.com/t/acme/authorize      │  📋 Copy        │║
║ └────────────────────────────────────────────────┴─────────────────┘║
║ Use this URL as the authorization_endpoint in upstream IdP          ║
║ configuration.                                                       ║
║                                                                      ║
║ ⬅️ End Session Endpoint (Logout URL)                                 ║
║ ┌────────────────────────────────────────────────┬─────────────────┐║
║ │ https://auth.example.com/t/acme/connect/ends..│  📋 Copy        │║
║ └────────────────────────────────────────────────┴─────────────────┘║
║ Use this URL as the end_session_endpoint in upstream IdP            ║
║ configuration.                                                       ║
╚══════════════════════════════════════════════════════════════════════╝
```

### After Clicking Copy Button (Success)

```
╔══════════════════════════════════════════════════════════════════════╗
║ 🔗 IdP Chaining Configuration URLs                                   ║
╠══════════════════════════════════════════════════════════════════════╣
║                                                                      ║
║ ℹ️ Use these tenant-aware URLs when configuring this instance as a   ║
║   downstream IdP in an identity provider chaining scenario.         ║
║                                                                      ║
║ ➡️ Authorization Endpoint (Login URL)                                ║
║ ┌────────────────────────────────────────────────┬─────────────────┐║
║ │ https://auth.example.com/t/acme/authorize      │  ✅ Copied!    │║ ← Green
║ └────────────────────────────────────────────────┴─────────────────┘║
║ Use this URL as the authorization_endpoint in upstream IdP          ║
║ configuration.                                                       ║
║                                                                      ║
║ ⬅️ End Session Endpoint (Logout URL)                                 ║
║ ┌────────────────────────────────────────────────┬─────────────────┐║
║ │ https://auth.example.com/t/acme/connect/ends..│  📋 Copy        │║
║ └────────────────────────────────────────────────┴─────────────────┘║
║ Use this URL as the end_session_endpoint in upstream IdP            ║
║ configuration.                                                       ║
╚══════════════════════════════════════════════════════════════════════╝
```
*Button changes to green with checkmark for 1.5 seconds, then reverts*

### URL Examples by Deployment Mode

#### Single-Tenant Deployment
```
Authorization:  https://auth.example.com/authorize
End Session:    https://auth.example.com/connect/endsession
```

#### Multi-Tenant Deployment (Tenant: "acme")
```
Authorization:  https://auth.example.com/t/acme/authorize
End Session:    https://auth.example.com/t/acme/connect/endsession
```

#### Multi-Tenant Deployment (Tenant: "contoso")
```
Authorization:  https://auth.example.com/t/contoso/authorize
End Session:    https://auth.example.com/t/contoso/connect/endsession
```

#### Localhost Development
```
Authorization:  http://localhost:8443/authorize
End Session:    http://localhost:8443/connect/endsession
```

#### Localhost Multi-Tenant Development (Tenant: "dev")
```
Authorization:  http://localhost:8443/t/dev/authorize
End Session:    http://localhost:8443/t/dev/connect/endsession
```

## Complete Page Context

### Tab Navigation (Third Tab Selected)
```
┌─────────┬──────────────────┬───────────────┬─────────┬──────┐
│ General │ Redirect URIs    │ ⭐ Providers │ Scopes  │ Keys │ ...
└─────────┴──────────────────┴───────────────┴─────────┴──────┘
  
  [IdP Chaining Configuration URLs Card]  ← New section
  
  Add or update mapping
  [Provider selector and mapping controls]
  
  Current mappings
  [Table of provider mappings]
```

## User Interaction Flow

```
1. Admin clicks "Providers" tab
   ↓
2. Sees IdP Chaining URLs at top
   ↓
3. Clicks "Copy" button next to Authorization URL
   ↓
4. Button shows "✅ Copied!" (green, 1.5s)
   ↓
5. URL is in clipboard
   ↓
6. Admin pastes into upstream IdP config
   ↓
7. Repeats for End Session URL
   ↓
8. Configuration complete!
```

## HTML Structure (Simplified)

```html
<div class="card mb-3">
  <div class="card-header bg-info text-white">
    <i class="bi bi-link-45deg"></i> IdP Chaining Configuration URLs
  </div>
  <div class="card-body">
    <p>ℹ️ Use these tenant-aware URLs...</p>
    
    <!-- Authorization URL -->
    <label>➡️ Authorization Endpoint (Login URL)</label>
    <div class="input-group">
      <input type="text" readonly value="[URL]" id="authz-url" />
      <button onclick="copyToClipboard('authz-url', this)">
        📋 Copy
      </button>
    </div>
    <small>Use this URL as the authorization_endpoint...</small>
    
    <!-- End Session URL -->
    <label>⬅️ End Session Endpoint (Logout URL)</label>
    <div class="input-group">
      <input type="text" readonly value="[URL]" id="endsession-url" />
      <button onclick="copyToClipboard('endsession-url', this)">
        📋 Copy
      </button>
    </div>
    <small>Use this URL as the end_session_endpoint...</small>
  </div>
</div>
```

## CSS Classes Used

- `card` - Bootstrap card container
- `card-header` - Blue header section
- `bg-info` - Blue background color
- `text-white` - White text on blue
- `card-body` - Card content area
- `form-label` - Label styling
- `fw-bold` - Bold font weight
- `input-group` - Input with button group
- `form-control` - Bootstrap form styling
- `font-monospace` - Monospace font for URLs
- `btn` - Bootstrap button
- `btn-outline-secondary` - Outlined button style
- `btn-success` - Green success state
- `form-text` - Helper text
- `text-muted` - Gray helper text

## Icons Used (Bootstrap Icons)

- `bi-link-45deg` - Chain link icon (card header)
- `bi-info-circle` - Info icon (description)
- `bi-box-arrow-in-right` - Arrow in (authorization)
- `bi-box-arrow-right` - Arrow out (end session)
- `bi-clipboard` - Clipboard icon (copy button)
- `bi-check2` - Checkmark (success state)
- `bi-x-circle` - X mark (error state)

## Responsive Behavior

### Desktop (Wide Screen)
```
┌────────────────────────────────────────────┐
│ Full URLs visible with Copy button        │
└────────────────────────────────────────────┘
```

### Mobile (Narrow Screen)
```
┌──────────────────────┐
│ URL truncated...     │
│ [Copy button below]  │
└──────────────────────┘
```

## Accessibility Features

- ✅ Proper label associations
- ✅ Button title attributes for tooltips
- ✅ Semantic HTML structure
- ✅ Keyboard navigation support
- ✅ Screen reader friendly
- ✅ Visual feedback on interaction
- ✅ High contrast text/background

## Color Scheme

| Element                | Color          | Purpose           |
|------------------------|----------------|-------------------|
| Card header            | Info blue      | Visual prominence |
| URL input              | White/Gray     | Read-only field   |
| Copy button (normal)   | Outline gray   | Default state     |
| Copy button (success)  | Solid green    | Confirmation      |
| Copy button (error)    | Solid red      | Error indication  |
| Helper text            | Muted gray     | Secondary info    |

## Animation Timeline

```
State 1: Normal
[📋 Copy] (gray outline)
         ↓ User clicks
State 2: Copying (instant)
[⏳ ...] (processing)
         ↓ Success
State 3: Success (1.5 seconds)
[✅ Copied!] (green solid)
         ↓ Timeout
State 4: Back to Normal
[📋 Copy] (gray outline)
```

## Example Screenshot Representation

```
┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓
┃ Edit client                                              ┃
┣━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┫
┃                                                          ┃
┃ ┌───┬───┬────────────┬───────┬──────┬──────┬─────┬────┐┃
┃ │Gen│Rdr│◉ Providers │Scopes │ Keys │Intro │ M2M │OBO │┃
┃ └───┴───┴────────────┴───────┴──────┴──────┴─────┴────┘┃
┃                                                          ┃
┃ ╔════════════════════════════════════════════════════╗  ┃
┃ ║ 🔗 IdP Chaining Configuration URLs                ║  ┃
┃ ╠════════════════════════════════════════════════════╣  ┃
┃ ║                                                    ║  ┃
┃ ║ ℹ️ Use these tenant-aware URLs when configuring   ║  ┃
┃ ║   this instance as a downstream IdP...            ║  ┃
┃ ║                                                    ║  ┃
┃ ║ Authorization Endpoint (Login URL)                ║  ┃
┃ ║ ┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┳━━━━━━━━━━━━━━┓  ║  ┃
┃ ║ ┃https://auth.example.com/t/  ┃  📋 Copy    ┃  ║  ┃
┃ ║ ┃acme/authorize                ┃              ┃  ║  ┃
┃ ║ ┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┻━━━━━━━━━━━━━━┛  ║  ┃
┃ ║ Use this URL as the authorization_endpoint...  ║  ┃
┃ ║                                                    ║  ┃
┃ ║ End Session Endpoint (Logout URL)                 ║  ┃
┃ ║ ┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┳━━━━━━━━━━━━━━┓  ║  ┃
┃ ║ ┃https://auth.example.com/t/  ┃  📋 Copy    ┃  ║  ┃
┃ ║ ┃acme/connect/endsession       ┃              ┃  ║  ┃
┃ ║ ┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┻━━━━━━━━━━━━━━┛  ║  ┃
┃ ║ Use this URL as the end_session_endpoint...    ║  ┃
┃ ╚════════════════════════════════════════════════════╝  ┃
┃                                                          ┃
┃ Add or update mapping                                    ┃
┃ [Provider selector and controls]                         ┃
┃                                                          ┃
┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛
```

---

This visual reference shows exactly what administrators will see and interact with when using the IdP chaining URLs feature.
