# The demo's JAR signing key

`jar-signing-private.pem` is the RSA key pair that the RazorClient demo uses to sign its **JAR** request
objects (a signed authorization request, RFC 9101).

It is committed to the repository **on purpose**: the demo exists to demonstrate the mechanism, not to keep
a secret. The identity provider verifies a request object only against the key registered for the client
(it stores a hash of the client secret, so it cannot verify an HMAC/`HS256` signed request object), and the
seeder registers the matching public key for the demo client (`blazor-web`) - see
`MrWhoOidc.Auth/Services/Seeder.cs`.

Consequences worth knowing:

- Do **not** reuse this key anywhere. It signs requests for a local demo stack only.
- Rotating it means updating the public key in the seeder as well. The unit test
  `DemoClientJwksMatchesTheDemoKeyTests` fails if the two drift apart.

The public part of the key is published by the demo at `GET /.well-known/openid-client-jwks`, and the demo
computes the same JWK from the private key at start-up.
