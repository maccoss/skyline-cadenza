# Release Notes

Per-version release notes for Skyline Cadenza.

## Versioning Scheme

Skyline Cadenza uses a `YY.feature.patch` versioning convention:

- **YY**: Two-digit year (e.g., `26` for 2026)
- **feature**: Incremented for each release containing new features
- **patch**: Incremented for bug-fix-only releases within the same feature version

Examples: `26.1.0` (first feature release of 2026), `26.1.1` (patch), `26.2.0` (second feature release).

The Skyline `tool-inf/info.properties` `Version` field is updated only at release time, not during development.

## File Format

Each release gets one file: `RELEASE_NOTES_v{version}.md`. During development, the unreleased draft lives in `RELEASE_NOTES_next.md` and is renamed at release time.

```text
release-notes/
  README.md                      # this file
  RELEASE_NOTES_next.md          # working draft for the next release
  RELEASE_NOTES_v26.1.0.md
  RELEASE_NOTES_v26.1.1.md
  RELEASE_NOTES_v26.2.0.md
```

## Writing Release Notes

### During Development

Append entries to `RELEASE_NOTES_next.md` as features and fixes land on `main`. This file is a working draft until the release is finalized.

### Content Structure

```markdown
# Skyline Cadenza v{version} Release Notes

One-sentence summary of the release.

## New Features

- Feature descriptions grouped by area (e.g., Scheduling, Ingest, UI)
- Focus on what changed from the user's perspective, not implementation details

## Bug Fixes

- Description of the bug and its impact
- What was fixed

## Performance

- Performance improvements with context (e.g., "Cut DIA-NN parquet load from 45 s to 8 s on 24k-precursor reports")

## Breaking Changes

- Any changes that require user action (parameter renames, removed options, etc.)
- Omit this section if there are no breaking changes
```

Sections can be omitted if empty.

### Style

- Write in past tense ("Added", "Fixed", "Removed")
- Lead with user impact, not implementation details
- Include specific numbers where relevant (precursor counts, runtime measurements)
- Reference parameters by their UI label, not the C# property name

## Release Process

1. Finalize `RELEASE_NOTES_next.md` on `main`.
2. Rename it: `git mv release-notes/RELEASE_NOTES_next.md release-notes/RELEASE_NOTES_v{version}.md`
3. Update the title heading inside the file to match the version.
4. Create a fresh empty `RELEASE_NOTES_next.md` for the following release (copy from this README's template above).
5. Bump `Version = {version}` in `src/SkylineCadenza.App/tool-inf/info.properties` so the Skyline tool installer reports the right version.
6. Commit: `git commit -m "Released v{version}."`
7. Tag: `git tag v{version}`
8. Push: `git push origin main --tags`
9. CI builds `SkylineCadenza.zip`, runs all tests, and publishes a GitHub Release with the curated release notes as the body and `SkylineCadenza-v{version}.zip` as the asset. The release workflow verifies that both the curated notes file and the `info.properties` version match the tag; if either is out of sync the release fails before publishing anything.
