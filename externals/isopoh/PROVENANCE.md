# Vendored upstream snapshot — Isopoh.Cryptography.Argon2

- **Source:** https://github.com/mheyman/Isopoh.Cryptography.Argon2 (upstream `main`)
- **Pinned commit:** `cfcec8465ae181c6dfd209c3661896e53eff19ad` (2026-09-12, "Allow one
  hour for SignPath approval")
- **License:** CC-BY-4.0 — see [LICENSE](LICENSE). File headers additionally dedicate the
  code to the public domain worldwide.

## Why this exists

The released NuGet package `Isopoh.Cryptography.Argon2` **2.0.0** (Aug 2023) allocates a
fresh ~128 MiB `SecureArray` working buffer for *every* hash/verify — a pinned managed
`byte[]` on the Large Object Heap. Under the production OP's Argon2id profile
(m=131072 KiB, t=4, p=4) that caused repeated transient heap spikes and cgroup OOM kills
on the 512 MiB deployment. Upstream `main` at the commit above introduces
`Argon2Memory` — reusable working buffers intended exactly for "allocation-sensitive
server workloads" — but it has **not been published to NuGet**. This directory vendors
that snapshot so the deployment uses `ProjectReference` instead of the package.

## What is adapted

Nothing in the vendored sources (`*.cs`, `*.csproj`, `stylecop.json`) — they are
byte-identical to the upstream commit (see `externals/isopoh/.gitattributes` for
byte-exact preservation). Only the build environment is adapted, via files that do
**not** exist upstream:

- `Directory.Packages.props` — turns off central package management for these projects so
  the upstream csproj's own `PackageReference` versions resolve normally.
- `Directory.Build.targets` — turns off NuGet packing, symbol packages, and doc-file
  generation (irrelevant for a ProjectReference consumer); removes upstream's
  build-time analyzers (SonarAnalyzer/StyleCop/ConfigureAwaitChecker/SourceLink — heavy
  PrivateAssets that would add minutes to every clean Docker/CI build of a frozen
  snapshot). Runtime dependencies and signing are untouched: the vendored assemblies
  are delay-signed with the author's public key exactly like upstream's own builds
  (strong-name verification is not performed on .NET Core/.NET 10).

One additional upstream-repository-root file is vendored because the byte-exact
csprojs reference it:

- `isopoh-pub.snk` — the author's **public-only** strong-name key (160 bytes, checked
  in upstream). Used for delay-signing; contains no private material.

Outside this directory, `MrWhoOidc.slnx` lists the three vendored projects. This is
required: when MSBuild builds a **solution**, ProjectReferences to projects that are
not in the solution are built in the referenced project's *default* configuration
(Debug), which would compile the vendored crypto unoptimized inside Release CI/Docker
builds. Membership in the solution pins them to the solution configuration.

## Updating / replacing

1. Check whether upstream has published a NuGet release containing `Argon2Memory`
   (`dotnet nuget search` / nuget.org). If yes, switch `MrWhoOidc.Auth` back to
   `PackageReference`, delete this directory, and remove the three vendored projects
   from `MrWhoOidc.slnx`.
2. Otherwise: `git clone https://github.com/mheyman/Isopoh.Cryptography.Argon2`,
   `git checkout <commit>`, re-copy `lib/Isopoh.Cryptography.*` project directories plus
   `stylecop.json`, `LICENSE`, and `isopoh-pub.snk` over this directory, and update the
   pinned commit above. Verify `Argon2TestVector` upstream CI is green at that commit.

Reference test vectors for the vendored code are exercised in
`MrWhoOidc.UnitTests/Argon2VendoredTests.cs` (official PHC winner vectors + backward
compatibility with hashes produced by NuGet 2.0.0).
