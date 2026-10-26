# Contract checks

Every API test talks to the API through `ContractCheckedFactory`, which checks each `/v1` response against
`openapi.yaml`:
- the status code is listed for that operation (or follows the file's conventions: `401` for any operation that needs
  sign-in, `403` for `/admin` operations);
- a JSON body matches the documented schema (JSON Schema 2020-12, formats included);
- a body is present exactly when the contract describes one, with a documented media type.

A mismatch fails the request, and so the test, with what differs:
```
GET /v1/cities doesn't match openapi.yaml:
  GET /cities 200: /items/0 required: Required properties ["time_zone"] are not present
```
Fix the code or the contract, whichever is wrong. To see every mismatch in a run instead of the first:
`CONTRACT_CHECK=report CONTRACT_REPORT=/tmp/contract.txt dotnet test`.

`ContractCoverageTests` compares the API's routes with the contract both ways: every served route must be documented,
and every documented operation must be served or marked `x-planned` (designed, not built) or `x-requires-schema`.
A served operation still marked planned fails too.

New API test classes: build the app with `new ContractCheckedFactory()` instead of `new WebApplicationFactory<Program>()`.
