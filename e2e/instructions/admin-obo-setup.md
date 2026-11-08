# Page: Admin – On-Behalf-Of Setup
## Route: /admin/obo-setup

## Expectations
- Page header "OBO Setup Wizard" with icon and subtitle "Configure On-Behalf-Of (token exchange) for your application"
- Left-hand stepper card ("Setup Progress") listing the wizard steps:
  Setup Mode, UI Client (or Select Clients), API & Delegation, User Access, Review, Complete
- The current step is highlighted (active state); completed steps show a check badge
- First step (Setup Mode) shows:
  - An explanation of what OBO (RFC 8693 token exchange) is
  - Radio-card selection between starting from an existing client and creating a new UI client
  - A Continue/Next action to advance the wizard
- Later steps configure client selection, API delegation policy, user access and a review summary
- Back/Cancel navigation that returns to the admin area

## Actions
- Verify the wizard loads without errors on the first step
- Verify the stepper lists all steps with the first one active
- Verify the mode-selection cards are visible and selectable

## Visual Checks
- Stepper in a sticky card on the left with clear active/completed states
- Mode selection presented as cards, not a bare radio list
- Explanatory text is concise and names RFC 8693 token exchange
- Primary action button uses btn-primary
