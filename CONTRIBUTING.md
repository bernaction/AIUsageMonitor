# Contributing to AI Usage Monitor

Thank you for helping improve AI Usage Monitor. Bug reports, ideas, documentation improvements, and code contributions are welcome.

## Before you start

- Search existing issues and pull requests to avoid duplicates.
- Open an issue before beginning a large change or a change to public behavior.
- Never include account data, access tokens, `auth.json`, or other credentials in an issue, log, screenshot, commit, or pull request.

## Development setup

1. Fork the repository and clone your fork.
2. Create a focused branch from `main`.
3. Install the .NET 8 SDK or later on Windows.
4. Restore and build the project:

   ```powershell
   dotnet restore
   dotnet build --configuration Release
   ```

5. Run the app when the change affects behavior or layout:

   ```powershell
   dotnet run
   ```

## Pull requests

- Keep each pull request focused on one concern.
- Preserve the existing architecture and naming conventions.
- Keep source code, UI text, documentation, branch names, and commit messages in English.
- Add validation and error handling where appropriate.
- Update documentation when behavior or setup changes.
- Describe what changed, why it changed, and how it was verified.
- Link the related issue when one exists.
- Confirm that the project builds and that no generated files or secrets are included.

Maintainers may request changes before merging. Submitting a pull request means you agree that your contribution is licensed under the MIT License.
