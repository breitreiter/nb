# Building for distribution

Publish `nb.csproj` on its own, per platform:

```bash
dotnet publish nb.csproj -c Release -r linux-x64 --self-contained -o /out
```

nb's publish targets build the provider plugins and copy them into `providers/` next to
the binary, in the layout nb loads from. The providers are built without a runtime
identifier: a plugin runs on the host's runtime, so only nb itself is self-contained. If
the copy ever comes up empty the build says so (`No provider plugins found at …`). A
binary published past that warning can run nothing, so do not ship it.

Publish the project, not the solution. A solution-level publish builds the same
provider projects on other MSBuild nodes concurrently with nb's own publish targets, and
the two race over one output directory. CI (`.github/workflows/ci.yml`) publishes
`nb.csproj` per platform for that reason.

**Configuration.** The publish output does not include your `appsettings.json`; it
holds live API keys, and shipping it would ship them. You get
`appsettings.example.json` instead. Point the deployed binary at a real config with
`--config <path>`, or drop an `appsettings.json` next to the executable. It is optional
at load, and nb starts without one, with no providers configured.

**Signing.** Release binaries are not code-signed. Users will see SmartScreen on Windows
and Gatekeeper on macOS; the README's installation section says what to click.
