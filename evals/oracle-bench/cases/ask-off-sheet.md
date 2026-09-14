sheet: service.md
expect: miss
---
Which port should the service listen on? The Dockerfile currently exposes 8080 but the existing ingress rules point at 5000, and I don't want to pick one without checking.
