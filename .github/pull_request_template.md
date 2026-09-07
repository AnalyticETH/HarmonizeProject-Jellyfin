## Change

<!-- Describe the behavior or release-contract change and its user impact. -->

## Verification

- [ ] I ran the relevant .NET build and tests with locked restore.
- [ ] I ran `dotnet format --verify-no-changes --no-restore` or explain why it is not applicable.
- [ ] I ran the relevant repository validators.
- [ ] I added or updated regression coverage for changed behavior.
- [ ] I updated README/CHANGELOG/API documentation when public behavior changed.
- [ ] I removed generated files, credentials, tokens, certificates, and private logs.

## Security and compatibility

- [ ] Configuration and status output remain credential-free.
- [ ] New bridge, FFmpeg, or request inputs remain bounded and fail closed.
- [ ] Jellyfin 10.9.0 ABI compatibility is preserved or the migration is documented.
