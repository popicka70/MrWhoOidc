# Page: Admin – Settings
## Route: /admin/settings

## Expectations
- General tenant settings form must be present
- Settings such as tenant name, session timeout, token lifespans should be configurable
- Save button must be visible and functional

## Actions
- Verify all setting fields are rendered with current values
- Modify a safe non-breaking setting (e.g., the display name)
- Submit and verify a success notification

## CRUD Operations
### Update Settings
1. Identify a safe string field (tenant display name, email, etc.)
2. Modify its value
3. Click Save
4. Verify the success toast or message appears
5. Verify the value persists on page reload

## Visual Checks
- Settings should be logically grouped into sections with clear headings
- Numeric inputs (timeouts, limits) should clearly show units (minutes, seconds, KB)
- Toggle/checkbox settings should use consistent styling
- Destructive or irreversible settings should have a warning label or separate section
- Save button should clearly be the primary action on the page
