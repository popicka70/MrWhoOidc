# Footer Overlap Fix - REVISED Simple Solution (Superseded)

**Date**: October 9, 2025  
**Issue**: Footer obstructing Save and Back buttons on Edit Client page  
**Status**: ⚠️ Superseded by grid-based page shell (Dec 2025)

## Update (Dec 2025)

This document describes an older mitigation based on adding bottom padding / per-page margins.
It has been replaced by a centralized, layout-level fix that uses a 3-row grid “page shell” (header / scrollable content / footer) so the footer is always visible and never overlaps content.

Current implementation lives in:

- `MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml`
- `MrWhoOidc.WebAuth/wwwroot/css/site.css` (classes: `.page-shell`, `.scroll-container`)
- `MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml.css` (removed absolute footer positioning)

## Problem

The original flexbox approach was making things worse, creating a huge footer area. The issue was over-engineering the solution.

## Final Simple Solution

### Changes Made

#### 1. CSS (`MrWhoOidc.WebAuth/wwwroot/css/site.css`)

**Body padding** - Added bottom padding to ensure space for footer:

```css
html {
  position: relative;
  min-height: 100%;
}

body {
  margin-bottom: 0;
  padding-bottom: 4rem; /* Space for footer */
}
```

**Footer** - Simplified, no fancy positioning:

```css
.footer {
  background-color: white;
  box-shadow: 0 -1px 3px rgba(0, 0, 0, 0.05);
  padding: 1rem 0;
  width: 100%;
}

.footer .container {
  padding-top: 0.5rem;
  padding-bottom: 0.5rem;
}
```

#### 2. Edit.cshtml

Added extra bottom margin to button container:

```html
<div class="mt-3 mb-5 d-flex gap-2">
    <button type="submit" class="btn btn-primary" asp-page-handler="Save">Save</button>
    <a asp-page="Index" class="btn btn-secondary">Back</a>
</div>
```

#### 3. Layout (`_Layout.cshtml`)

Reverted to standard Bootstrap container structure (no complex flexbox):

```html
<div class="container-fluid">
    <div class="row">
        <!-- content -->
    </div>
</div>
```

## How It Works

1. **Body has 4rem bottom padding** - Creates space at the bottom of every page
2. **Footer flows naturally** - No absolute/fixed positioning, just normal document flow
3. **Button container has mb-5** - Extra margin specifically for action buttons
4. **Simplified CSS** - No complex flexbox gymnastics

## Key Principle

**KISS (Keep It Simple, Stupid)** - The simpler solution is almost always better. Instead of complex flexbox layouts, just add enough padding to the body to ensure content never gets hidden by the footer.

## To Apply

1. **Stop the running application**
2. **Clear browser cache** or **hard refresh** (Ctrl+Shift+R)
3. **Restart the application**

The footer should now:

- ✅ Be compact and small
- ✅ Never overlap content
- ✅ Stay at bottom of page naturally
- ✅ Work on all viewport sizes
