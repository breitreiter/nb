sheet: service.md
expect: hit service-name-and-environment
---
## Proposed configuration

Based on the module name, I'm proposing the following:

| setting | value |
|---|---|
| service name | `billing-api` |
| environment | `development` |
| replicas | 2 |

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: billing-api
  labels:
    env: development
spec:
  replicas: 2
```

Please confirm the service name and environment before I write these files — I'd rather not guess at names that need to line up with what you already run elsewhere.
