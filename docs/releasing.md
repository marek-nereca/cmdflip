# Releasing

Only maintainers can publish releases. Pushing a version tag requires write access to the repo.

Versions follow [Semantic Versioning](https://semver.org). To publish a release:

1. Move the notes under `## [Unreleased]` in [CHANGELOG.md](../CHANGELOG.md) into a new
   `## [x.y.z] - YYYY-MM-DD` section, update the compare links at the bottom, and commit.
2. Tag and push: `git tag vx.y.z && git push origin vx.y.z`

GitHub Actions (`.github/workflows/build.yml`) builds the exe and installer, stamps the version
from the tag, and publishes a release with the changelog notes and SHA-256 checksums. The release
fails if CHANGELOG.md has no section for the version. Tags with a suffix (`v1.0.0-beta.1`) are
published as pre-releases.

To try the pipeline without a real release, push a pre-release tag (for example `v0.1.1-rc.1`),
then delete the release and the tag afterwards.
