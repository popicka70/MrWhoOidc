# Page: Admin – User Role Assignments
## Route: /admin/users/roles

## Expectations
- Page heading "Role assignments" with the target user's display name/email shown as a muted subtitle
- A tenant context banner (when applicable) noting that only realms, clients, and roles from
  the user's tenant can be assigned
- The user-detail sub-tab strip (Overview, Profile, Emails, Roles, Clients, Linked accounts...)
  with the Roles tab active
- Two inner tabs: "Realm Roles" (default active) and "Client Roles"
- Realm Roles tab: a "Select Realm" list-group with per-realm assigned-count badges;
  when a realm is selected: a role picker with checkboxes/multi-select, an assignment table
  (Realm, Role, Granted, Status, Actions) when assignments exist, and an Add/Remove flow
- Client Roles tab: a client selector and equivalent per-client role assignment UI
- Friendly empty states when no realms/clients/roles exist yet

## Actions
- Verify page loads without errors
- Verify the realm selector and both inner tabs are present
- Verify the user sub-tab strip renders with the Roles tab highlighted
- Verify the tenant banner or empty state is shown as appropriate

## Visual Checks
- Role names use code styling, statuses use color-coded badges
- Realm selector is a list-group with count badges, not a plain table
- This page is a SINGLE user's role-assignment editor: the searchable users list,
  "Add User" button, and user-table pagination are NOT expected here
