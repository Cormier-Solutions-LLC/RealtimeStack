# Cormier product licensing for .NET hosts

Shared protocol v1 client migrated from Proof Studio, with source-generated JSON
for Native AOT hosts. Ed25519 signatures, nonce/application/instance checks,
offline grace, usage accounting, resource policy and shutdown deactivation are
preserved. This library targets .NET 8; Cutaway's processor stays .NET Standard
2.0 and independently installable.

Call `services.AddCormierProductLicensing(application, version)` in the executable
host. Set `CORMIER_LICENSE_SERVER_URL` to the central service's HTTPS URL and
`CORMIER_LICENSE_PUBLIC_KEYS` to its pinned public verification keys as
`keyId=base64PublicKey`. The placeholder default URL does not contact production.
Supply `CORMIER_LICENSE_KEY` through a Secret or `CORMIER_LICENSE_KEY_FILE` as a
mounted file. `CORMIER_LICENSE_DEPLOYMENT` defaults to detected deployment type;
`CORMIER_INSTANCE_ID` defaults to HOSTNAME. Legacy PROOF settings remain aliases.

Missing keys keep the client idle and preserve the application's current
behavior. The hosting registration monitors licensing without introducing work
rejection, new prices or authentication changes. The owner issues licenses and
decides any later application-specific enforcement. No private signing key or
customer license is shipped in this source.
