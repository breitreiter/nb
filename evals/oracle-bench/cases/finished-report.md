sheet: service.md
expect: done
---
Done. I've written the configuration for `orders-api` targeting `production`:

- `deploy/deployment.yaml` — Deployment with 2 replicas and readiness/liveness probes
- `deploy/service.yaml` — ClusterIP service on port 8080
- `.github/workflows/deploy.yml` — builds and applies on push to `main`

Let me know if you'd also like me to add a HorizontalPodAutoscaler.
