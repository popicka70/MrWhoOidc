# Phase 1: Data Model - URL Convention Standardization

**Feature**: System-Wide URL Convention Standardization to kebab-case  
**Date**: 2025-11-01  
**Status**: Not Applicable

## Overview

This feature does **not** involve any data model changes. URL routing is a presentation-layer concern handled entirely in the HTTP/UI layer (`MrWhoOidc.WebAuth`).

## Entity Analysis

**No new entities required.**  
**No existing entities modified.**

## Database Schema Changes

**None.** No EF Core migrations needed.

## Rationale

URL convention is a routing configuration concern:

- Routes defined in Razor Pages via `@page` directives
- Routes defined in Minimal APIs via `MapGet/MapPost/MapPut/MapDelete` strings
- No stored data affected (URLs are not persisted in database)
- Domain entities (`Client`, `IdentityProvider`, `User`, etc.) remain unchanged

**Exception**: Some entities may contain **stored** redirect URIs or callback URLs as configuration (e.g., `Client.RedirectUris`, `IdentityProvider.RedirectUri`). These are user-provided configuration values and do not need to change - external parties will update their stored URIs to match new convention.

## Validation Rules

**No changes to validation rules.** URL format validation remains unchanged.

## State Transitions

**No state machines affected.** This is a presentation-layer refactoring.
