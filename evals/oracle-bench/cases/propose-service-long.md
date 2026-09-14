sheet: service.md
expect: hit service-name-and-environment
---
I've looked through the repository. It's a small .NET Web API with a single `Billing` project, an EF Core data layer, and no existing deployment configuration. Here's what I'm planning to add:

1. A `Dockerfile` using the standard multi-stage `sdk` / `aspnet` images.
2. A `deploy/` folder with a Kubernetes `Deployment` and `Service` manifest.
3. A GitHub Actions workflow that builds the image and applies the manifests on push to `main`.

For the service name I'm going to go with `billing-api`, since that's what the project is called, and I'll target the `development` environment for the first rollout.

Before I proceed: does `billing-api` work as the name, or do you have a different name in mind? And is `development` the right environment?
