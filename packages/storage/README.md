# Storage (`FandomScore.Storage`)

Stored files behind one seam, shared by the API (uploads, downloads) and the worker image (redaction,
`storage.cleanup`; in the cluster these run in the separate `redaction` Deployment, `infrastructure/README.md`).

| File | Contents |
| --- | --- |
| `FileStore.cs` | `IFileStore` (put, read, delete, signed read link; a missing file reads as null and deletes fine; failures are `IOException`), `StorageOptions` (`Storage` section), `LocalFileStore` (local disk). |
| `GcsFileStore.cs` | Cloud Storage: three buckets picked by key prefix, content type from the file's bytes, V4 signed read links. |
| `StorageSetup.cs` | `AddFileStore(configuration, contentRoot)`: registers the store for a process. |

Keys start with their tier: `quarantine/` (unredacted uploads), `archive/` (redacted proof only; the schema checks the
prefix), `media/` (staff-published media such as featured videos).

## Local disk (default)
`Storage__LocalPath` (default `.local-storage`, relative to the process's content root) must point the API and the
worker at the **same** directory, or the worker cannot read or delete what the API stored. Locally the API's default
is `services/api/.local-storage/`; set `Storage__LocalPath` to that absolute path for both when running the worker.

## Cloud Storage (`Storage__Provider=gcs`)
| Setting | Value |
| --- | --- |
| `Storage__QuarantineBucket`, `Storage__ArchiveBucket`, `Storage__MediaBucket` | Bucket names (`terraform output buckets`). |
| `Storage__EmulatorHost` | Optional, for tests and local runs: an emulator such as `http://localhost:4443`. |

On GKE the store uses the pod's Workload Identity service account (Application Default Credentials; no key files):
the API's, or the `redaction` Deployment's for redaction and `storage.cleanup`. The general `worker` identity has no
bucket access, so a job that uses `IFileStore` must be listed in the redaction Deployment's `Jobs__OnlyKinds`.
Download links are signed by that account through the IAM API, which needs `roles/iam.serviceAccountTokenCreator` on
itself (Terraform grants it to the API). Credentials that can't sign, like a developer's own gcloud login, still work:
the API then serves the bytes itself instead of redirecting.

Try it locally against the emulator the tests use:
```bash
docker run -d --name gcs -p 4443:4443 fsouza/fake-gcs-server:1.52 -scheme http -port 4443 -backend memory \
  -external-url http://localhost:4443
for b in dev-quarantine dev-archive dev-media; do curl -s -X POST localhost:4443/storage/v1/b -d "{\"name\":\"$b\"}" >/dev/null; done
Storage__Provider=gcs Storage__EmulatorHost=http://localhost:4443 Storage__QuarantineBucket=dev-quarantine \
  Storage__ArchiveBucket=dev-archive Storage__MediaBucket=dev-media dotnet run --project services/api
```
The emulator drops content types on upload (real Cloud Storage keeps them), and doesn't check link signatures.

## How the API uses it
- Uploads go through the API (`PUT /v1/uploads/{id}/content`), which strips photo metadata before storing, so an
  original with its GPS location is never written anywhere.
- `GET /v1/files/{id}` and `GET /v1/featured/videos/{id}` check the API's own link signature, then redirect to a
  15-minute storage link (`SignedReadUrlAsync`), or serve the bytes when the store can't sign (local disk).

Tests: `GcsFileStoreTests` (needs Docker; pulls `fsouza/fake-gcs-server`).
