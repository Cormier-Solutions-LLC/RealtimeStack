# CI configuration

Linux checks use the runner label configured in the repository variable `CI_RUNNER_LABEL`. The shared setup action is `.github/actions/setup-realtime-runner`.

Container tools and language fixtures use the `CI_*_IMAGE` repository variables referenced by `.github/workflows/ci.yml`. Set complete image references with a version and immutable SHA-256 digest. The release validation configuration uses upstream registries so an unavailable intermediary cache does not block unrelated checks. `CI_IMAGE_REGISTRY` configures the optional authenticated image registry; its credentials stay in repository secrets.

Changing a variable does not update an already-started job. Start a fresh validation run after changing runner or image configuration. Preserve the successful CI commit and its immutable package candidate for publication.

The CI workflow also supports manual dispatch on a selected ref. For a temporary shared-runner outage, `CI_RUNNER_LABEL` can select `ubuntu-24.04`; the same Linux prerequisite setup and complete validation matrix apply. Restore the normal runner label after the release. Publication still requires successful push CI on the exact main commit.
