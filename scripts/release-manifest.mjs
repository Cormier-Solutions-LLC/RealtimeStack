import { writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export function releaseManifest(input) {
  const repository = /^[a-z0-9][a-z0-9.-]*(?::[0-9]+)?\/[a-z0-9][a-z0-9._/-]*$/;
  const digest = /^sha256:[a-f0-9]{64}$/;
  const hash = /^[a-f0-9]{64}$/;
  if (!/^[a-f0-9]{40}$/.test(input.sourceCommit) || !Number.isSafeInteger(input.ciRunId) || input.ciRunId < 1
    || !/^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$/.test(input.releaseVersion)
    || typeof input.versionRelease !== 'boolean' || !/^(github|harbor|dockerhub|generic)$/.test(input.credentialProvider)) throw Error('Invalid trusted release coordinates.');
  for (const image of [input, input.accounts]) {
    if (!image || !repository.test(image.image) || !digest.test(image.digest) || !hash.test(image.archiveSha256)) throw Error('Both tested immutable image archives are required.');
  }
  if (input.image === input.accounts.image || input.image.split('/')[0] !== input.accounts.image.split('/')[0]) throw Error('Account and gateway images need distinct repositories in the same authenticated registry.');
  return { schemaVersion: 3, releaseVersion: input.releaseVersion, versionRelease: input.versionRelease,
    sourceCommit: input.sourceCommit, image: input.image, credentialProvider: input.credentialProvider, digest: input.digest,
    archiveSha256: input.archiveSha256, ciRunId: input.ciRunId,
    accounts: { image: input.accounts.image, digest: input.accounts.digest, archiveSha256: input.accounts.archiveSha256, sourceCommit: input.sourceCommit } };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const e = process.env;
  const value = releaseManifest({ releaseVersion: e.RELEASE_VERSION, versionRelease: e.VERSION_RELEASE === 'true', sourceCommit: e.SOURCE_SHA,
    ciRunId: Number(e.CI_RUN_ID), image: e.GATEWAY_IMAGE, digest: e.GATEWAY_DIGEST, archiveSha256: e.GATEWAY_ARCHIVE_SHA256,
    credentialProvider: e.REGISTRY_CREDENTIAL_PROVIDER,
    accounts: { image: e.ACCOUNTS_IMAGE, digest: e.ACCOUNTS_DIGEST, archiveSha256: e.ACCOUNTS_ARCHIVE_SHA256 } });
  writeFileSync(process.argv[2] || 'artifacts/release-manifest.json', JSON.stringify(value, null, 2) + '\n');
}
