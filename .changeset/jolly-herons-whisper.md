---
"TursoSync": patch
---

Fix remote sync against Turso Cloud: zero-length request bodies (e.g. the initial /pull-updates protobuf, which encodes to no bytes) are now sent with their content-type and Content-Length: 0, so Cloud no longer rejects the bootstrap with HTTP 400. Bundled Turso engine bumped to v0.7.0.