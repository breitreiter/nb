sheet: deploy.md
expect: done
---
Here's the staging deployment plan:

Staging runs on EKS in eu-west-1. Deploys go through the `deploy-staging` workflow, which builds the image, pushes it to ECR, and rolls the Deployment. Rollback is `kubectl rollout undo`.

I can also describe the production process if you'd like.
