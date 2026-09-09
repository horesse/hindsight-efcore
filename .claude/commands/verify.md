Run the full definition-of-done check and report the result honestly.

1. `dotnet build --configuration Release`
2. `dotnet format --verify-no-changes`
3. `dotnet test tests/Hindsight.Tests --configuration Release --no-build`
4. `dotnet test tests/Hindsight.IntegrationTests --configuration Release --no-build` (needs Docker; if Docker is unavailable say so explicitly instead of skipping silently)

If any step fails: paste the relevant error output, state the most likely cause in one or two
sentences, and propose the fix — do not apply it unless it is a formatting fix from step 2
(`dotnet format`), which you may apply directly.

If everything passes, reply with the one-line summary `build ✓ format ✓ unit ✓ integration ✓` and
the number of tests run.
