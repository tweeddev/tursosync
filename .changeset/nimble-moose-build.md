---
type: fix
"TursoSync"
---

Fix enumerating a TursoDataReader (foreach / DbEnumerator): GetDataTypeName no longer throws InvalidEnumArgumentException on the Unknown value kind (which occurs while building schema before the first row); it now falls back to the declared column type.