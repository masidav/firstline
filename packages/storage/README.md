# Storage (`FandomScore.Storage`)

Stored files behind one seam, shared by the API (uploads, signed downloads) and the worker (`storage.cleanup`).

| File | Contents |
| --- | --- |
| `FileStore.cs` | `IFileStore` (put, read, delete; deleting a missing file is not an error), `StorageOptions` (`Storage` section), `LocalFileStore` (local disk). |
| `StorageSetup.cs` | `AddFileStore(configuration, contentRoot)`: registers the store for a process. |

`Storage__LocalPath` (default `.local-storage`, relative to the process's content root) must point the API and the
worker at the **same** directory, or the worker cannot delete what the API stored. Locally the API's default is
`services/api/.local-storage/`; set `Storage__LocalPath` to that absolute path for both when running the worker.

Production storage (the Cloud Storage quarantine bucket, ADR 0002) is a separate Phase 7 item: a second
`IFileStore` chosen in `AddFileStore`.
